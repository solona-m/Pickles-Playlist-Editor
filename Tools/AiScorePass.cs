using System.Threading;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>Which AI scoring pass: phase 2 of Compute Stats, or the background scan of the whole library.</summary>
    internal enum AiPass { ComputeStats, Library }

    internal enum AiPassEnd { NothingToScore, NotReady, RuntimeFailed, Finished, Cancelled, Error }

    internal enum AiPassStart { Started, Queued, AlreadyRunning }

    /// <summary>Handed out by <see cref="AiPassGate.TryBegin"/>; Id 0 means no pass started.</summary>
    internal readonly record struct AiPassTicket(long Id, CancellationToken Token);

    internal static class AiPassRules
    {
        /// <summary>A background scan stays quiet unless the model can't load; a cancelled or failed pass never shows one.</summary>
        public static bool ShowsDialog(AiPass pass, AiPassEnd end) =>
            end is not (AiPassEnd.Cancelled or AiPassEnd.Error) && (pass == AiPass.ComputeStats || end == AiPassEnd.RuntimeFailed);
    }

    /// <summary>
    /// One AI pass at a time. A request that finds a pass running is queued and becomes one library scan after it
    /// (Compute Stats is also told). Turning the score off cancels and clears the queue; closing never restarts.
    /// </summary>
    internal sealed class AiPassGate(CancellationToken closing)
    {
        private readonly object _lock = new();
        private CancellationTokenSource? _cts;
        private long _runningId, _nextId;
        private bool _queued;

        public bool IsRunning { get { lock (_lock) return _runningId != 0; } }
        public bool IsQueued { get { lock (_lock) return _queued; } }

        public AiPassStart TryBegin(AiPass pass, out AiPassTicket ticket)
        {
            lock (_lock)
            {
                if (_runningId != 0)
                {
                    ticket = default;
                    _queued = true;
                    return pass == AiPass.Library ? AiPassStart.Queued : AiPassStart.AlreadyRunning;
                }
                _cts = CancellationTokenSource.CreateLinkedTokenSource(closing);
                _runningId = ++_nextId;
                ticket = new AiPassTicket(_runningId, _cts.Token);
                return AiPassStart.Started;
            }
        }

        public void TurnOff()
        {
            lock (_lock)
            {
                _queued = false;
                CancelLocked();
            }
        }

        public void CancelRunning()
        {
            lock (_lock) CancelLocked();
        }

        /// <summary>Releases the pass the ticket started; true when a queued library scan should start now.</summary>
        public bool End(AiPassTicket ticket, bool enabled)
        {
            lock (_lock)
            {
                if (ticket.Id == 0 || ticket.Id != _runningId) return false;
                bool queued = _queued;
                _queued = false;
                _runningId = 0;
                _cts?.Dispose();
                _cts = null;
                return queued && enabled && !closing.IsCancellationRequested;
            }
        }

        private void CancelLocked()
        {
            try { _cts?.Cancel(); } catch (System.ObjectDisposedException) { }
        }
    }
}
