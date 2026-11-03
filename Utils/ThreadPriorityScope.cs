using System;
using System.Threading;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>Lowers the current thread's priority for a block and always restores it.</summary>
    internal readonly struct ThreadPriorityScope : IDisposable
    {
        private readonly ThreadPriority _saved;
        private ThreadPriorityScope(ThreadPriority saved) => _saved = saved;

        public static ThreadPriorityScope BelowNormal()
        {
            var saved = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            return new ThreadPriorityScope(saved);
        }

        public void Dispose() => Thread.CurrentThread.Priority = _saved;
    }
}
