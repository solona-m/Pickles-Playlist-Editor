using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VfxEditor.Formats.ScdFormat.Utils
{
    internal class Plugin
    {
        public static string RootLocation = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + "\\ScdConverter";
        public class Configuration
        {
            public static string WriteLocation = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + "\\ScdConverter";
        }
    }
}
