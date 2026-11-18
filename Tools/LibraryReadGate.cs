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
        private volatile string? _holder;

        public static LibraryReadGate Shared { get; } = new();

        /// <summary>Who holds the gate ("tags", "ai", "stats"), for diagnostics; may briefly lag a handover.</summary>
        public string? Holder => _holder;

        public async Task EnterAsync(CancellationToken ct, string? holder = null)
        {
            await _sem.WaitAsync(ct);
            _holder = holder;
        }

        /// <summary>For code already on a background thread; waits its turn like everyone else.</summary>
        public void Enter(CancellationToken ct, string? holder = null)
        {
            _sem.WaitAsync(ct).GetAwaiter().GetResult();
            _holder = holder;
        }

        public void Exit()
        {
            _holder = null;
            _sem.Release();
        }

        public bool IsFree => _sem.CurrentCount == 1;
    }
}
