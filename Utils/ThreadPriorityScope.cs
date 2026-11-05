using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>Lowers the current thread's priority for a block and always restores it.</summary>
    internal readonly struct ThreadPriorityScope : IDisposable
    {
        private readonly ThreadPriority _saved;
        private readonly bool _background, _suspended;
        [ThreadStatic] private static bool s_backgroundMode;
        internal static bool IsBackgroundMode => s_backgroundMode; // this thread; for tests
        private ThreadPriorityScope(ThreadPriority saved, bool background = false, bool suspended = false)
        {
            _saved = saved;
            _background = background;
            _suspended = suspended;
        }

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
            s_backgroundMode |= begun;
            return new ThreadPriorityScope(saved, begun);
        }

        /// <summary>Leaves background mode for a block (a write under a lock the UI thread also takes); a no-op otherwise.</summary>
        public static ThreadPriorityScope SuspendBackground()
        {
            var saved = Thread.CurrentThread.Priority;
            if (!s_backgroundMode) return new ThreadPriorityScope(saved);
            bool ended = false;
            try { ended = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd); } catch { }
            if (ended) s_backgroundMode = false;
            return new ThreadPriorityScope(saved, suspended: ended);
        }

        public static ThreadPriorityScope BelowNormal()
        {
            var saved = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            return new ThreadPriorityScope(saved);
        }

        public void Dispose()
        {
            if (_suspended)
            {
                try { if (SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin)) s_backgroundMode = true; } catch { }
                return;
            }
            if (_background)
            {
                try { SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd); } catch { }
                s_backgroundMode = false;
            }
            Thread.CurrentThread.Priority = _saved;
        }
    }
}
