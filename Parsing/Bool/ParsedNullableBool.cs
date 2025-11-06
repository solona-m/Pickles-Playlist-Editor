using System;
using System.IO;
using System.Numerics;

namespace VfxEditor.Parsing {
    public class ParsedNullableBool : ParsedSimpleBase<bool?> {
        private int Size;

        public ParsedNullableBool( string name, bool value, int size = 4 ) : base( name, value ) {
            Size = size;
        }

        public ParsedNullableBool( string name, int size = 4 ) : base( name ) {
            Size = size;
        }

        public override void Read( BinaryReader reader ) => Read( reader, Size );

        public override void Read( BinaryReader reader, int size ) {
            var value = reader.ReadByte();
            Value = value switch {
                0x00 => false,
                0x01 => true,
                0xff => null,
                _ => null
            };
            Size = size;
        }

        public override void Write(BinaryWriter writer)
        {
            byte v = Value switch
            {
                true => 0x01,
                false => 0x00,
                null => 0xff
            };
            writer.Write(v);
            WritePad(writer, Size - 1);
        }

        public static void WritePad(BinaryWriter writer, int count)
        {
            for (var i = 0; i < count; i++) writer.Write((byte)0);
        }
    }
}
