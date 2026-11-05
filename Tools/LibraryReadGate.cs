using System.Threading;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>
    /// One background reader of the library's audio at a time: Compute Stats and AI scoring per song, the tag scan per run.
    /// Only ever acquired through WaitAsync, so waiters are served in order and a loop that releases and re-enters
    /// can't jump the queue.
    /// </summary>
    internal sealed class LibraryReadGate
    {
        private readonly SemaphoreSlim _sem = new(1, 1);

        public static LibraryReadGate Shared { get; } = new();

        public Task EnterAsync(CancellationToken ct) => _sem.WaitAsync(ct);

        /// <summary>For code already on a background thread; waits its turn like everyone else.</summary>
        public void Enter(CancellationToken ct) => _sem.WaitAsync(ct).GetAwaiter().GetResult();

        public void Exit() => _sem.Release();

        public bool IsFree => _sem.CurrentCount == 1;
    }
}
