using System;
using System.Threading;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>A Compute Stats run the user started, or the quiet once-per-version run.</summary>
    internal enum StatsPassKind { Manual, Auto }

    internal enum StatsPassEnd { Finished, Cancelled, Error }

    internal readonly record struct StatsPassTicket(long Id, StatsPassKind Kind, CancellationToken Token);

    /// <summary>One stats run at a time, linked to the window's closing token; like <see cref="AiPassGate"/>.</summary>
    internal sealed class StatsPassGate(CancellationToken closing)
    {
        private readonly object _lock = new();
        private CancellationTokenSource? _cts;
        private long _runningId, _nextId;

        public bool IsRunning { get { lock (_lock) return _runningId != 0; } }

        public bool TryBegin(StatsPassKind kind, out StatsPassTicket ticket)
        {
            lock (_lock)
            {
                if (_runningId != 0) { ticket = default; return false; }
                _cts = CancellationTokenSource.CreateLinkedTokenSource(closing);
                _runningId = ++_nextId;
                ticket = new StatsPassTicket(_runningId, kind, _cts.Token);
                return true;
            }
        }

        public void CancelRunning()
        {
            lock (_lock) { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }
        }

        /// <summary>Releases the run the ticket started; a ticket that started nothing changes nothing.</summary>
        public bool End(StatsPassTicket ticket)
        {
            lock (_lock)
            {
                if (ticket.Id == 0 || ticket.Id != _runningId) return false;
                _runningId = 0;
                _cts?.Dispose();
                _cts = null;
                return true;
            }
        }
    }

    /// <summary>When the once-per-version run starts, and what it records. Pure, so every rule is a table test.</summary>
    internal static class AutoStatsRules
    {
        public const int MaxAttempts = 3;

        public static bool ShouldRun(string? lastCompleted, string? attempt, string running, bool modReadable) =>
            modReadable && !string.IsNullOrEmpty(running) && lastCompleted != running && AttemptsFor(attempt, running) < MaxAttempts;

        /// <summary>
        /// An attempt is recorded when the automatic run starts. It is kept for an Error or failed saves, and a crash keeps
        /// it by never ending; a clean finish or a cancel (closing, an exit, a mod change) takes it back. A run stopped by
        /// too many timed-out songs keeps it, so a later launch retries.
        /// </summary>
        public static bool KeepsAttempt(StatsPassEnd end, int saveFailures, bool stoppedEarly = false) =>
            end == StatsPassEnd.Error || (end == StatsPassEnd.Finished && (saveFailures > 0 || stoppedEarly));

        public static string NextAttempt(string? attempt, string running) => $"{running}|{AttemptsFor(attempt, running) + 1}";

        public static bool ShouldWriteMarker(StatsPassEnd end, int saveFailures, int snapshotCount, int playlistCount, bool stoppedEarly = false) =>
            end == StatsPassEnd.Finished && saveFailures == 0 && !stoppedEarly && !(snapshotCount == 0 && playlistCount == 0);

        /// <summary>Failed attempts recorded for this version; another version or an unreadable value counts as 0.</summary>
        public static int AttemptsFor(string? attempt, string running)
        {
            if (string.IsNullOrEmpty(attempt)) return 0;
            int bar = attempt.LastIndexOf('|');
            if (bar <= 0 || attempt[..bar] != running) return 0;
            return int.TryParse(attempt[(bar + 1)..], out int n) && n > 0 ? n : 0;
        }
    }

    /// <summary>Two status-bar slots: the first wins; nothing when both are empty. Nested for backfill over stats over AI.</summary>
    internal static class BackgroundStatus
    {
        public static (string Text, int Percent)? Pick((string Text, int Percent)? stats, (string Text, int Percent)? ai) =>
            stats is { } s && !string.IsNullOrEmpty(s.Text) ? s : ai is { } a && !string.IsNullOrEmpty(a.Text) ? a : null;
    }
}
