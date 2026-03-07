using Dalamud.Bindings.ImGui;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.IO;
using VfxEditor.FileManager.Interfaces;
using VfxEditor.Formats.ScdFormat.Utils;
using VfxEditor.Select;
using VfxEditor.Utils;

namespace VfxEditor.FileManager {
    public abstract partial class FileManager<T, R, S> : FileManagerBase, IFileManager where T : FileManagerDocument<R, S> where R : FileManagerFile {
        public T ActiveDocument { get; protected set; }
        public R? File => ActiveDocument?.File;

        private int DOC_ID = 0;
        public override string NewWriteLocation => Path.Combine(Plugin.RootLocation, $"{Id}Temp{DOC_ID++}.{Extension}" ).Replace( '\\', '/' );

        public readonly List<T> Documents = [];

        public FileManager( string title, string id ) : this( title, id, id.ToLower(), id, id ) { }

        public FileManager( string title, string id, string extension, string workspaceKey, string workspacePath ) : base( title, id, extension, workspaceKey, workspacePath ) {
            AddDocument();
        }

        // ===================


        // ====================

        protected abstract T GetNewDocument();

        public void AddDocument() {
            ActiveDocument = GetNewDocument();
            Documents.Add( ActiveDocument );
        }

        public void SelectDocument( T document ) {
            ActiveDocument = document;
        }

        public bool RemoveDocument( T document ) {
            Documents.Remove( document );
            document.Dispose();



            if( document == ActiveDocument ) {
                ActiveDocument = Documents[0];
                return true;
            }
            return false;
        }

        // ====================

        public IEnumerable<IFileDocument> GetDocuments() => Documents;

        public void WorkspaceImport( JObject meta, string loadLocation ) {
            var items = WorkspaceUtils.ReadFromMeta<S>( meta, WorkspaceKey );
            if( items == null || items.Length == 0 ) {
                AddDocument();
                return;
            }
            foreach( var item in items ) {
                var newDocument = GetWorkspaceDocument( item, Path.Combine( loadLocation, WorkspacePath ) );
                ActiveDocument = newDocument;
                Documents.Add( newDocument );
            }
        }

        protected abstract T GetWorkspaceDocument( S data, string localPath );

        public void WorkspaceExport( Dictionary<string, string> meta, string saveLocation ) {
            var rootPath = Path.Combine( saveLocation, WorkspacePath );
            Directory.CreateDirectory( rootPath );

            List<S> documentMeta = [];
            foreach( var (document, idx) in Documents.WithIndex() ) {
                document.WorkspaceExport( documentMeta, rootPath, $"{Id}Temp{idx}.{Extension}" );
            }

            WorkspaceUtils.WriteToMeta( meta, documentMeta.ToArray(), WorkspaceKey );
        }

        // ====================

        public bool FileExists( string path ) => IFileManager.FileExist( this, path );


        public bool DoDebug( string path ) => path.Contains( $".{Extension}" );

        public virtual void Reset( ResetType type ) {
            Documents.ForEach( x => x.Dispose() );
            Documents.Clear();

            ActiveDocument = null;

            if( type == ResetType.ToDefault ) AddDocument(); // Default document
        }
    }
}
