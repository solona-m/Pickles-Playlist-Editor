using Microsoft.UI.Xaml;
using System;
using System.IO;
using Velopack;

namespace Pickles_Playlist_Editor
{
    internal class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Install before anything else so even a failure during Velopack/startup lands
            // in the log. Covers AppDomain + unobserved task exceptions; the WinUI UI-thread
            // channel is hooked separately in App (Application.UnhandledException).
            Utils.Logger.InstallGlobalExceptionHandlers();

            try
            {
                TryRunVelopack();

                ApplyLanguageOverride();

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
                        LogCrash("Application.Start callback", ex);
                        global::System.Environment.Exit(1);
                    }
                });
            }
            catch (Exception ex)
            {
                LogCrash("Main", ex);
                global::System.Environment.Exit(1);
            }
        }

        // Applies the saved UI language override before any XAML/resources load.
        // Empty setting falls back to the system language.
        static void ApplyLanguageOverride()
        {
            try
            {
                string lang = Settings.Language;
                if (!string.IsNullOrWhiteSpace(lang))
                    global::Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = lang;
            }
            catch (Exception ex)
            {
                LogCrash("Language override", ex);
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
                LogCrash("Velopack", ex);
            }
        }

        static void LogCrash(string source, Exception ex) => Utils.Logger.LogCrash(source, ex);
    }
}
