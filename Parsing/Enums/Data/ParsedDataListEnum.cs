using System;
using System.Collections.Generic;

namespace VfxEditor.Parsing.Data {
    public class ParsedDataListEnum<T, S> : ParsedEnum<T> where T : Enum where S : class {
        private readonly List<S> Items;

        public ParsedDataListEnum( List<S> items, string name, T value, int size = 4 ) : base( name, value, size ) {
            Items = items;
        }

        public ParsedDataListEnum( List<S> items, string name, int size = 4 ) : base( name, size ) {
            Items = items;
        }
    }
}
