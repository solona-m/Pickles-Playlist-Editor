
using Dalamud.Bindings.ImGui;
using System;
using System.Linq;
using VfxEditor.Utils;

namespace VfxEditor.Parsing.Int {
    public class ParsedIntSelect<T> : ParsedInt where T : class {
        private readonly Func<T, int> ToValue;
        private readonly Func<T, int, string> GetText;
        private readonly int DefaultValue;

        public T Selected => null ;


        public ParsedIntSelect(
            string name, int defaultValue,
             Func<T, int> toValue, Func<T, int, string> getText,
            int size = 4 ) : base( name, size ) {

            ToValue = toValue;
            GetText = getText;
            DefaultValue = defaultValue;
        }
    }
}
