using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Notices when something outside this app has taken content out of the mod folder, and keeps
    /// the reload from hammering a Penumbra that has stopped accepting them.
    ///
    /// The failure this exists for, from three users' logs: Penumbra answers a reload with 500, so
    /// it never re-reads the folder and its in-memory copy of the mod stays older than what is on
    /// disk. The next time anything makes Penumbra write that mod — its own UI, its folder watcher,
    /// a sort — it writes the stale copy back out and renumbers the group files as it goes, and
    /// every edit made since is gone. Four reversions were recorded that way, each one preceded by
    /// a failed reload, twice on a mod folder the user had freshly copied and was not reinstalling.
    ///
    /// The mechanism itself is Penumbra's and has been understood here for a while — see
    /// <see cref="V3GroupFileStore.SanitizeGroupFileName"/>, which documents ModCreator rewriting
    /// and renumbering the whole set "from the copy it read at the start of that reload". What was
    /// missing is that the existing defence assumed the reload SUCCEEDS: the response is treated as
    /// the safe-to-write-again signal, and the 500 path was a log line and nothing else.
    ///
    /// Deliberately does NOT restore on its own. A revert and a change the user made deliberately in
    /// Penumbra's own UI are the same bytes on disk, so restoring automatically would silently undo
    /// real intent. This class decides and reports. A human presses the button.
    ///
    /// All state is per-process and per-mod, and every member is safe to call from any thread.
    /// </summary>
    internal static class ModFolderGuard
    {
        /// <summary>What the folder held the last time we knew we were the ones who put it there.</summary>
        internal readonly record struct Baseline(
            string ModRoot, int Playlists, int Songs, long Writes, DateTime TakenAt);

        /// <summary>
        /// A confirmed loss, in the same units the log line and the backups dropdown use.
        ///
        /// Carries the before and after counts and nothing derived from them. A "how much was lost"
        /// property would be a second set of numbers for the UI to choose from, and the whole point of
        /// reporting raw before/after is that a user can match what the banner says against their own
        /// log line without arithmetic.
        /// </summary>
        internal sealed record ExternalChange(Baseline Before, int Playlists, int Songs);

        /// <summary>Raised once per confirmed loss, from a background thread.</summary>
        internal static event Action<ExternalChange>? ExternalChangeDetected;

        // Bumped by every write this app makes. The point is not the value but the comparison: if it
        // has not moved since the baseline, whatever changed on disk was not us. Interlocked because
        // writes come off the UI thread, the drag-reorder task, download tasks and the debounce timer.
        private static long s_writes;

        private static readonly object s_lock = new();
        private static Baseline? s_baseline;
        private static bool s_confirming;
        private static DateTime s_quietUntil;

        /// <summary>
        /// Records that this app is about to write to the mod folder.
        ///
        /// Called from <see cref="V3GroupFileStore.TrySnapshotSet"/> and
        /// <see cref="PenumbraMeta.TrySnapshot()"/> rather than from each mutator, because every
        /// write path in the app already snapshots immediately before writing — Save, Upsert, Delete,
        /// ReorderAll, Mutate, Library.Repair, SoundPathRename and BackupCatalog.Restore all do. A
        /// mutator added later is therefore covered by default, and forgetting the snapshot is
        /// already treated as a bug here.
        ///
        /// Deliberately NOT called from HealOnLoad's repairs. ReclaimReorderTempFiles only ever moves
        /// a ".reorder_tmp" sidecar, which does not match the group_*.json glob and so cannot change
        /// the count, and NormalizeGroupFileNames only renames. Neither can produce a loss, and
        /// counting them would suppress detection on exactly the load after a revert — the load where
        /// the folder most likely needs normalizing.
        /// </summary>
        internal static void NoteWrite() => Interlocked.Increment(ref s_writes);

        /// <summary>
        /// The write counter as it stands. For a caller that has to sample it BEFORE doing something
        /// slow, so that what it records afterwards is honest about what it could not have seen.
        /// </summary>
        internal static long CurrentWrites => Interlocked.Read(ref s_writes);

        /// <summary>
        /// Records what the folder holds at a moment when this app is certainly the author of it.
        ///
        /// Called from the debounced reload once the folder gate is held: every write of ours has
        /// landed by then and nothing else of ours can be in flight, so what is on disk is exactly
        /// what we put there. Any later disagreement is somebody else's doing.
        ///
        /// The counts are READ FROM DISK rather than handed in by the caller. They used to be the
        /// ones from the last library load, which is the same thing only if a load has happened since
        /// the last write — and the common edits do not reload. Deleting a song patches the tree in
        /// place (MainWindow's delete only reloads while a filter is active), and Library.Repair takes
        /// its GetAll BEFORE it drops anything. Either way the baseline came out higher than the
        /// folder, and the next refresh of any kind reported the user's own deletion back to them as a
        /// Penumbra revert — offering to restore the song they had just chosen to remove.
        ///
        /// An unreadable folder KEEPS the baseline it had rather than dropping it. Dropping is never
        /// the better of the two: if writes have happened since, the write-counter test already makes
        /// the guard inert either way, and if none have, the old baseline is still exactly true. It
        /// matters because the two counters disagree about damaged files — V3GroupFileStore.ReadAll
        /// skips an unparseable group file and carries on, while <see cref="CountOnDisk"/> refuses to
        /// answer at all — so one corrupt file used to disable the guard permanently, and a manifest
        /// caught locked mid-write disabled it exactly when Penumbra was rewriting the folder.
        /// </summary>
        internal static void NoteBaseline(string modRoot)
        {
            if (string.IsNullOrWhiteSpace(modRoot)) return;

            // Nothing of ours has been written since the last baseline, so a recount can only return
            // what that baseline already says — and if it disagrees, it disagrees because somebody
            // ELSE changed the folder, which is the one state that must never be adopted as a new
            // baseline. Keeping what we have is both the cheaper and the more correct answer, and it
            // is what lets the retry against a wedged Penumbra run every couple of minutes without
            // re-parsing the whole folder each time.
            lock (s_lock)
            {
                if (s_baseline is Baseline current
                    && string.Equals(current.ModRoot, modRoot, StringComparison.OrdinalIgnoreCase)
                    && current.Writes == Interlocked.Read(ref s_writes))
                    return;
            }

            // Deliberately outside the lock: this reads every group file, and ReportIfExternalChange
            // takes the same lock from the UI thread on every library load.
            var (playlists, songs) = CountOnDisk(modRoot);

            if (playlists < 0)
            {
                // Once per episode, not once per reload — this runs after every edit, and a folder
                // that stays uncountable would otherwise put a line in the log every 20 seconds.
                if (!s_reportedUncountable)
                {
                    s_reportedUncountable = true;
                    Logger.LogInfo("Mod folder could not be counted, so the previous baseline stands. " +
                        "Detection of outside changes is on hold until it reads cleanly again.");
                }
                return;
            }

            s_reportedUncountable = false;

            lock (s_lock)
                s_baseline = new Baseline(modRoot, playlists, songs,
                    Interlocked.Read(ref s_writes), DateTime.Now);
        }

        // Whether the "could not count the folder" line has already been logged for this episode.
        private static volatile bool s_reportedUncountable;

        /// <summary>
        /// Records a baseline from a library load, when the load is the best moment available.
        ///
        /// <see cref="NoteBaseline"/> is the stronger of the two and stays the preferred one: it runs
        /// with the folder gate held, straight after Penumbra confirmed it re-read the mod. But it was
        /// for a while the ONLY one, and it hangs off the reload — so the entire guard was switched off
        /// for anyone who unticked "auto-reload mod", a setting with nothing to do with any of this and
        /// a plausible thing to reach for when Penumbra is misbehaving. The same hole swallowed a
        /// reload dropped because the folder gate was busy (nothing reschedules that one), and the
        /// whole of every session before the user's first edit.
        ///
        /// A load is a sound enough moment on its own. <paramref name="writesBeforeRead"/> was sampled
        /// before the folder was read, so if any write of ours landed during the read the baseline
        /// records a counter older than its own contents — which makes it inert until a better moment
        /// comes along, rather than wrong.
        ///
        /// Only ever moves the baseline forward past writes it does not already account for. A
        /// standing baseline at or ahead of this read is left alone, which is what keeps a load from
        /// trampling the reload's baseline, and from overwriting the re-baseline
        /// <see cref="Confirm"/> takes so a revert is reported once rather than on every load.
        /// </summary>
        internal static void NoteBaselineFromLoad(string modRoot, int playlists, int songs,
            long writesBeforeRead)
        {
            if (string.IsNullOrWhiteSpace(modRoot)) return;

            lock (s_lock)
            {
                if (s_baseline is Baseline current
                    && string.Equals(current.ModRoot, modRoot, StringComparison.OrdinalIgnoreCase)
                    && current.Writes >= writesBeforeRead)
                    return;

                s_baseline = new Baseline(modRoot, playlists, songs, writesBeforeRead, DateTime.Now);
            }
        }

        /// <summary>
        /// Drops the baseline, so counts from one mod are never compared against another's.
        ///
        /// Called from the <see cref="Settings.ModName"/> and <see cref="Settings.PenumbraLocation"/>
        /// setters rather than from the Settings dialog, because those are the two values
        /// <see cref="PenumbraMeta.ModRoot"/> is built from and every path that repoints the app goes
        /// through one of them. <see cref="ReportIfExternalChange"/> compares roots and so would not
        /// report mod A's counts against mod B either way — but without this a baseline for A survives
        /// a trip to B and back, and by the time it is compared again it can be arbitrarily old.
        /// </summary>
        internal static void Forget()
        {
            lock (s_lock)
                s_baseline = null;
        }

        /// <summary>Stops the UI being offered an undo for a while, without stopping detection or
        /// logging. Used after a restore, so the restore's own settling cannot re-raise it.</summary>
        internal static void QuietFor(TimeSpan window)
        {
            lock (s_lock)
                s_quietUntil = DateTime.UtcNow.Add(window);
        }

        /// <summary>
        /// Judges the counts just loaded, and raises <see cref="ExternalChangeDetected"/> if the
        /// folder has lost content that this app did not remove.
        ///
        /// Two conditions gate a suspicion, and both are load-bearing:
        ///
        ///   * No write of ours since the baseline. A user deleting a playlist goes through a write
        ///     path, so their own deletions can never raise this — which is the whole reason the
        ///     counter exists rather than a bare count comparison.
        ///   * Content was LOST. A Penumbra rewrite that renumbers or renames without dropping
        ///     anything is not worth interrupting anyone over, and renumbering alone is routine.
        ///
        /// A suspicion is then CONFIRMED on a background thread before anything is reported. Penumbra
        /// rewrites the whole group set non-atomically and the folder gate cannot lock it out — see
        /// PlaylistStore.ModFolderGate — so a read landing mid-rewrite sees a partial folder and
        /// looks like catastrophic loss. Without the second look this would cry wolf during normal
        /// use. Returns immediately; the caller never waits.
        ///
        /// Counts rather than a content hash on purpose: the caller already has both, they are the
        /// units the log line and <see cref="BackupCatalog"/> report in — so a user can match the
        /// numbers against their own log — and a hash would flag every harmless renumber.
        /// </summary>
        internal static void ReportIfExternalChange(string modRoot, int playlists, int songs)
        {
            Baseline before;
            lock (s_lock)
            {
                if (s_baseline is not Baseline baseline) return;
                if (s_confirming) return;

                // A different mod is not evidence about this one. Settings change at runtime.
                if (!string.Equals(baseline.ModRoot, modRoot, StringComparison.OrdinalIgnoreCase))
                    return;

                if (Interlocked.Read(ref s_writes) != baseline.Writes) return;
                if (playlists >= baseline.Playlists && songs >= baseline.Songs) return;

                s_confirming = true;
                before = baseline;
            }

            _ = Task.Run(() => Confirm(modRoot, before));
        }

        private static void Confirm(string modRoot, Baseline before)
        {
            try
            {
                // Long enough for a whole-set rewrite to finish, short enough that the banner still
                // feels like a reaction. Penumbra's rewrite is ~17 small files.
                Thread.Sleep(600);

                // Test the write counter again before believing anything. It was checked before the
                // sleep, and this runs off a library load the user may well still be acting on, so a
                // save of ours can land inside the window. A half-written file is caught below, but a
                // delete that completes cleanly in here reads as a clean, lower, entirely legitimate
                // count — and would be reported as somebody else's doing.
                if (Interlocked.Read(ref s_writes) != before.Writes) return;

                var (playlists, songs) = CountOnDisk(modRoot);
                if (playlists < 0) return;   // unreadable; say nothing rather than guess

                if (playlists >= before.Playlists && songs >= before.Songs)
                {
                    Logger.LogInfo("Mod folder dipped to {Playlists} playlist(s)/{Songs} song(s) and " +
                        "came back — a rewrite caught in progress, not a loss.", playlists, songs);
                    return;
                }

                lock (s_lock)
                {
                    // Re-baseline to what is really there, so one revert reports once instead of on
                    // every load until the user acts. A further loss re-arms it.
                    if (s_baseline is Baseline current
                        && string.Equals(current.ModRoot, modRoot, StringComparison.OrdinalIgnoreCase))
                        s_baseline = current with { Playlists = playlists, Songs = songs };

                    if (DateTime.UtcNow < s_quietUntil)
                    {
                        Logger.LogInfo("Mod folder changed outside the editor again, within the quiet " +
                            "window after a restore — logged, not shown.");
                        return;
                    }
                }

                Logger.LogWarn("Mod folder changed outside the editor: was {WasPlaylists} playlist(s)/" +
                    "{WasSongs} song(s) at {When}, now {NowPlaylists}/{NowSongs}. This app wrote " +
                    "nothing in between. Penumbra {Penumbra}.",
                    before.Playlists, before.Songs, before.TakenAt.ToString("HH:mm:ss"),
                    playlists, songs, PenumbraInstall.CachedDescription);

                ExternalChangeDetected?.Invoke(new ExternalChange(before, playlists, songs));
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Confirming an external mod folder change failed: {Error}", ex.Message);
            }
            finally
            {
                lock (s_lock) s_confirming = false;
            }
        }

        /// <summary>
        /// Playlists and songs straight off disk, counted the way <c>Playlist.GetAll</c> counts them —
        /// which means nameless groups skipped and a repeated name counted once.
        ///
        /// Matching GetAll's arithmetic is not a nicety. Penumbra permits duplicate group names and
        /// GetAll keeps only the first, so a bare count of the files read HIGHER than the baseline it
        /// was being compared against. The confirmation pass then found the folder "no smaller than
        /// before" and wrote a genuine loss off as a rewrite caught in progress — on precisely the
        /// whole-set rewrite-and-renumber that produces duplicate names in the first place.
        ///
        /// Deliberately does not go through Playlist.GetAll: that runs HealOnLoad first, which WRITES
        /// to the folder. A confirmation pass has to be a pure observation, or it changes the thing
        /// it is measuring. Everything BELOW GetAll is reused rather than reimplemented, though — the
        /// same manifest reader, the same layout test, the same file ordering — because answering
        /// those questions independently is precisely how this drifted out of step with GetAll three
        /// separate ways: a hardcoded glob, a shape test the format detector documents as wrong, and
        /// an unordered enumeration that settled the duplicate-name tie the other way round.
        ///
        /// Returns (-1, -1) when the folder cannot be read.
        /// </summary>
        private static (int Playlists, int Songs) CountOnDisk(string modRoot)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot)) return (-1, -1);

                // PenumbraMeta.Read rather than a bare parse: it retries a file caught locked mid-
                // write and remembers one already proven bad, which is the difference between "the
                // library changed" and "Penumbra happened to be holding the manifest just then".
                var root = PenumbraMeta.Read(modRoot);

                // FileVersion, never shape — DetectFormat's own doc explains why the presence of a
                // Groups array cannot be the discriminator, and PlaylistStore picks its store the
                // same way. Branching on "are there group files?" counted the leftover group files of
                // a converted v4 mod instead of its manifest.
                var format = PenumbraMeta.DetectFormat(root, modRoot);

                // The loader's own counting rule, not a copy of it — see Playlist.GroupTally.
                var tally = new Playlist.GroupTally();

                switch (format)
                {
                    // v4 keeps the whole library in the manifest. No Groups key is a legitimately
                    // empty mod rather than an unreadable one — Penumbra omits it — and V4MetaStore
                    // loads nothing for it, so (0, 0) is the count that matches the load.
                    case ModFormat.V4:
                        foreach (var g in (PenumbraMeta.TryReadGroups(root) ?? new JArray()).OfType<JObject>())
                            tally.Add(g);
                        return (tally.Playlists, tally.Songs);

                    // v3 spreads it across the group files. GroupFilesOrdered, not a raw enumeration:
                    // GetAll reads them in this order and keeps the FIRST of a repeated name, so a
                    // different order picks a different winner — same playlist count, but a different
                    // song count, which reads as a loss that never happened.
                    case ModFormat.V3:
                        foreach (string file in V3GroupFileStore.GroupFilesOrdered(modRoot))
                        {
                            var group = V3GroupFileStore.TryLoadGroupQuiet(file, out _);
                            if (group == null) return (-1, -1);   // mid-write; a partial count is worse than none

                            tally.Add(group);
                        }
                        return (tally.Playlists, tally.Songs);

                    // Not a folder we can read as a mod at all, most likely caught mid-swap. Not "an
                    // empty mod": answering (0, 0) would report the most total loss imaginable every
                    // single time one appeared.
                    default:
                        return (-1, -1);
                }
            }
            catch
            {
                return (-1, -1);
            }
        }

        // ---- reload backoff --------------------------------------------------------------------

        /// <summary>How many answered-but-failed reloads before the per-edit reload stops firing.</summary>
        internal const int FailuresBeforePause = 3;

        /// <summary>How long to leave it paused before trying once more.</summary>
        internal static readonly TimeSpan RetryAfterPause = TimeSpan.FromMinutes(2);

        private static int s_consecutiveFailures;
        private static long s_pausedUntilTicks;   // DateTime.UtcNow.Ticks, 0 when not paused

        internal static int ConsecutiveFailures => Volatile.Read(ref s_consecutiveFailures);

        /// <summary>True the first time each pause begins, so the caller reports it once.</summary>
        internal static bool NoteReloadFailed()
        {
            int failures = Interlocked.Increment(ref s_consecutiveFailures);
            if (failures < FailuresBeforePause) return false;

            // Only the transition reports. Every later failure while already paused is silent, or a
            // wedged Penumbra would put a line in the log for every edit — the noise the API layer's
            // once-per-session reporting exists to avoid.
            long until = DateTime.UtcNow.Add(RetryAfterPause).Ticks;
            return Interlocked.Exchange(ref s_pausedUntilTicks, until) == 0;
        }

        /// <summary>Clears the failure run and any pause. True if this ended a pause.</summary>
        internal static bool NoteReloadSucceeded()
        {
            Interlocked.Exchange(ref s_consecutiveFailures, 0);
            return Interlocked.Exchange(ref s_pausedUntilTicks, 0) != 0;
        }

        /// <summary>
        /// Records a reload nobody answered. True if that cleared a pause which had already run out.
        ///
        /// A lapsed pause is left armed on purpose — see <see cref="RemainingPause"/> — and only a
        /// success used to clear it. That left it armed forever as soon as Penumbra stopped answering
        /// at all, which is how every session ordinarily ends: the user closes the game. The next time
        /// Penumbra genuinely started refusing, the transition test in <see cref="NoteReloadFailed"/>
        /// found that stale value, concluded a pause was already running, and reported nothing —
        /// no log line and no banner, for the whole of the next session.
        ///
        /// The failure count is deliberately NOT reset here. A no-answer is also Penumbra mid-zone-
        /// load, and letting a transient one refund the budget would stop the pause ever arming
        /// against a Penumbra that alternates between refusing and being busy.
        /// </summary>
        internal static bool NoteReloadUnanswered()
        {
            long until = Interlocked.Read(ref s_pausedUntilTicks);
            if (until == 0 || DateTime.UtcNow.Ticks < until) return false;

            return Interlocked.CompareExchange(ref s_pausedUntilTicks, 0, until) == until;
        }

        /// <summary>
        /// What is left of the current pause, or <see cref="TimeSpan.Zero"/> once it has run out (or
        /// never started). The scheduler waits out the REMAINDER rather than starting a fresh interval
        /// each time, or a steady stream of edits would push the retry back on every one and it would
        /// never fire at all.
        ///
        /// Reaching zero does not disarm the pause. Leaving it armed is what lets exactly one reload
        /// through: if it succeeds the pause clears, and if it fails <see cref="NoteReloadFailed"/>
        /// re-arms it for another interval without re-reporting a state the user was already told
        /// about. <see cref="NoteReloadUnanswered"/> is the one other way out.
        /// </summary>
        internal static TimeSpan RemainingPause
        {
            get
            {
                long until = Interlocked.Read(ref s_pausedUntilTicks);
                if (until == 0) return TimeSpan.Zero;

                var left = TimeSpan.FromTicks(until - DateTime.UtcNow.Ticks);
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }

        // The write count as of the last copy the failed-reload path took.
        private static long s_lastFailureSnapshotWrites = -1;

        /// <summary>
        /// Whether the failed-reload path should take another copy of the folder.
        ///
        /// True only if this app has written something since the last one it took. A wedged Penumbra
        /// gets retried for as long as it stays wedged, and copying a folder that has not changed
        /// since the previous copy adds nothing — an evening of retries would otherwise leave a few
        /// hundred identical snapshot sets behind, when the one worth keeping is the earliest, taken
        /// while the good state was still fresh.
        /// </summary>
        internal static bool ShouldSnapshotForFailure()
        {
            long writes = Interlocked.Read(ref s_writes);
            return Interlocked.Exchange(ref s_lastFailureSnapshotWrites, writes) != writes;
        }
    }
}
