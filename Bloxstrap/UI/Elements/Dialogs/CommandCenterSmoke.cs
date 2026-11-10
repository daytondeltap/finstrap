using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bloxstrap.Integrations;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class LaunchMenuDialog
    {
        internal static bool SmokeMode;
        internal static async Task RunSmokeAsync(string directory)
        {
            SmokeMode = true;
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
                foreach (var card in window.Cards) window.AccountsList.SelectedItems.Add(card);
                window.GroupName.Text = "Smoke group";
                window.AccountDestination.Text = "456";
                window.SaveGroup_Click(window, new RoutedEventArgs());
                Check(App.Settings.LastSaveSucceeded, "Group save failed.");
                var group = App.Settings.Prop.LaunchGroups.Single();
                Check(group.Accounts.SequenceEqual(new long[] { 123, 456 }), "Group ordering was not persisted.");
                Check(group.Destination == "456", "Group destination was not persisted.");
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

                File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: dashboard opened, cards bound, group saved, accounts reordered, queue cancelled, dark and light themes rendered with readable controls.");
            }
            finally
            {
                App.Settings.Prop.Theme = Enums.Theme.Dark;
                window.Close();
            }
        }
    }
}
