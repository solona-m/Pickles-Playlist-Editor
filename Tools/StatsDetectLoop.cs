using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>
    /// Analyses one song at a time, in Windows background mode, pausing while the window is busy and taking the shared
    /// library-read gate per song. The waits and progress run on the caller's (UI) context.
    /// </summary>
    internal static class StatsDetectLoop
    {
        /// <returns>The songs whose analysis did not complete.</returns>
        public static async Task<List<string>> RunAsync(IReadOnlyList<string> rels, Func<string, bool> detect, Func<bool> isIdle,
            Func<CancellationToken, Task> waitUntilIdle, LibraryReadGate gate, CancellationToken ct, Action<int, int>? progress = null)
        {
            var failed = new List<string>();
            for (int i = 0; i < rels.Count; i++)
            {
                string rel = rels[i];
                bool ok = false;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    await waitUntilIdle(ct);
                    await gate.EnterAsync(ct);
                    if (isIdle()) break;
                    gate.Exit(); // something started while we waited for the gate: wait again
                }
                try
                {
                    ok = await Task.Run(() => { using (ThreadPriorityScope.Background()) return detect(rel); });
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { ok = false; }
                finally { gate.Exit(); }
                if (!ok) failed.Add(rel);
                progress?.Invoke(i + 1, rels.Count);
            }
            return failed;
        }
    }
}
