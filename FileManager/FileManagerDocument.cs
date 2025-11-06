using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using VfxEditor.FileManager.Interfaces;
using VfxEditor.Select;
using VfxEditor.Utils;

namespace VfxEditor.FileManager {
    public abstract class FileManagerDocument<R, S> : IFileDocument where R : FileManagerFile {
        public R File { get; protected set; }
        protected VerifiedStatus Verified => File == null ? VerifiedStatus.UNKNOWN : File.Verified;
        public bool Unsaved => File != null && File.Unsaved;

        protected string Name = "";
        public string DisplayName = string.Empty;
        protected bool Disabled = false;

        private string SourceTextInput = "";
        private string ReplaceTextInput = "";

        public string WriteLocation { get; protected set; }

        public abstract string Id { get; }
        public abstract string Extension { get; }

        protected readonly FileManagerBase Manager;

        protected DateTime LastUpdate = DateTime.Now;

        public FileManagerDocument( FileManagerBase manager, string writeLocation ) {
            Manager = manager;
            WriteLocation = writeLocation;
        }

        protected abstract R FileFromReader( BinaryReader reader, bool verify );

        protected void LoadLocal( string path, bool verify ) {
            if( !System.IO.File.Exists( path ) ) {
                return;
            }

            if( !path.EndsWith( $".{Extension}" ) ) {
                return;
            }

            try {
                using var reader = new BinaryReader( System.IO.File.Open( path, FileMode.Open ) );
                File?.Dispose();
                File = FileFromReader( reader, verify );
            }
            catch( Exception e ) {
            }
        }


        // =================


        // =====================

        protected void WriteFile( string path ) {
            if( File == null ) return;
            System.IO.File.WriteAllBytes( path, File.ToBytes() );
        }

        public void Update() {
            if( ( DateTime.Now - LastUpdate ).TotalSeconds <= 0.2 ) return;
            LastUpdate = DateTime.Now;

            File?.Update();

            var newWriteLocation = Manager.NewWriteLocation;
            WriteFile( newWriteLocation );
            WriteLocation = newWriteLocation;
        }

        // =======================


        public abstract S GetWorkspaceMeta( string newPath );

        public void WorkspaceExport( List<S> meta, string rootPath, string newPath ) {
            if( File == null ) return;

            var newFullPath = Path.Combine( rootPath, newPath );
            System.IO.File.WriteAllBytes( newFullPath, File.ToBytes() );
            meta.Add( GetWorkspaceMeta( newPath ) );
        }

        // ====== DRAWING ==========


        private static float DegreesToRadians( float degrees ) => MathF.PI / 180 * degrees;


        protected virtual void DrawExtraColumn() { }

        // ====== TEXT INPUTS ============


        // ==========================


        public virtual void Dispose() {
            File?.Dispose();
            File = null;
        }

        // ========================


        private static readonly string WarningText = "DO NOT modify movement abilities (dashes, backflips). Please read a guide before attempting to modify a .tmb or .pap file";

    }
}
