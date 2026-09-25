using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Tex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Pickles_Playlist_Editor
{
    /// <summary>
    /// Putting your own pictures on the DJ table and the laptop screen.
    ///
    /// A separate dialog rather than a page of Settings for the reason the dances dialog is one: it
    /// edits the VFX mod, not the music mod the rest of the app writes to, and that is worth stating
    /// on screen rather than leaving to be inferred.
    ///
    /// The important promise this surface makes is that nothing is written until Apply. Choosing a
    /// picture composites it and shows the real, compressed result in the preview; only Apply and
    /// Restore touch the mod. WinUI allows one ContentDialog at a time, so confirmations here are
    /// Win32 message boxes, as they are everywhere else in this app.
    /// </summary>
    public sealed partial class TexturesDialog : ContentDialog
    {
        /// <summary>One surface on screen: its controls, what it shows now, and what is pending.</summary>
        private sealed class Card
        {
            public required string Key { get; init; }
            public required Image Preview { get; init; }
            public required TextBlock State { get; init; }
            public required ComboBox Fit { get; init; }
            public required Button Choose { get; init; }
            public required Button Restore { get; init; }

            public DjTextureTarget? Target { get; set; }

            /// <summary>Why this surface is not editable in the chosen mod, when it is not.</summary>
            public string? Problem { get; set; }

            /// <summary>The picture the user chose, at its own size, before any fitting.</summary>
            public BgraImage? Picked { get; set; }

            public string PickedName { get; set; } = string.Empty;

            public bool HasPending => Picked != null;
        }

        private readonly Dictionary<string, Card> _cards = new(StringComparer.OrdinalIgnoreCase);
        private List<DjTextureModCandidate> _candidates = new();
        private string _modFolder = string.Empty;

        /// <summary>True while a composite or a write is in flight, so a second click cannot start one.</summary>
        private bool _busy;

        /// <summary>
        /// False until the constructor has finished wiring everything up.
        ///
        /// Guards the SelectionChanged handlers. A control that raises one while the XAML is still
        /// being parsed reaches handlers whose other controls do not exist yet, and WinUI reports the
        /// resulting null reference only as "XAML parsing failed" with no inner detail at all.
        /// </summary>
        private bool _ready;

        public TexturesDialog()
        {
            this.InitializeComponent();

            _cards["table"] = new Card
            {
                Key = "table",
                Preview = TablePreview,
                State = TableState,
                Fit = TableFit,
                Choose = TableChoose,
                Restore = TableRestore,
            };
            _cards["laptop"] = new Card
            {
                Key = "laptop",
                Preview = LaptopPreview,
                State = LaptopState,
                Fit = LaptopFit,
                Choose = LaptopChoose,
                Restore = LaptopRestore,
            };
            _cards["lidlogo"] = new Card
            {
                Key = "lidlogo",
                Preview = LidPreview,
                State = LidState,
                Fit = LidFit,
                Choose = LidChoose,
                Restore = LidRestore,
            };

            PrimaryButtonText = AppStrings.Textures_Apply;
            IsPrimaryButtonEnabled = false;

            foreach (var card in _cards.Values) card.Fit.SelectedIndex = 0;   // Fill
            _ready = true;

            LoadMod();
        }

        // ---- choosing the mod --------------------------------------------------------------------

        private void LoadMod()
        {
            string penumbra = Settings.PenumbraLocation ?? string.Empty;
            string folder = Settings.DanceModName;

            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(Path.Combine(penumbra, folder)))
            {
                ShowPicker();
                return;
            }

            Start(() => UseModAsync(folder), "Loading the table pictures");
        }

        /// <summary>
        /// Points the dialog at a mod and draws both cards from it.
        ///
        /// The reads are off the UI thread because they are not small: the laptop atlas is 22 MB, and
        /// working out whether it is still the file this app wrote means hashing all of it. Done
        /// inline, opening the dialog froze the window for as long as that took.
        /// </summary>
        private async Task UseModAsync(string folder)
        {
            if (_busy) return;

            string penumbra = Settings.PenumbraLocation ?? string.Empty;
            string root = Path.Combine(penumbra, folder);

            // Busy for the whole load, not just the scan. This is the slowest thing the dialog does
            // — deciding whether a surface is still the file we wrote means hashing 22 MB, and then
            // both previews are read and decoded — and the cards become visible partway through. A
            // click on the finished card while the other was still loading used to interleave with
            // this method's own enable and disable writes.
            SetBusy(true);
            try
            {
                List<DjTextureTarget> targets;
                List<DjTextureProblem> problems;
                string displayName;
                try
                {
                    (targets, problems, displayName) = await Task.Run(() =>
                    {
                        // Pointing somewhere new: any cached baseline belongs to the old mod.
                        DjTextures.Forget();
                        var found = DjTextures.Locate(root, folder, out var reasons);
                        return (found, reasons, PenumbraOptions.DisplayName(root));
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogWarn("Reading textures from {Mod} failed: {Error}", folder, ex.Message);
                    SetBusy(false);
                    StatusText.Text = ex.Message;
                    ShowPicker();
                    return;
                }

                if (targets.Count == 0)
                {
                    // Not an error on its own: most VFX mods are not this one. The picker says so.
                    SetBusy(false);
                    ModText.Text = AppStrings.TextureModNoPictures(displayName);
                    ShowPicker();
                    return;
                }

                _modFolder = folder;
                ModText.Text = displayName;
                PickerSection.Visibility = Visibility.Collapsed;
                CardsSection.Visibility = Visibility.Visible;
                StatusText.Text = string.Empty;

                foreach (var card in _cards.Values)
                {
                    card.Target = targets.FirstOrDefault(t =>
                        string.Equals(t.Surface.Key, card.Key, StringComparison.OrdinalIgnoreCase));
                    card.Problem = card.Target != null ? null
                        : problems.FirstOrDefault(p =>
                            string.Equals(p.Surface.Key, card.Key, StringComparison.OrdinalIgnoreCase))?.Reason
                          ?? AppStrings.Textures_NotInThisMod;
                    card.Picked = null;
                    card.PickedName = string.Empty;
                    await RefreshCardAsync(card);
                }
            }
            finally
            {
                // In a finally so a failure drawing a card cannot leave the dialog permanently busy.
                SetBusy(false);
            }
        }

        /// <summary>
        /// Goes back to choosing a mod, abandoning anything picked for the one on screen.
        ///
        /// Clearing the pending pictures is the point, not tidiness. The Apply button lives in the
        /// dialog footer and stays visible while the picker is up, so leaving a choice pending here
        /// would leave an enabled Apply pointing at the mod the user has just navigated away from —
        /// one click from writing to a mod they are no longer looking at.
        /// </summary>
        private void ShowPicker()
        {
            foreach (var card in _cards.Values)
            {
                card.Picked = null;
                card.PickedName = string.Empty;
            }
            UpdateApplyButton();

            CardsSection.Visibility = Visibility.Collapsed;
            PickerSection.Visibility = Visibility.Visible;
            StatusText.Text = AppStrings.Textures_ScanRunning;

            string penumbra = Settings.PenumbraLocation ?? string.Empty;
            string preferred = Settings.DanceModName;

            _ = Task.Run(() =>
            {
                List<DjTextureModCandidate> found;
                try
                {
                    found = DjTextures.FindCandidates(penumbra, preferred);
                }
                catch (Exception ex)
                {
                    Logger.LogWarn("Texture mod scan failed: {Error}", ex.Message);
                    found = new List<DjTextureModCandidate>();
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    _candidates = found;
                    CandidateList.Items.Clear();
                    foreach (var candidate in found)
                        CandidateList.Items.Add(
                            AppStrings.TextureModCandidate(candidate.Name, candidate.SurfaceCount));

                    StatusText.Text = found.Count == 0
                        ? AppStrings.Textures_NoModsFound
                        : AppStrings.TextureModsFound(found.Count);
                });
            });
        }

        private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _busy) return;
            int at = CandidateList.SelectedIndex;
            if (at < 0 || at >= _candidates.Count) return;

            // Not saved to Settings.DanceModName. That setting means "the mod holding my dances", and
            // a DJ whose table art lives in a second pack would otherwise have their dances quietly
            // repointed at it by opening this dialog.
            Start(() => UseModAsync(_candidates[at].Folder), "Loading the table pictures");
        }

        private void ChangeModButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy) ShowPicker();
        }

        // ---- picking a picture -------------------------------------------------------------------

        private void Choose_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || sender is not FrameworkElement { Tag: string key }) return;
            if (!_cards.TryGetValue(key, out var card) || card.Target == null) return;

            using var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = AppStrings.Textures_PickTitle,
                Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)"
                    + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            if (dialog.ShowDialog(new Win32Window(OwnerWindow())) != System.Windows.Forms.DialogResult.OK)
                return;

            Start(() => TakePictureAsync(card, dialog.FileName), "Reading the chosen picture");
        }

        private void Preview_DragOver(object sender, DragEventArgs e)
        {
            if (_busy || sender is not FrameworkElement { Tag: string key }) return;
            if (!_cards.TryGetValue(key, out var card) || card.Target == null) return;
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

            e.AcceptedOperation = DataPackageOperation.Copy;

            // Absent for some drag sources, and reaching through it unchecked turns a drag from one
            // of them into a crash rather than a drop that simply has no caption.
            if (e.DragUIOverride != null) e.DragUIOverride.Caption = AppStrings.Textures_DropCaption;
        }

        private async void Preview_Drop(object sender, DragEventArgs e)
        {
            if (_busy || sender is not FrameworkElement { Tag: string key }) return;
            if (!_cards.TryGetValue(key, out var card) || card.Target == null) return;

            try
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

                var items = await e.DataView.GetStorageItemsAsync();
                var dropped = items.FirstOrDefault();
                if (dropped == null) return;

                // DragOver already showed the copy cursor, so saying nothing here reads as the drop
                // having silently failed. A folder is the common way to land in this branch.
                if (dropped is not StorageFile file)
                {
                    StatusText.Text = AppStrings.TexturesUnreadable(dropped.Name);
                    return;
                }

                await TakePictureAsync(card, file.Path);
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Dropping a picture failed: {Error}", ex.Message);
                StatusText.Text = ex.Message;
            }
        }

        /// <summary>Loads the picture, then shows what it would look like on the surface.</summary>
        private async Task TakePictureAsync(Card card, string path)
        {
            try
            {
                var picture = await LoadPictureAsync(path);
                if (picture == null)
                {
                    StatusText.Text = AppStrings.TexturesUnreadable(Path.GetFileName(path));
                    return;
                }

                card.Picked = picture;
                card.PickedName = Path.GetFileName(path);
                await RefreshPreviewAsync(card);
                UpdateApplyButton();
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Reading '{File}' failed: {Error}", path, ex.Message);
                StatusText.Text = AppStrings.TexturesUnreadable(Path.GetFileName(path));
            }
        }

        private async void Fit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || sender is not FrameworkElement { Tag: string key }) return;
            if (!_cards.TryGetValue(key, out var card) || !card.HasPending) return;

            await RefreshPreviewAsync(card);
        }

        // ---- previewing and applying ---------------------------------------------------------------

        /// <summary>
        /// Shows the pending picture as the game would draw it.
        ///
        /// The composite runs off the UI thread because it is a real paste into a real texture — the
        /// laptop atlas is 22 MB — and it is the compressed result that goes on screen rather than
        /// the picture as chosen. A photograph that turns to mush at panel size should look like mush
        /// here, before it reaches the game.
        /// </summary>
        private async Task RefreshPreviewAsync(Card card)
        {
            if (card.Target is not { } target || card.Picked is not { } picture) return;

            var fit = SelectedFit(card);
            SetBusy(true);
            StatusText.Text = AppStrings.Textures_Composing;

            BgraImage? preview;
            string error;
            try
            {
                (preview, error) = await Task.Run(() =>
                {
                    var result = DjTextures.Preview(target, picture, fit, out string message);
                    return (result, message);
                });
            }
            finally
            {
                // In a finally throughout this dialog: a throw that skipped the release would leave
                // every control greyed out for the rest of the visit with no way back.
                SetBusy(false);
            }

            if (preview == null)
            {
                card.Picked = null;
                await RefreshCardAsync(card);
                StatusText.Text = error;
                UpdateApplyButton();
                return;
            }

            StatusText.Text = string.Empty;
            card.Preview.Source = ToBitmap(preview);
            // SetBusy ran above with Picked already set, so Restore has picked up its second
            // job: with a picture pending but unwritten, that button cancels the choice rather
            // than undoing anything on disk.
            card.State.Text = AppStrings.TexturesPending(card.PickedName);
        }

        private void PrimaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // The dialog stays open: applying is not the end of the visit, and closing it would hide
            // the previews that are the only confirmation the right thing was written.
            args.Cancel = true;
            Start(ApplyAsync, "Writing the table pictures");
        }

        private async Task ApplyAsync()
        {
            var pending = _cards.Values.Where(c => c.HasPending && c.Target != null).ToList();
            if (_busy || pending.Count == 0) return;

            SetBusy(true);
            StatusText.Text = AppStrings.Textures_Applying;

            try
            {
            var failures = new List<string>();
            var results = new Dictionary<Card, BgraImage?>();
            int written = 0;

            foreach (var card in pending)
            {
                var target = card.Target!;
                var picture = card.Picked!;
                var fit = SelectedFit(card);

                var (ok, image, message) = await Task.Run(() =>
                {
                    bool applied = DjTextures.Apply(target, picture, fit, out var result, out string error);
                    return (applied, result, error);
                });

                if (!ok)
                {
                    failures.Add(message);
                    continue;
                }

                written++;
                results[card] = image;
                card.Picked = null;
                card.PickedName = string.Empty;
            }

            if (written > 0) await Task.Run(() => DjTextures.Reload(_modFolder));

            SetBusy(false);

            // Written cards redraw from what the apply handed back; only the untouched ones are
            // worth a read from disk. A write that somehow could not be read back falls through to
            // the disk path rather than showing a blank card under a successful apply.
            foreach (var card in _cards.Values)
            {
                if (results.TryGetValue(card, out var image) && image != null) ShowCard(card, image, null);
                else await RefreshCardAsync(card);
            }
            UpdateApplyButton();

            if (failures.Count > 0)
                MessageBox(OwnerWindow(), string.Join("\n\n", failures),
                    AppStrings.Dlg_Textures_Title, 0x00000010); // MB_ICONERROR
            else
                StatusText.Text = AppStrings.TexturesApplied(written);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || sender is not FrameworkElement { Tag: string key }) return;
            if (!_cards.TryGetValue(key, out var card) || card.Target is not { } target) return;

            if (card.HasPending)
            {
                // Dropping an unapplied choice is not the same act as undoing a written one, and
                // doing both on one click would throw away work without saying so.
                card.Picked = null;
                card.PickedName = string.Empty;
                await RefreshCardAsync(card);
                UpdateApplyButton();
                return;
            }

            if (!target.IsCustomised) return;

            const uint YesNo = 0x00000004 | 0x00000020;  // MB_YESNO | MB_ICONQUESTION
            if (MessageBox(OwnerWindow(), AppStrings.Textures_ConfirmRestore,
                    AppStrings.Dlg_Textures_Title, YesNo) != 6) // IDYES
                return;

            Start(() => RestoreAsync(card, target), "Restoring the shipped picture");
        }

        private async Task RestoreAsync(Card card, DjTextureTarget target)
        {
            SetBusy(true);
            StatusText.Text = AppStrings.Textures_Restoring;

            try
            {
                var (ok, error) = await Task.Run(() =>
                {
                    bool restored = DjTextures.Restore(target, out string message);
                    if (restored) DjTextures.Reload(_modFolder);
                    return (restored, message);
                });

                SetBusy(false);
                await RefreshCardAsync(card);
                UpdateApplyButton();

                if (ok) StatusText.Text = AppStrings.Textures_Restored;
                else MessageBox(OwnerWindow(), error, AppStrings.Dlg_Textures_Title, 0x00000010);
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ---- drawing the cards ---------------------------------------------------------------------

        /// <summary>Draws the card from what is actually on the surface right now.</summary>
        private async Task RefreshCardAsync(Card card)
        {
            if (card.Target is not { } target)
            {
                ShowCard(card, null, null);
                return;
            }

            var (current, error) = await Task.Run(() =>
            {
                var image = DjTextures.ReadCurrent(target, out string message);
                return (image, message);
            });

            ShowCard(card, current, error);
        }

        /// <summary>
        /// Puts an already-known picture on the card.
        ///
        /// Split out so an apply does not have to read the file it has just written back off disk —
        /// 22 MB for the laptop, and the bytes were in memory a moment earlier.
        /// </summary>
        private void ShowCard(Card card, BgraImage? image, string? error)
        {
            if (card.Target is not { } target)
            {
                card.Preview.Source = null;
                card.State.Text = card.Problem ?? AppStrings.Textures_NotInThisMod;
                card.Choose.IsEnabled = false;
                card.Fit.IsEnabled = false;
                card.Restore.IsEnabled = false;
                return;
            }

            card.Preview.Source = image == null ? null : ToBitmap(image);
            card.State.Text = image == null ? (error ?? string.Empty)
                : target.IsCustomised
                    ? AppStrings.TexturesYours(target.Surface.UprightWidth, target.Surface.UprightHeight)
                    : AppStrings.TexturesShipped(target.Surface.UprightWidth, target.Surface.UprightHeight);

            card.Choose.IsEnabled = !_busy;
            card.Fit.IsEnabled = !_busy;
            card.Restore.IsEnabled = !_busy && target.IsCustomised;
        }

        private void UpdateApplyButton() =>
            IsPrimaryButtonEnabled = !_busy && _cards.Values.Any(c => c.HasPending && c.Target != null);

        /// <summary>
        /// Greys the controls out while a composite or a write is running.
        ///
        /// Control by control rather than on the section that holds them: a Panel has no IsEnabled,
        /// and wrapping the layout in something that does would put a second focus stop around
        /// content the user tabs through.
        ///
        /// The mod picker is in here too, not just the cards. Switching mods mid-write reassigns
        /// every target and the mod folder underneath the operation in flight, which ends with the
        /// reload going to the newly chosen mod while the one actually written never gets told.
        /// </summary>
        private void SetBusy(bool busy)
        {
            _busy = busy;
            ChangeModButton.IsEnabled = !busy;
            CandidateList.IsEnabled = !busy;

            foreach (var card in _cards.Values)
            {
                bool usable = !busy && card.Target != null;
                card.Choose.IsEnabled = usable;
                card.Fit.IsEnabled = usable;
                card.Restore.IsEnabled = usable && (card.HasPending || card.Target!.IsCustomised);
            }
            UpdateApplyButton();
        }

        private static FitMode SelectedFit(Card card) => card.Fit.SelectedIndex switch
        {
            1 => FitMode.Fit,
            2 => FitMode.Stretch,
            _ => FitMode.Fill,
        };

        // ---- pixels in and out ---------------------------------------------------------------------

        /// <summary>
        /// Reads a picture file into the format the texture code works in.
        ///
        /// Capped on the way in rather than after decoding: this is an x86 process, and a modern
        /// phone photograph at full size is a 100 MB buffer for a picture that ends up 656 pixels
        /// wide. The cap is generous next to the panels and costs nothing at ordinary sizes.
        ///
        /// The scaling is expressed in RAW pixels and the result reported in ORIENTED ones, which is
        /// the whole subtlety here. BitmapTransform runs before the EXIF rotation, so scaling to the
        /// oriented size squashes a portrait phone photo into its own landscape frame and then turns
        /// it — and because the two sizes multiply to the same buffer length, nothing throws. It
        /// simply comes back diagonally sheared.
        ///
        /// That arithmetic lives in <see cref="PictureLoadPlan"/> so the harness can exercise it
        /// without an image decoder; what is left here is the platform call it feeds.
        /// </summary>
        private static async Task<BgraImage?> LoadPictureAsync(string path)
        {
            const uint MaxEdge = 2048;

            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            var plan = PictureLoadPlan.For(
                decoder.PixelWidth, decoder.PixelHeight, decoder.OrientedPixelWidth, MaxEdge);
            if (plan.OutputWidth <= 0 || plan.OutputHeight <= 0) return null;

            var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
            if (plan.Scale)
            {
                transform.ScaledWidth = plan.ScaledWidth;
                transform.ScaledHeight = plan.ScaledHeight;
            }

            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);

            var data = pixels.DetachPixelData();
            if (data.Length < plan.RequiredBytes) return null;

            return new BgraImage(plan.OutputWidth, plan.OutputHeight, data);
        }

        private static WriteableBitmap ToBitmap(BgraImage image)
        {
            var bitmap = new WriteableBitmap(image.Width, image.Height);
            using (var stream = bitmap.PixelBuffer.AsStream())
                stream.Write(image.Pixels, 0, image.Pixels.Length);
            bitmap.Invalidate();
            return bitmap;
        }

        // ---- plumbing ------------------------------------------------------------------------------

        /// <summary>
        /// Starts work this dialog does not await, with somewhere for its failures to go.
        ///
        /// Every discarded task would otherwise be an unobserved fault: no log line, no message, and
        /// a button that simply does nothing when clicked. MainWindow says the same thing about the
        /// call that opens this dialog; these are the same hazard one level down.
        /// </summary>
        private void Start(Func<Task> work, string what)
        {
            _ = Observe(work, what);
        }

        private async Task Observe(Func<Task> work, string what)
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                Logger.LogError("{What} failed: {Error}", what, ex);

                // The dialog may already be gone, which is the one case where reporting must not
                // itself throw.
                try { StatusText.Text = ex.Message; } catch { }
            }
        }

        private static IntPtr OwnerWindow() =>
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);

        private sealed class Win32Window(IntPtr handle) : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; } = handle;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
    }
}
