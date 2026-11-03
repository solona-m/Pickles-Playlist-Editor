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

        /// <summary>True when a snapshot should be skipped: inside a batch that already has its one.</summary>
        internal static bool SkipSnapshot() => s_current is { _taken: true };

        /// <summary>A snapshot now exists (written, or identical to the newest); later saves in the batch skip theirs.</summary>
        internal static void Taken()
        {
            if (s_current is { } b) b._taken = true;
        }

        public readonly struct Scope : IDisposable
        {
            private readonly SnapshotBatch? _previous;
            public Scope(SnapshotBatch batch) { _previous = s_current; s_current = batch; }
            public void Dispose() => s_current = _previous;
        }
    }
}
