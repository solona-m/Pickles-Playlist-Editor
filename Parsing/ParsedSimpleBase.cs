using System;

namespace VfxEditor.Parsing {
    public abstract class ParsedSimpleBase<T> : ParsedBase {
        public readonly string Name;

        protected bool InTable => Name.StartsWith( "##" );

        public T Value = default;
        public Action OnChangeAction;

        public ParsedSimpleBase( string name, T value ) : this( name ) {
            Value = value;
        }

        public ParsedSimpleBase( string name ) {
            Name = name;
        }
    }
}
