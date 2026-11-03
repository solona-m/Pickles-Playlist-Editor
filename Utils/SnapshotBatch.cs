using System;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// One manifest snapshot for a run of saves the user didn't make one by one (the automatic stats run), so it
    /// can't push the user's recent snapshots out of the keep-20 window. Active only inside each synchronous save.
    /// </summary>
    internal sealed class SnapshotBatch
    {
        [ThreadStatic] private static SnapshotBatch? s_current;
        private bool _taken;

        public Scope Enter() => new(this);

        /// <summary>True when a snapshot should be skipped: inside a batch that has already taken its one.</summary>
        internal static bool SkipSnapshot()
        {
            var b = s_current;
            if (b == null) return false;
            if (b._taken) return true;
            b._taken = true;
            return false;
        }

        public readonly struct Scope : IDisposable
        {
            private readonly SnapshotBatch? _previous;
            public Scope(SnapshotBatch batch) { _previous = s_current; s_current = batch; }
            public void Dispose() => s_current = _previous;
        }
    }
}
