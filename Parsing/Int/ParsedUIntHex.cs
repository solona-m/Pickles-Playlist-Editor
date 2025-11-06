using Dalamud.Bindings.ImGui;

namespace VfxEditor.Parsing.Int {
    public class ParsedUIntHex : ParsedUInt {
        public ParsedUIntHex( string name ) : base( name ) { }

        public ParsedUIntHex( string name, uint value ) : base( name, value ) { }

    }
}