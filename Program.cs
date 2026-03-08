using Microsoft.UI.Xaml;
using System;
using System.IO;
using Velopack;

namespace Pickles_Playlist_Editor
{
    internal class Program
    {
        static readonly string CrashLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PicklesPlaylistEditor", "crash.log");

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                LogCrash("AppDomain: " + (e.ExceptionObject?.ToString() ?? "unknown"));

            try
            {
                TryRunVelopack();

                global::WinRT.ComWrappersSupport.InitializeComWrappers();
                global::Microsoft.UI.Xaml.Application.Start((p) =>
                {
                    try
                    {
                        var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                            global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                        global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                        _ = new App();
                    }
                    catch (Exception ex)
                    {
                        LogCrash("Application.Start callback: " + ex);
                        global::System.Environment.Exit(1);
                    }
                });
            }
            catch (Exception ex)
            {
                LogCrash("Main: " + ex);
                global::System.Environment.Exit(1);
            }
        }

        static void TryRunVelopack()
        {
            try
            {
                VelopackApp.Build().Run();
            }
            catch (Exception ex)
            {
                // Update checks should never prevent the app from launching.
                LogCrash("Velopack: " + ex);
            }
        }

        static void LogCrash(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
                File.WriteAllText(CrashLogPath, $"[{DateTime.Now}]\n{message}\n");
            }
            catch { }
        }
    }
}
