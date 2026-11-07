using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor.Tools
{
    internal enum StatsSongNote { StillRunning, FinishedSlow, TimedOut }

    internal readonly record struct StatsDetectStop(int TimedOut, int Skipped);

    /// <summary>When a song counts as slow, when it is given up on, and how many give-ups end the run.</summary>
    internal readonly record struct StatsDetectLimits(TimeSpan SlowAfter, TimeSpan Timeout, int MaxTimeouts)
    {
        public static StatsDetectLimits Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3), 3);
    }

    /// <summary>Observers for the log; all run on the caller's context.</summary>
    internal sealed record StatsDetectHooks(
        Action<string, TimeSpan, StatsSongNote>? Note = null, // rel, elapsed (the limit for TimedOut), kind
        Action<StatsDetectStop>? Stopped = null,              // once, when MaxTimeouts ends the run
        Action<int, TimeSpan>? Waiting = null);               // once per song, index and elapsed, when its turn is slow to come

    /// <summary>
    /// Analyses one song at a time, in Windows background mode, pausing while the window is busy and taking the shared
    /// library-read gate per song. The waits and progress run on the caller's (UI) context.
    /// </summary>
    internal static class StatsDetectLoop
    {
        /// <returns>The songs whose analysis did not complete, then any left unattempted after too many timeouts.</returns>
        public static async Task<List<string>> RunAsync(IReadOnlyList<string> rels, Func<string, bool> detect, Func<bool> isIdle,
            Func<CancellationToken, Task> waitUntilIdle, LibraryReadGate gate, CancellationToken ct, Action<int, int>? progress = null,
            StatsDetectLimits? limits = null, StatsDetectHooks? hooks = null)
        {
            var l = limits ?? StatsDetectLimits.Default;
            // Hooks only log; one that throws must never leave the gate held or skip a song's bookkeeping.
            static void Safe(Action a) { try { a(); } catch { } }
            var failed = new List<string>();
            int timeouts = 0;
            for (int i = 0; i < rels.Count; i++)
            {
                string rel = rels[i];
                bool ok = false;
                async Task WaitTurn()
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        await waitUntilIdle(ct);
                        await gate.EnterAsync(ct, "stats");
                        if (isIdle()) break;
                        gate.Exit(); // something started while we waited for the gate: wait again
                    }
                }

                var sw = Stopwatch.StartNew();
                using (var timer = new CancellationTokenSource())
                {
                    var turn = WaitTurn();
                    if (hooks?.Waiting != null && await Task.WhenAny(turn, Task.Delay(l.SlowAfter, timer.Token)) != turn)
                    {
                        var waited = sw.Elapsed;
                        Safe(() => hooks.Waiting(i, waited));
                    }
                    timer.Cancel();
                    await turn;
                }

                try
                {
                    sw.Restart();
                    var work = Task.Run(() => { using (ThreadPriorityScope.Background()) return detect(rel); });
                    bool slow = false, timedOut = false;
                    using (var timer = new CancellationTokenSource())
                    {
                        if (await Task.WhenAny(work, Task.Delay(l.SlowAfter, timer.Token)) != work)
                        {
                            slow = true;
                            var at = sw.Elapsed;
                            Safe(() => hooks?.Note?.Invoke(rel, at, StatsSongNote.StillRunning));
                            var rest = l.Timeout - sw.Elapsed;
                            if (await Task.WhenAny(work, Task.Delay(rest > TimeSpan.Zero ? rest : TimeSpan.Zero, timer.Token)) != work)
                                timedOut = true;
                        }
                        timer.Cancel();
                    }
                    if (timedOut)
                    {
                        // Abandoned: a thread can't be stopped safely; any later fault is observed here.
                        timeouts++;
                        _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        Safe(() => hooks?.Note?.Invoke(rel, l.Timeout, StatsSongNote.TimedOut));
                    }
                    else
                    {
                        try { ok = await work; }
                        finally
                        {
                            // Closed out even when it threw, so a slow song never reads like a hang in the log.
                            var took = sw.Elapsed;
                            if (slow) Safe(() => hooks?.Note?.Invoke(rel, took, StatsSongNote.FinishedSlow));
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { ok = false; }
                finally { gate.Exit(); }
                if (!ok) failed.Add(rel);
                progress?.Invoke(i + 1, rels.Count);
                if (timeouts >= l.MaxTimeouts)
                {
                    var rest = rels.Skip(i + 1).ToList();
                    failed.AddRange(rest);
                    var stop = new StatsDetectStop(timeouts, rest.Count);
                    Safe(() => hooks?.Stopped?.Invoke(stop));
                    break;
                }
            }
            return failed;
        }
    }
}
