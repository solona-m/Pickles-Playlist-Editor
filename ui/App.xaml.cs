using Microsoft.UI.Xaml;
using Pickles_Playlist_Editor.Tools;

namespace Pickles_Playlist_Editor
{
    public partial class App : Application
    {
        public static MainWindow MainWindow { get; private set; } = null!;

        public App()
        {
            this.InitializeComponent();

            // WinUI 3 routes exceptions thrown on the UI thread (click handlers, dispatcher
            // callbacks, binding evaluation) here — NOT to AppDomain.UnhandledException.
            // Without this hookup those crashes terminated the process silently, leaving no
            // crash.log at all, which is exactly what made the playback crash unreportable.
            this.UnhandledException += OnUnhandledException;
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Utils.Logger.LogCrash("Application.UnhandledException", e.Exception ?? (object)e.Message);
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                YtDlpService.StartCookieListener();
                MainWindow = new MainWindow();
                MainWindow.Activate();
            }
            catch (System.Exception ex)
            {
                Utils.Logger.LogCrash("OnLaunched", ex);
                throw;
            }
        }
    }
}
