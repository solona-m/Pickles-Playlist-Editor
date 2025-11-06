using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VfxEditor.Utils;

namespace VfxEditor.Parsing {
    public class ParsedStringIcon {
        public bool Remove;
        public Action<string> Action;
    }

    public class ParsedString : ParsedSimpleBase<string> {
        public readonly List<ParsedStringIcon> Icons;
        public bool HasIcons => Icons?.Count > 0;
        private readonly bool ForceLowerCase;

        private bool Editing = false;
        private DateTime LastEditTime = DateTime.Now;
        private string StateBeforeEdit = "";

        public ParsedString( string name, string value ) : base( name, value ) { }

        public ParsedString( string name, List<ParsedStringIcon> icons = null, bool forceLower = false ) : base( name ) {
            Value = "";
            Icons = icons ?? [];
            ForceLowerCase = forceLower;
        }

        public override void Read( BinaryReader reader ) => Read( reader, 0 );

        public override void Read( BinaryReader reader, int size ) {
            Value = FileUtils.ReadString( reader );
        }

        public override void Write( BinaryWriter writer ) => FileUtils.WriteString( writer, Value, writeNull: true );

    }
}
