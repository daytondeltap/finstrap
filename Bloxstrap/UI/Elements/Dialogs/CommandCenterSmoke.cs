using System.IO.Compression;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bloxstrap.Integrations;
using Bloxstrap.UI.Elements.Settings.Pages;
using Bloxstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class LaunchMenuDialog
    {
        internal static bool SmokeMode;
        internal static async Task RunSmokeAsync(string directory)
        {
            SmokeMode = true;
            await VerifySessionSettingsAsync();
            VerifyFastFlagEditor();
            await VerifyModPackPageAsync(directory);
            VerifyRobloxVersionsPage();
            var first = new SessionProfile { UserId = 123, Username = "SmokeOne", Name = "First account" };
            var second = new SessionProfile { UserId = 456, Username = "SmokeTwo", Name = "Second account" };
            App.Settings.Prop.Theme = Enums.Theme.Dark;
            App.Settings.Prop.MultiInstance = true;
            App.Settings.Prop.SessionProfiles.Add(first);
            App.Settings.Prop.SessionProfiles.Add(second);
            var window = new LaunchMenuDialog();
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            try
            {
                void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
                Check(window.Cards.Count() == 2, "Account cards did not load.");
                window.ServerCountry.SelectedValue = "SG";
                Check(window.SelectedServerCountry == "SG" && App.Settings.LastSaveSucceeded, "Server country selection was not saved.");
                window.AccountDestination.Text = "0";
                await window.LaunchAccountsAsync(new[] { first });
                Check(window.GameFinder.Visibility == Visibility.Visible && window._pendingGameLaunch is not null,
                    "A filtered Home launch did not open the game finder in the dashboard.");
                Check(Application.Current.Windows.OfType<AccountSignInDialog>().Count() == 0,
                    "A filtered Home launch opened a separate sign-in window.");
                window.CloseGameFinder_Click(window, new RoutedEventArgs());
                window.GameQuery.Text = "1537690962";
                window.ShowGameFinder();
                await window.SearchGamesAsync();
                await window.UseSelectedGameAsync();
                Check(window.AccountDestination.Text == "1537690962" && window.GameFinder.Visibility == Visibility.Collapsed,
                    "The in-app game finder did not select the entered place ID.");
                var persisted = new JsonManager<Bloxstrap.Models.Persistable.Settings>();
                Check(persisted.Load() && persisted.Prop.PreferredServerCountry == "SG", "Server country was not restored from settings.");
                window.ServerCountry.SelectedValue = "";
                Check(window.SelectedServerCountry is null && App.Settings.LastSaveSucceeded, "Any region did not clear the saved filter.");
                string profileKey = Guid.NewGuid().ToString("N");
                window.SaveVerifiedAccount(first.UserId, first.Username, profileKey, new());
                var savedSettings = new JsonManager<Models.Persistable.Settings>();
                Check(savedSettings.Load() && savedSettings.Prop.SessionProfiles.Single(p => p.UserId == first.UserId).LaunchProfileKey == profileKey,
                    "A verified account must be saved before requesting its launch ticket.");
                await VerifyBackgroundAccountProfileAsync(window, first);
                foreach (var card in window.Cards) window.AccountsList.SelectedItems.Add(card);
                window.GroupName.Text = "Smoke group";
                window.GroupChannel.Text = "Production";
                window.AccountDestination.Text = "456";
                window.SaveGroup_Click(window, new RoutedEventArgs());
                Check(App.Settings.LastSaveSucceeded, "Group save failed.");
                var group = App.Settings.Prop.LaunchGroups.Single();
                Check(group.Accounts.SequenceEqual(new long[] { 123, 456 }), "Group ordering was not persisted.");
                Check(group.Destination == "456" && group.PlayerChannel == "production", "Group destination or channel was not persisted.");
                var rejoinButton = new System.Windows.Controls.Button { DataContext = window.Cards.First() };
                window.RejoinCard_Click(rejoinButton, new RoutedEventArgs());
                Check(window.AccountStatus.Text.Contains("No public server has been recorded"), "An account with no history offered a rejoin.");
                window.AccountsList.SelectedItems.Clear();
                window.AccountsList.SelectedItem = window.Cards.Last();
                window.Move(-1);
                Check(window.Cards.First().Profile.UserId == 456, "Account ordering failed.");
                window.Queue.Begin(new long[] { 123, 456 });
                window.Queue.Set(123, AccountLaunchState.Ready);
                window.CancelRemaining_Click(window, new RoutedEventArgs());
                window.Queue.Finish();
                Check(window.Queue.States[123] == AccountLaunchState.Ready && window.Queue.States[456] == AccountLaunchState.Cancelled, "Queue cancellation failed.");

                foreach (var theme in new[] { Enums.Theme.Dark, Enums.Theme.Light })
                {
                    App.Settings.Prop.Theme = theme;
                    window.ApplyTheme();
                    await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();

                    var expected = (Color)window.FindResource("ApplicationBackgroundColor");
                    Check(window.DashboardSurface.Background is SolidColorBrush background && background.Color == expected && expected.A == 255,
                        $"{theme}: dashboard surface must use the opaque Bloxstrap background.");
                    foreach (var foreground in new[] { window.RootTitleBar.Foreground, window.AccountHint.Foreground, window.AccountDestination.Foreground, window.LaunchAll.Foreground })
                    {
                        Check(foreground is SolidColorBrush text && (theme == Enums.Theme.Dark ? text.Color.R > 192 : text.Color.R < 64),
                            $"{theme}: dashboard labels and controls must use contrasting text.");
                    }

                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    byte[] pixel = new byte[4];
                    foreach (var point in new[]
                    {
                        window.RootTitleBar.TranslatePoint(new Point(200, 12), window),
                        window.DashboardSurface.TranslatePoint(new Point(16, 12), window)
                    })
                    {
                        bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
                        Check(pixel.SequenceEqual(new[] { expected.B, expected.G, expected.R, expected.A }), $"{theme}: rendered title bar and dashboard backgrounds must match the selected theme.");
                    }
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(directory, theme == Enums.Theme.Dark ? "dashboard.png" : "dashboard-light.png"));
                    encoder.Save(output);
                }

                File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: mod pack imported, previewed and saved, session switches persisted and reloaded, Roblox build pinned and cleared, filtered Home stays in the dashboard, saved account profile opens without navigation and stays isolated, empty rejoin checked, group and channel saved, accounts reordered, queue cancelled, dark and light themes rendered with readable controls.");
            }
            finally
            {
                App.Settings.Prop.Theme = Enums.Theme.Dark;
                window.Close();
            }
        }

        private static async Task VerifyBackgroundAccountProfileAsync(LaunchMenuDialog window, SessionProfile first)
        {
            var second = new SessionProfile { Name = "Unlinked test account", LaunchProfileKey = Guid.NewGuid().ToString("N") };
            using (var seed = new Microsoft.Web.WebView2.Wpf.WebView2())
            {
                window.AccountSessionHost.Children.Add(seed);
                try
                {
                    await RobloxWebProfile.InitializeAsync(seed, first.LaunchProfileKey!, default);
                    var manager = seed.CoreWebView2.CookieManager;
                    var cookie = manager.CreateCookie(".ROBLOSECURITY", "test-cookie", ".roblox.com", "/");
                    cookie.IsSecure = true;
                    cookie.Expires = DateTime.UtcNow.AddMinutes(10);
                    manager.AddOrUpdateCookie(cookie);
                    if (seed.Source is { AbsoluteUri: not "about:blank" })
                        throw new InvalidOperationException("The saved account profile navigated to a web page.");
                }
                finally { window.AccountSessionHost.Children.Remove(seed); }
            }

            using var linked = await window.OpenLinkedClientAsync(first, default);
            try
            {
                using var other = await window.OpenLinkedClientAsync(second, default);
            }
            catch (FormatException ex) when (ex.Message.Contains("Reconnect"))
            {
                return;
            }
            throw new InvalidOperationException("A different WebView profile could read the linked account cookie.");
        }

        private static void VerifyRobloxVersionsPage()
        {
            const string buildId = "version-0000000000000001";
            string directory = Path.Combine(Paths.Versions, buildId);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, PlayerBuilds.ExecutableName), "");
            PlayerBuilds.Save(directory, new DistributionState
            {
                VersionGuid = buildId,
                KeepPlayerBuild = true,
                PackageHashes = new() { ["RobloxApp.zip"] = "test" }
            });

            var page = new RobloxVersionsPage();
            page.BuildChoice.Text = buildId;
            page.PinButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var persisted = new JsonManager<Models.Persistable.Settings>();
            if (!persisted.Load() || persisted.Prop.PinnedPlayerBuild != buildId)
                throw new InvalidOperationException("The Roblox version pin did not persist.");

            page.AutomaticButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (!persisted.Load() || persisted.Prop.PinnedPlayerBuild is not null || persisted.Prop.PlayerChannel is not null)
                throw new InvalidOperationException("Automatic did not clear the saved version pin.");
        }

        private static async Task VerifyModPackPageAsync(string directory)
        {
            string path = Path.Combine(directory, "smoke-mod.zip");
            using (var archive = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            {
                using var source = Resource.GetStream("Cursor.From2006.ArrowCursor.png");
                using var target = archive.CreateEntry(ModPackStore.SupportedTargets[0]).Open();
                source.CopyTo(target);
            }

            var page = new ModsPage();
            var host = new Window { Content = new Frame { Content = page } };
            host.Show();
            try
            {
                await page.ImportPackAsync(path);
                if (page.PackImagePreview.Source is null || App.Settings.Prop.ModPacks.Single().Enabled)
                    throw new InvalidOperationException("Imported pack did not display its cursor or was enabled by default.");

                page.TogglePackButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                new MainWindowViewModel().SaveSettingsCommand.Execute(null);
                var persisted = new JsonManager<Models.Persistable.Settings>();
                if (!App.Settings.LastSaveSucceeded || !persisted.Load() || !persisted.Prop.ModPacks.Single().Enabled)
                    throw new InvalidOperationException("The enabled mod pack did not survive a save and reload.");
            }
            finally { host.Close(); File.Delete(path); }
        }

        private static void VerifyFastFlagEditor()
        {
            const string flag = "DFIntTextureQualityOverride";
            var editor = new FastFlagEditorPage();
            var preview = new FastFlagImportDialog(new[] { new FastFlagImportRow(flag, "2", "New") }, "Player");
            preview.Close();
            App.FastFlags.SetValue(flag, "2");
            App.StudioFastFlags.SetValue(flag, "3");
            App.FastFlags.Save();
            App.StudioFastFlags.Save();
            if (!App.FastFlags.Load() || !App.StudioFastFlags.Load() ||
                App.FastFlags.GetValue(flag) != "2" || App.StudioFastFlags.GetValue(flag) != "3" ||
                FastFlagManager.GetValidationError(flag, "bad value") is null ||
                !FastFlagManager.PlayerAllowlist.Contains(flag))
                throw new InvalidOperationException("Fast Flag target isolation or value validation failed.");
        }

        private static async Task VerifySessionSettingsAsync()
        {
            static IEnumerable<ToggleSwitch> GetSessionSwitches(IntegrationsPage page) =>
                new[] { page.MultiInstanceOption, page.KeepAwakeOption, page.AntiIdleOption }.Select(option => (ToggleSwitch)option.InnerContent);

            var page = new IntegrationsPage();
            var host = new Window { Content = new Frame { Content = page } };
            host.Show();
            try
            {
                await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                foreach (var toggle in GetSessionSwitches(page))
                    ((IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();

                new MainWindowViewModel().SaveSettingsCommand.Execute(null);
                if (!App.Settings.LastSaveSucceeded || !App.Settings.Load() || !App.Settings.Prop.MultiInstance ||
                    !App.Settings.Prop.KeepComputerAwake || !App.Settings.Prop.ForegroundAntiIdle)
                    throw new InvalidOperationException("Roblox session switches did not survive a save and reload.");

                var reopened = new IntegrationsPage();
                host.Content = new Frame { Content = reopened };
                await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (GetSessionSwitches(reopened).Any(toggle => toggle.IsChecked != true))
                    throw new InvalidOperationException("Saved Roblox session switches did not appear on the reopened settings page.");
            }
            finally { host.Close(); }
        }
    }
}
