using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using System.Numerics;
using VfxEditor.Utils;

namespace VfxEditor.FileManager {
    public abstract partial class FileManager<T, R, S> : FileManagerBase where T : FileManagerDocument<R, S> where R : FileManagerFile {
    

    }
}
