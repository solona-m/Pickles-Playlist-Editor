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
                newSound.Read( reader, offset, GetNextEntryOffset( offsets, offset, Header.FileSize, reader.BaseStream.Length ) );
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
                    case ".mp3":
                    case ".m4a":
                    case ".flac":
                        newEntry = ScdVorbis.Importmp3(path, oldEntry);
                        break;
                    default:
                        newEntry = ScdVorbis.Importmp3(path, oldEntry);
                        break;
                }

                if (newEntry != null)
                {
                    file.Audio.Clear();
                    file.Audio.Add(newEntry);
                }
                else
                {
                    throw new Exception("couldn't import ogg for " + path);
                }

                file.ApplyCurrentSettings();

                return file;

            }
            finally
            {
                if (reader != null)
                    reader.Dispose();
            }
        }

        private static int GetNextEntryOffset( ScdReader offsets, int currentOffset, int fileSize, long streamLength ) {
            var next = offsets.SoundOffsets
                .Concat( offsets.TrackOffsets )
                .Concat( offsets.AudioOffsets )
                .Concat( offsets.LayoutOffsets )
                .Concat( offsets.AttributeOffsets )
                .Where( x => x > currentOffset )
                .DefaultIfEmpty( fileSize > currentOffset ? fileSize : ( int )streamLength )
                .Min();
            return next;
        }

        public void ApplyCurrentSettings() {
            if( Audio.Count == 0 ) return;
            ApplyRecommendedProfile( this, Audio[0] );
        }

        private static void ApplyRecommendedProfile( ScdFile file, ScdAudioEntry audio ) {
            var duration = GetAudioDuration( audio );
            var playLengthSamples = GetAudioSampleLength( duration, audio.SampleRate );

            audio.LoopStart = 0;
            audio.LoopEnd = Pickles_Playlist_Editor.Settings.LoopSongs ? audio.Data.TimeToBytes( ( float )duration.TotalSeconds ) : 0;
            if( Pickles_Playlist_Editor.Settings.ScdVersionShift ) audio.Flags.Value |= AudioFlag.Version_Shift;
            else audio.Flags.Value &= ~AudioFlag.Version_Shift;

            if( file.Attributes.Count > 0 ) ApplyRecommendedAttributeProfile( file.Attributes[0] );
            if( file.Sounds.Count > 0 ) {
                ApplyRecommendedSoundProfile( file.Sounds[0], duration );
                ApplyRecommendedLayoutProfile( file.Sounds[0].Layout );
            }
            if( file.Tracks.Count > 0 ) ApplyRecommendedTrackProfile( file.Tracks[0], playLengthSamples, Pickles_Playlist_Editor.Settings.LoopSongs );
        }

        private static TimeSpan GetAudioDuration( ScdAudioEntry audio ) {
            try {
                using var stream = audio.Data?.GetStream();
                return stream?.TotalTime ?? TimeSpan.Zero;
            }
            catch {
                return TimeSpan.Zero;
            }
        }

        private static int GetAudioSampleLength( TimeSpan duration, int sampleRate ) {
            if( duration <= TimeSpan.Zero || sampleRate <= 0 ) return 0;
            var samples = duration.TotalSeconds * sampleRate;
            return samples > int.MaxValue ? int.MaxValue : ( int )Math.Round( samples, MidpointRounding.AwayFromZero );
        }

        private static void ApplyRecommendedSoundProfile( ScdSoundEntry sound, TimeSpan duration ) {
            var attributes = SoundAttribute.Extra_Desc;
            if( Pickles_Playlist_Editor.Settings.LoopSongs ) attributes |= SoundAttribute.Loop;
            if( !Pickles_Playlist_Editor.Settings.FadeWithDistance ) attributes |= SoundAttribute.Fixed_Position;
            if( Pickles_Playlist_Editor.Settings.FadeBackgroundMusic ) attributes |= SoundAttribute.Bus_Ducking;

            sound.Type.Value = SoundType.Normal;
            sound.Attributes.Value = attributes;
            sound.Priority.Value = 0xC2;
            sound.Volume.Value = Pickles_Playlist_Editor.Settings.ScdVolumePercentage / 100f;
            sound.BusNumber.Value = Pickles_Playlist_Editor.Settings.FadeWithDistance ? ( byte )8 : ( byte )Pickles_Playlist_Editor.Settings.BusNumber;
            sound.LocalNumber.Value = 1;
            sound.UserId.Value = 0;
            sound.PlayHistory.Value = 0;

            sound.BusDucking.Number.Value = Pickles_Playlist_Editor.Settings.FadeBackgroundMusic ? 1 : 0;
            sound.BusDucking.FadeTime.Value = Pickles_Playlist_Editor.Settings.FadeBackgroundMusic ? 1200 : 0;
            sound.BusDucking.Volume.Value = Pickles_Playlist_Editor.Settings.FadeBackgroundMusic ? 0f : 1f;
            sound.Extra.ApplyRecommended( checked( ( int )Math.Round( duration.TotalMilliseconds, MidpointRounding.AwayFromZero ) ) );
            sound.TailPayload = new byte[12];

            sound.Tracks.Entries.Clear();
            var track = new SoundTrackInfo();
            track.TrackIdx.Value = 0;
            track.AudioIdx.Value = 0;
            sound.Tracks.Entries.Add( track );
            sound.RandomTracks.Entries.Clear();
        }

        private static void ApplyRecommendedLayoutProfile( ScdLayoutEntry layout ) {
            layout.Size = 0x80;
            if( layout.Type.Value != SoundObjectType.Point ) {
                layout.Type.Value = SoundObjectType.Point;
                layout.UpdateData();
            }

            layout.Version.Value = 1;
            layout.Flag1.Value = SoundObjectFlags1.First_Inactive | SoundObjectFlags1.Is_Little_Endian;

            if( Pickles_Playlist_Editor.Settings.FadeWithDistance && layout.GetData() is LayoutPointData layoutData ) {
                layoutData.MinRange.Value = 85f;
                layoutData.MaxRange.Value = 10f;
            }
        }

        private static void ApplyRecommendedAttributeProfile( ScdAttributeEntry attribute ) {
            attribute.Version.Value = 2;
            attribute.AttributeId.Value = 0x22;
            attribute.SearchAttributeId.Value = 0x22;
            attribute.ConditionFirst.Value = 1;
            attribute.ArgumentCount.Value = 1;
            attribute.SoundLabelLow.Value = 0;
            attribute.SoundLabelHigh.Value = 0;
            attribute.ResultFirst.SelfCommandSelect.Value = SelfCommand.None;
            attribute.ResultFirst.TargetCommandSelect.Value = TargetCommand.Stop;
            attribute.ResultFirst.SelfArgument.Value = 0;
            attribute.ResultFirst.TargetArgument.Value = 0;

            ApplyRecommendedAttributeExtend( attribute.Extend1, ( ConditionType1st )0x15, ConditionType2nd.EQ, 1, SelfCommand.NoPlay );
            ApplyRecommendedAttributeExtend( attribute.Extend2, ConditionType1st.Unknown, ConditionType2nd.EQ, 0, SelfCommand.None );
            ApplyRecommendedAttributeExtend( attribute.Extend3, ConditionType1st.Unknown, ConditionType2nd.EQ, 0, SelfCommand.None );
            ApplyRecommendedAttributeExtend( attribute.Extend4, ConditionType1st.Unknown, ConditionType2nd.EQ, 0, SelfCommand.None );
        }

        private static void ApplyRecommendedAttributeExtend( AttributeExtendData extend, ConditionType1st firstCondition, ConditionType2nd secondCondition, byte conditionCount, SelfCommand resultCommand ) {
            extend.FirstCondition.Value = firstCondition;
            extend.SecondCondition.Value = secondCondition;
            extend.JoinTypeSelect.Value = JoinType.And;
            extend.NumberOfConditions.Value = conditionCount;
            extend.SelfArgument.Value = 0;
            extend.TargetArgument_Int.Value = 0;
            extend.TargetArgument_Float.Value = 0f;
            extend.Result.SelfCommandSelect.Value = resultCommand;
            extend.Result.TargetCommandSelect.Value = TargetCommand.None;
            extend.Result.SelfArgument.Value = 0;
            extend.Result.TargetArgument.Value = 0;
        }

        private static void ApplyRecommendedTrackProfile( ScdTrackEntry track, int playLengthSamples, bool enableLoop ) {
            track.Items.Clear();
            track.Items.Add( CreateShortTrackItem( TrackCmd.Version, 11 ) );
            track.Items.Add( CreateIntTrackItem( TrackCmd.ReleaseRate, 0 ) );
            track.Items.Add( CreateModulationOffItem( OscillatorCarrier.Pan ) );
            track.Items.Add( CreateModulationOffItem( OscillatorCarrier.Pitch ) );
            track.Items.Add( CreateModulationOffItem( OscillatorCarrier.Volume ) );
            track.Items.Add( CreateParamTrackItem( TrackCmd.Volume, 1f, 0 ) );
            track.Items.Add( CreateParamTrackItem( TrackCmd.Pitch, 1f, 0 ) );
            track.Items.Add( CreateParamTrackItem( TrackCmd.Panning, 0f, 0 ) );
            track.Items.Add( CreateParamTrackItem( TrackCmd.FrPanning, 0f, 0 ) );
            track.Items.Add( CreateTrackItem( TrackCmd.ChannelVolumeZeroOne ) );
            track.Items.Add( CreateTrackItem( TrackCmd.KeyOn ) );

            if( enableLoop ) {
                track.Items.Add( CreateIntTrackItem( TrackCmd.Interval, 0 ) );
                track.Items.Add( CreateInt2TrackItem( TrackCmd.LoopStart, 0, 0 ) );
                track.Items.Add( CreateIntTrackItem( TrackCmd.Interval, playLengthSamples ) );
                track.Items.Add( CreateTrackItem( TrackCmd.LoopEnd ) );
            }
            else {
                track.Items.Add( CreateIntTrackItem( TrackCmd.Interval, playLengthSamples ) );
                track.Items.Add( CreateTrackItem( TrackCmd.KeyOff ) );
                track.Items.Add( CreateTrackItem( TrackCmd.End ) );
            }
        }

        private static ScdTrackItem CreateTrackItem( TrackCmd command ) {
            var item = new ScdTrackItem();
            item.Type.Value = command;
            item.UpdateData();
            return item;
        }

        private static ScdTrackItem CreateShortTrackItem( TrackCmd command, short value ) {
            var item = CreateTrackItem( command );
            ( ( TrackShortData )item.GetData() ).Value.Value = value;
            return item;
        }

        private static ScdTrackItem CreateIntTrackItem( TrackCmd command, int value ) {
            var item = CreateTrackItem( command );
            ( ( TrackIntData )item.GetData() ).Value.Value = value;
            return item;
        }

        private static ScdTrackItem CreateInt2TrackItem( TrackCmd command, int value1, int value2 ) {
            var item = CreateTrackItem( command );
            var data = ( TrackInt2Data )item.GetData();
            data.Value1.Value = value1;
            data.Value2.Value = value2;
            return item;
        }

        private static ScdTrackItem CreateParamTrackItem( TrackCmd command, float value, int time ) {
            var item = CreateTrackItem( command );
            var data = ( TrackParamData )item.GetData();
            data.Value.Value = value;
            data.Time.Value = time;
            return item;
        }

        private static ScdTrackItem CreateModulationOffItem( OscillatorCarrier carrier ) {
            var item = CreateTrackItem( TrackCmd.ModulationOff );
            ( ( TrackModulationOffData )item.GetData() ).Carrier.Value = carrier;
            return item;
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
