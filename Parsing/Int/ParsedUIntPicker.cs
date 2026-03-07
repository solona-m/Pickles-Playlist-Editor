using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Linq;

namespace VfxEditor.Parsing.Int {
    public class ParsedUIntPicker<T> : ParsedUIntHex where T : class {
        private readonly Func<List<T>> ListAction;
        private readonly Func<T, int, string> GetText;
        private readonly Func<T, uint> ToValue;

        public ParsedUIntPicker( string name, Func<List<T>> listAction, Func<T, int, string> getText, Func<T, uint> toValue ) : base( name, 4 ) {
            ListAction = listAction;
            GetText = getText;
            ToValue = toValue;
        }


        public T Selected {
            get {
                var items = ListAction.Invoke();
                if( items == null ) return null;

                return ToValue == null ?
                    ( ( Value < 0 || Value >= items.Count ) ? null : items[( int )Value] ) :
                    items.FirstOrDefault( x => ToValue( x ) == Value, null ); ;
            }
        }
    }
}