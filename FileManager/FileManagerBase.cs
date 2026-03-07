
using VfxEditor.FileManager.Interfaces;
using VfxEditor.Select;
using VfxEditor.Ui;

namespace VfxEditor.FileManager {
    public abstract class FileManagerBase :  IFileManagerSelect {
        public readonly string Id;
        public readonly string Title;
        public readonly string Extension;
        public readonly string WorkspaceKey;
        public readonly string WorkspacePath;


        public abstract string NewWriteLocation { get; }

        protected FileManagerBase( string title, string id, string extension, string workspaceKey, string workspacePath )  {

            Title = title;
            Extension = extension;
            WorkspaceKey = workspaceKey;
            WorkspacePath = workspacePath;
            Id = id;
        }

        public string GetId() => Id;

        public string GetName() => Id.ToLower();
    }
}
