using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VfxEditor.FileManager;
using VfxEditor.Formats.ScdFormat.Utils;
using VfxEditor.ScdFormat.Music;
using VfxEditor.ScdFormat.Music.Data;
using VfxEditor.Utils;

namespace VfxEditor.ScdFormat {
    public class ScdFile : FileManagerFile {
        private readonly ScdHeader Header;

        public readonly List<ScdAudioEntry> Audio = [];
        public readonly List<ScdSoundEntry> Sounds = [];
        private List<ScdLayoutEntry> Layouts => Sounds.Select( x => x.Layout ).ToList();
        public readonly List<ScdTrackEntry> Tracks = [];
        public readonly List<ScdAttributeEntry> Attributes = [];


        private readonly short UnknownOffset;
        private readonly int EofPaddingSize;

        public ScdFile( BinaryReader reader, bool verify ) : base() {
            Header = new( reader );
            var offsets = new ScdReader( reader );
            UnknownOffset = offsets.UnknownOffset;
            EofPaddingSize = offsets.EofPaddingSize;

            // The acutal sound effect/music data
            foreach( var offset in offsets.AudioOffsets.Where( x => x != 0 ) ) {
                var newAudio = new ScdAudioEntry( this );
                newAudio.Read( reader, offset );
                Audio.Add( newAudio );
            }

            var layouts = new List<ScdLayoutEntry>();
            foreach( var offset in offsets.LayoutOffsets.Where( x => x != 0 ) ) {
                var newLayout = new ScdLayoutEntry();
                newLayout.Read( reader, offset );
                layouts.Add( newLayout );
            }

            foreach( var offset in offsets.TrackOffsets.Where( x => x != 0 ) ) {
                var newTrack = new ScdTrackEntry();
                newTrack.Read( reader, offset );
                Tracks.Add( newTrack );
            }

            foreach( var offset in offsets.AttributeOffsets.Where( x => x != 0 ) ) {
                var newAttribute = new ScdAttributeEntry();
                newAttribute.Read( reader, offset );
                Attributes.Add( newAttribute );
            }

            foreach( var (offset, index) in offsets.SoundOffsets.Where( x => x != 0 ).WithIndex() ) {
                var newSound = new ScdSoundEntry( layouts[index] );
                newSound.Read( reader, offset );
                Sounds.Add( newSound );
            }

            if( verify ) Verified = FileUtils.Verify( reader, ToBytes() );
            if( offsets.Modded || Audio.Any( x => x.Data is ScdVorbis vorbis && vorbis.LegacyImported ) ) {
                Verified = VerifiedStatus.UNSUPPORTED;
            }
        }


        public override void Write( BinaryWriter writer ) {
            Header.Write( writer );

            writer.Write( ( short )Sounds.Count );
            writer.Write( ( short )Tracks.Count );
            writer.Write( ( short )Audio.Count );
            writer.Write( UnknownOffset );

            var placeholders = writer.BaseStream.Position;
            writer.Write( 0 ); // track
            writer.Write( 0 ); // audio
            writer.Write( 0 ); // layout
            writer.Write( 0 ); // routing
            writer.Write( 0 ); // attribute
            writer.Write( EofPaddingSize );

            var soundOffset = PopulateOffsetPlaceholders( writer, Sounds, false );
            var trackOffset = PopulateOffsetPlaceholders( writer, Tracks, false );
            var audioOffset = PopulateOffsetPlaceholders( writer, Audio, false );
            var layoutOffset = PopulateOffsetPlaceholders( writer, Layouts, false );
            var attributeOffset = PopulateOffsetPlaceholders( writer, Attributes, true );

            // Update placeholders
            var savePos = writer.BaseStream.Position;
            writer.BaseStream.Position = placeholders;
            writer.Write( trackOffset );
            writer.Write( audioOffset );
            writer.Write( layoutOffset );
            writer.Write( 0 ); // routing
            writer.Write( attributeOffset );
            writer.BaseStream.Position = savePos;

            UpdateOffsets( writer, Sounds.Select( x => x.Layout ).ToList(), layoutOffset, ( bw, item ) => {
                item.Write( writer );
            } );
            FileUtils.PadTo( writer, 16 );

            UpdateOffsets( writer, Sounds, soundOffset, ( bw, item ) => {
                item.Write( writer );
            } );
            FileUtils.PadTo( writer, 16 );

            UpdateOffsets( writer, Tracks, trackOffset, ( bw, item ) => {
                item.Write( writer );
            } );
            FileUtils.PadTo( writer, 16 );

            UpdateOffsets( writer, Attributes, attributeOffset, ( bw, item ) => {
                item.Write( writer );
            } );
            FileUtils.PadTo( writer, 16 );

            // Sounds
            long paddingSubtract = 0;
            UpdateOffsets( writer, Audio, audioOffset, ( bw, music ) => {
                music.Write( writer, out var padding );
                paddingSubtract += padding;
            } );
            if( ( paddingSubtract % 16 ) > 0 ) paddingSubtract -= paddingSubtract % 16;

            ScdHeader.UpdateFileSize( writer, paddingSubtract ); // end with this
        }

        public void Replace( ScdAudioEntry entry, ScdAudioEntry newEntry ) {
            var index = Audio.IndexOf( entry );
            if( index == -1 || entry == newEntry || entry == null || newEntry == null ) return;
            Audio.Remove( entry );
            Audio.Insert( index, newEntry );
        }

        public void Dispose() => Audio.ForEach( x => x.Dispose() );

        public static ScdFile Import(string path)
        {
            BinaryReader reader = null;
            try
            {
                if (path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                {
                    reader = new BinaryReader(System.IO.File.Open(path, FileMode.Open));

                    var scdFile = new ScdFile(reader, false);
                    return scdFile;
                }

                reader = new BinaryReader(System.IO.File.Open(Path.Combine(Directory.GetCurrentDirectory(), "default.scd"), FileMode.Open));
                var file = new ScdFile(reader, false);
                var oldEntry = file.Audio[0];

                ScdAudioEntry newEntry = null;
                switch (Path.GetExtension(path))
                {
                    case ".ogg":
                        newEntry = ScdVorbis.ImportOgg(path, oldEntry);
                        break;
                    case ".wav":
                        newEntry = ScdVorbis.ImportWav(path, oldEntry);
                        break;
                    default:
                        newEntry = ScdVorbis.Importmp3(path, oldEntry);
                        break;
                }

                if (newEntry != null)
                {
                    file.Audio.Clear();
                    file.Audio.Add(newEntry);
                    file.Attributes[0].Version.Value = 1;
                    file.Attributes[0].ConditionFirst.Value = 0;
                }
                else
                {
                    throw new Exception("couldn't import ogg for " + path);
                }

                file.Sounds[0].BusDucking.FadeTime.Value = 1200;
                file.Sounds[0].BusDucking.Volume.Value = 0f;
                file.Sounds[0].BusDucking.Number.Value = 1;
                return file;

            }
            finally
            {
                if (reader != null)
                    reader.Dispose();
            }
        }

        private static int PopulateOffsetPlaceholders<T>( BinaryWriter writer, List<T> items, bool defaultZero ) {
            if( items.Count == 0 ) return defaultZero ? 0 : ( int )writer.BaseStream.Position;
            var offset = writer.BaseStream.Position;
            foreach( var _ in items ) writer.Write( 0 );
            FileUtils.PadTo( writer, 16 );
            return ( int )offset;
        }

        private static void UpdateOffsets<T>( BinaryWriter writer, List<T> items, int offsetLocation, Action<BinaryWriter, T> action ) where T : ScdEntry {
            List<int> positions = [];
            foreach( var item in items ) {
                positions.Add( ( int )writer.BaseStream.Position );
                action.Invoke( writer, item );
            }
            var savePos = writer.BaseStream.Position;

            writer.BaseStream.Position = offsetLocation;
            foreach( var position in positions ) {
                writer.Write( position );
            }

            writer.BaseStream.Position = savePos;
        }
    }
}
