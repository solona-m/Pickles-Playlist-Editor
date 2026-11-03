using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>Lowers the current thread's priority for a block and always restores it.</summary>
    internal readonly struct ThreadPriorityScope : IDisposable
    {
        private readonly ThreadPriority _saved;
        private readonly bool _background;
        private ThreadPriorityScope(ThreadPriority saved, bool background = false) { _saved = saved; _background = background; }

        private const int ThreadModeBackgroundBegin = 0x00010000, ThreadModeBackgroundEnd = 0x00020000;
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr thread, int priority);

        /// <summary>Windows background mode: lower CPU, I/O and memory priority. Falls back to BelowNormal.</summary>
        public static ThreadPriorityScope Background()
        {
            var saved = Thread.CurrentThread.Priority;
            bool begun = false;
            try { begun = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin); } catch { }
            if (!begun) Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            return new ThreadPriorityScope(saved, begun);
        }

        public static ThreadPriorityScope BelowNormal()
        {
            var saved = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            return new ThreadPriorityScope(saved);
        }

        public void Dispose()
        {
            if (_background) { try { SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd); } catch { } }
            Thread.CurrentThread.Priority = _saved;
        }
    }
}
