using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Pickles_Playlist_Editor.Tools;
using Pickles_Playlist_Editor.Utils;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    /// <summary>
    /// Hosts a real SoundCloud sign-in page and lifts the resulting `oauth_token` session
    /// cookie out of it, so the user never has to go digging in browser devtools.
    ///
    /// Everything here runs on the UI thread deliberately: WebView2 has thread affinity —
    /// the control must be created on the UI thread and every CoreWebView2 call must happen
    /// on that same thread. Do NOT add ConfigureAwait(false) in this file, even though
    /// YtDlpService uses it everywhere.
    /// </summary>
    public sealed partial class SoundCloudLoginDialog : ContentDialog
    {
        private const string SignInUrl = "https://soundcloud.com/signin";
        private const string CookieOrigin = "https://soundcloud.com";

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _pollTimer;

        /// <summary>Set when sign-in succeeded and the token has been stored.</summary>
        public bool SignedIn { get; private set; }

        public SoundCloudLoginDialog()
        {
            this.InitializeComponent();
            this.Opened += OnOpened;
            this.Closed += OnClosed;
        }

        /// <summary>
        /// True when the WebView2 runtime is present. It ships with Windows 10 20H2 and
        /// later, but an older or stripped machine can be missing it, and asking before
        /// showing the dialog turns a hard crash into an explainable message.
        /// </summary>
        public static bool IsWebViewRuntimeAvailable()
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (Exception ex)
            {
                Logger.LogWarn("WebView2 runtime is not available: {Error}", ex.Message);
                return false;
            }
        }

        private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            StatusLabel.Text = "Loading SoundCloud…";

            try
            {
                // A private user data folder keeps this session out of any other WebView2
                // the app might use later, and gives "Sign out" something concrete to wipe.
                // WinRT projection names this CreateWithOptionsAsync; the .NET (WinForms/WPF)
                // SDK spells the same thing as a 3-arg CreateAsync.
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                    string.Empty, YtDlpService.WebViewDataDirectory, new CoreWebView2EnvironmentOptions());
                await LoginWebView.EnsureCoreWebView2Async(env);

                LoginWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                LoginWebView.CoreWebView2.Navigate(SignInUrl);
                StatusLabel.Text = string.Empty;

                // Navigation events alone are not enough. SoundCloud's sign-in is a
                // single-page app: it authenticates over XHR and changes route with
                // history.pushState, neither of which raises NavigationCompleted. Relying
                // on that event left the window sitting on a logged-in page forever.
                // Polling the cookie store is the only signal that actually tracks the
                // thing we care about. NavigationCompleted stays as an extra nudge so the
                // common case closes immediately rather than up to a second later.
                _pollTimer = this.DispatcherQueue.CreateTimer();
                _pollTimer.Interval = TimeSpan.FromSeconds(1);
                _pollTimer.Tick += async (_, _) => await TryCaptureTokenAsync();
                _pollTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not start the SoundCloud sign-in window: {Error}", ex.Message);
                StatusLabel.Text = "Could not open the sign-in window: " + ex.Message;
            }
        }

        private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            // Tear the browser down explicitly. Left alone it keeps a background process
            // and a lock on the user data folder, which would then defeat Sign Out.
            try
            {
                _pollTimer?.Stop();
                _pollTimer = null;
                if (LoginWebView.CoreWebView2 != null)
                    LoginWebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                LoginWebView.Close();
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Could not close the sign-in browser cleanly: {Error}", ex.Message);
            }
        }

        private async void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
            => await TryCaptureTokenAsync();

        /// <summary>
        /// Looks for the `oauth_token` cookie SoundCloud sets once a login actually
        /// succeeds. Safe to call repeatedly; the first hit stores the token and closes.
        /// </summary>
        private async Task TryCaptureTokenAsync()
        {
            if (SignedIn || LoginWebView.CoreWebView2 == null)
                return;

            try
            {
                // CookieManager reads the browser's real cookie store rather than running
                // script, so an HttpOnly cookie is still visible here.
                var cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync(CookieOrigin);
                var token = cookies.FirstOrDefault(c => string.Equals(c.Name, "oauth_token", StringComparison.Ordinal));
                if (token == null || !LooksLikeToken(token.Value))
                    return;

                SignedIn = true;
                _pollTimer?.Stop();
                Settings.SoundCloudToken = token.Value;
                StatusLabel.Text = "Signed in.";
                Logger.LogInfo("SoundCloud sign-in succeeded; token stored.");
                Hide();
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Could not read the SoundCloud sign-in cookie: {Error}", ex.Message);
            }
        }

        /// <summary>
        /// Cheap sanity check so a placeholder or anonymous value can't be mistaken for a
        /// real session and leave the UI claiming to be signed in. Real tokens look like
        /// "2-294177-123456789-AbCdEfGhIjKlM". yt-dlp does the authoritative validation.
        /// </summary>
        private static bool LooksLikeToken(string? value) =>
            !string.IsNullOrWhiteSpace(value) && value.Length >= 20 && value.Contains('-');
    }
}
