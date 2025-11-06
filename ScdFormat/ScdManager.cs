using System.IO;
using VfxEditor.FileManager;
using VfxEditor.FileManager.Interfaces;
using VfxEditor.Formats.ScdFormat.Utils;

using VfxEditor.Utils;

namespace VfxEditor.Select.Formats {}
namespace VfxEditor.ScdFormat {
    public class ScdManager : FileManager<ScdDocument, ScdFile, WorkspaceMetaBasic> {
        public static string ConvertWav => Path.Combine( Plugin.Configuration.WriteLocation, "temp_out.wav" ).Replace( '\\', '/' );
        public static string ConvertOgg => Path.Combine( Plugin.Configuration.WriteLocation, "temp_out.ogg" ).Replace( '\\', '/' );

        public ScdManager() : base( "Scd Editor", "Scd" ) {
        }

        protected override ScdDocument GetNewDocument() => new( this, NewWriteLocation );

        protected override ScdDocument GetWorkspaceDocument( WorkspaceMetaBasic data, string localPath ) => new( this, NewWriteLocation, localPath, data );

        public override void Reset( ResetType type ) {
            File?.Dispose();
            base.Reset( type );
            ScdUtils.Cleanup();
        }
    }
}
