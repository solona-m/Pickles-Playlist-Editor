using System.Collections.Generic;
using System.IO;
using VfxEditor.Utils;

namespace VfxEditor.FileManager {
    public abstract class FileManagerFile {
        public VerifiedStatus Verified = VerifiedStatus.WORKSPACE;
        public bool Unsaved { get; protected set; } = false;

        public FileManagerFile( ) {
           
        }

        public virtual void OnChange() {
            Unsaved = true;
        }

        public virtual void Update() {
            Unsaved = false;
        }
        public virtual void Dispose() { }
        public virtual List<string>? GetPapIds() => null;

        public virtual List<short>? GetPapTypes() => null;

        public abstract void Write( BinaryWriter writer );

        public byte[] ToBytes() {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter( ms );
            Write( writer );
            return ms.ToArray();
        }
    }
}
