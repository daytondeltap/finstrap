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
                window.UpdateLayout();
                Check(window.DashboardSurface.Background is SolidColorBrush background && background.Color.A == 255, "Dashboard background must be opaque for readable labels.");
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.Combine(directory, "dashboard.png"))) encoder.Save(output);
                File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: dashboard opened, cards bound, group saved, accounts reordered, queue cancelled, screenshot rendered.");
            }
            finally { window.Close(); }
        }
    }
}
