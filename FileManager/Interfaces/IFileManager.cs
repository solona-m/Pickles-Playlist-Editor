using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace VfxEditor.FileManager.Interfaces {
    public enum ResetType {
        Reset,
        PluginClosing,
        ToDefault
    }

    public interface IFileManager : IFileManagerSelect {
        public bool DoDebug( string path );


        public bool FileExists( string path );

        public void WorkspaceImport( JObject meta, string loadLocation );

        public void WorkspaceExport( Dictionary<string, string> meta, string saveLocation );

        public IEnumerable<IFileDocument> GetDocuments();

        public string GetName();


        public void Reset( ResetType type );

        public static bool FileExist(IFileManager manager, string path) => File.Exists(path);

    }
}
