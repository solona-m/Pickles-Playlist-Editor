using Dalamud.Interface.Utility.Raii;
using System.IO;
using VfxEditor.Parsing;

namespace VfxEditor.ScdFormat.Sound.Data {
    public class SoundExtra {
        public readonly ParsedByte Version = new( "Version" );
        private byte Reserved1;
        private ushort Size = 0x10;
        public readonly ParsedInt PlayTimeLength = new( "Play Time Length" );
        private readonly ParsedReserve Reserve2 = new( 2 * 4 );

        public void Read( BinaryReader reader ) {
            Version.Read( reader );
            Reserved1 = reader.ReadByte();
            Size = reader.ReadUInt16();
            PlayTimeLength.Read( reader );
            Reserve2.Read( reader );
        }

        public void ApplyRecommended( int playTimeLengthMs ) {
            Version.Value = 2;
            Reserved1 = 0;
            Size = 0x10;
            PlayTimeLength.Value = playTimeLengthMs;
            Reserve2.SetBytes( [0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x79, 0x44] );
        }

        public void Write( BinaryWriter writer ) {
            Version.Write( writer );
            writer.Write( Reserved1 );
            writer.Write( Size );
            PlayTimeLength.Write( writer );
            Reserve2.Write( writer );
        }
    }
}
