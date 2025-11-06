using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;

namespace VfxEditor.Parsing.HalfFloat {
    public class ParsedHalf3Color : ParsedHalf3 {
        private bool Editing = false;
        private DateTime LastEditTime = DateTime.Now;
        private Vector3 StateBeforeEdit;

        public ParsedHalf3Color( string name ) : base( name ) { }

        public ParsedHalf3Color( string name, Vector3 value ) : base( name, value ) { }

    }
}
