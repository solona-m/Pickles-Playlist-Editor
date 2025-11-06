using System.IO;
using VfxEditor.FileManager;
using VfxEditor.Utils;

namespace VfxEditor.ScdFormat {
    public class ScdDocument : FileManagerDocument<ScdFile, WorkspaceMetaBasic> {
        public override string Id => "Scd";
        public override string Extension => "scd";

        public ScdDocument( ScdManager manager, string writeLocation ) : base( manager, writeLocation ) { }

        public ScdDocument( ScdManager manager, string writeLocation, string localPath, WorkspaceMetaBasic data ) : this( manager, writeLocation ) {
        }

        protected override ScdFile FileFromReader( BinaryReader reader, bool verify ) => new( reader, verify );

        public override WorkspaceMetaBasic GetWorkspaceMeta( string newPath ) => new() {
            Name = Name,
            RelativeLocation = newPath,
            Disabled = Disabled
        };
    }
}
