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
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            YtDlpService.StartCookieListener();
            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
    }
}
