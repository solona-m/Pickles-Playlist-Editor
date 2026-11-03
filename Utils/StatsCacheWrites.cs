using System;
using System.Threading;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Every BPM, length and key cache write goes through one lock, so closing or restarting can wait (briefly) for a
    /// write in progress: a cache file torn mid-write is deleted at the next start, and every song's stats with it.
    /// </summary>
    internal static class StatsCacheWrites
    {
        public static readonly object Lock = new();
        private static bool s_held; // UI thread only

        /// <summary>At normal priority, so a background-mode detector never holds the lock at the bottom of the I/O queue.</summary>
        public static void Write(Action action)
        {
            using (ThreadPriorityScope.SuspendBackground())
                lock (Lock) action();
        }

        /// <summary>Waits up to a second for a write in progress, then keeps writers out until exit. Idempotent.</summary>
        public static bool HoldForExit()
        {
            if (s_held) return true;
            s_held = Monitor.TryEnter(Lock, TimeSpan.FromSeconds(1));
            return s_held;
        }

        /// <summary>Undoes <see cref="HoldForExit"/> when an exit was called off; never releases a lock it didn't take.</summary>
        public static void ReleaseExitHold()
        {
            if (!s_held) return;
            s_held = false;
            Monitor.Exit(Lock);
        }
    }
}
