using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bloxstrap.Integrations;
using Microsoft.Win32;

namespace Bloxstrap.UI.Elements.Settings.Pages
{
    public partial class ModsPage
    {
        private sealed record PackRow(ModPackSelection Selection, InstalledModPack? Pack, string Label);

        private readonly MediaPlayer _previewPlayer = new();
        private ModPackStore PackStore => new(Path.Combine(Paths.Base, "ModPacks"));
        private PackRow? SelectedPack => PackList.SelectedItem as PackRow;

        private void InitializePacks()
        {
            Loaded += (_, _) => RefreshPacks();
            Unloaded += (_, _) => _previewPlayer.Close();
            _previewPlayer.MediaFailed += (_, _) => PackStatus.Text = "Windows could not play the sound preview.";
        }

        private void RefreshPacks(Guid? selected = null)
        {
            selected ??= SelectedPack?.Selection.Id;
            var rows = new List<PackRow>();
            foreach (var selection in App.Settings.Prop.ModPacks)
            {
                InstalledModPack? pack = null;
                try { pack = PackStore.Read(selection.Id); }
                catch (Exception ex) when (IsPackError(ex)) { }
                string status = selection.Enabled ? "Enabled" : "Disabled";
                rows.Add(new(selection, pack, $"{rows.Count + 1}. {pack?.Name ?? "Unavailable pack"} — {status}"));
            }
            PackList.ItemsSource = rows;
            PackList.SelectedItem = rows.FirstOrDefault(row => row.Selection.Id == selected) ?? rows.FirstOrDefault();
        }

        private async void ImportPack_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Mod pack ZIP files (*.zip)|*.zip" };
            if (dialog.ShowDialog() != true)
                return;

            await ImportPackAsync(dialog.FileName);
        }

        internal async Task ImportPackAsync(string path)
        {
            ImportPackButton.IsEnabled = false;
            PackStatus.Text = "Checking pack contents…";
            try
            {
                ModPackStore store = PackStore;
                var pack = await Task.Run(() => store.Import(path, (target, data) =>
                {
                    if (target.EndsWith(".png", StringComparison.Ordinal))
                        DecodeCursor(data);
                }));
                App.Settings.Prop.ModPacks.Add(new() { Id = pack.Id });
                RefreshPacks(pack.Id);
                PackStatus.Text = "Pack imported disabled. Enable it and save your settings to use it.";
            }
            catch (Exception ex) when (IsPackError(ex))
            {
                PackStatus.Text = "Could not import pack. " + ex.Message;
            }
            finally { ImportPackButton.IsEnabled = true; }
        }

        private void TogglePack_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedPack is not { } row || (row.Pack is null && !row.Selection.Enabled))
                return;
            row.Selection.Enabled = !row.Selection.Enabled;
            RefreshPacks();
            PackStatus.Text = "Save settings to confirm the change.";
        }

        private void MovePackUp_Click(object sender, RoutedEventArgs e) => MovePack(-1);
        private void MovePackDown_Click(object sender, RoutedEventArgs e) => MovePack(1);

        private void MovePack(int offset)
        {
            if (SelectedPack is not { } row)
                return;
            var packs = App.Settings.Prop.ModPacks;
            int index = packs.IndexOf(row.Selection);
            int next = index + offset;
            if (index < 0 || next < 0 || next >= packs.Count)
                return;
            (packs[index], packs[next]) = (packs[next], packs[index]);
            RefreshPacks(row.Selection.Id);
            PackStatus.Text = "Packs at the top have higher priority. Save settings to confirm the order.";
        }

        private void RemovePack_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedPack is not { } row)
                return;
            App.Settings.Prop.ModPacks.Remove(row.Selection);
            RefreshPacks();
            PackStatus.Text = "Save settings to remove the pack from the list and restore any overridden assets.";
        }

        private void DisablePacks_Click(object sender, RoutedEventArgs e)
        {
            foreach (var selection in App.Settings.Prop.ModPacks)
                selection.Enabled = false;
            RefreshPacks();
            PackStatus.Text = "Save settings to disable every pack.";
        }

        private void PackSelected(object sender, SelectionChangedEventArgs e)
        {
            var row = SelectedPack;
            var pack = row?.Pack;
            PackAssetList.ItemsSource = pack?.Assets.Select(asset => asset.Target).ToArray();
            PackAssetList.SelectedIndex = pack is null ? -1 : 0;
            if (pack is null)
            {
                PackDetails.Text = row is null ? "No packs imported." : "The pack files are unavailable. Disable or remove this entry.";
                return;
            }

            var details = new List<string> { string.Join(" · ", new[] { pack.Name, pack.Author, pack.Version }.OfType<string>()) };
            foreach (var asset in pack.Assets)
            {
                string legacy = Path.Combine(Paths.Modifications, asset.Target.Replace('/', Path.DirectorySeparatorChar));
                var winner = (PackList.ItemsSource as IEnumerable<PackRow>)?.FirstOrDefault(item =>
                    item.Selection.Enabled && item.Pack?.Assets.Any(entry => entry.Target == asset.Target) == true);
                string source = File.Exists(legacy) ? "existing preset or manual override" : winner?.Pack?.Name ?? "original Roblox asset";
                details.Add($"{Path.GetFileName(asset.Target)} — {source}");
            }
            PackDetails.Text = string.Join(Environment.NewLine, details);
        }

        private void PackAssetSelected(object sender, SelectionChangedEventArgs e)
        {
            _previewPlayer.Close();
            PackImagePreview.Source = null;
            PlayPackSound.Visibility = Visibility.Collapsed;
            if (SelectedPack?.Pack is not { } pack || PackAssetList.SelectedItem is not string target)
                return;
            try
            {
                var entry = pack.Assets.Single(asset => asset.Target == target);
                byte[] bytes = PackStore.ReadAsset(pack.Id, entry);
                if (target.EndsWith(".png", StringComparison.Ordinal))
                    PackImagePreview.Source = DecodeCursor(bytes);
                else
                    PlayPackSound.Visibility = Visibility.Visible;
            }
            catch (Exception ex) when (IsPackError(ex)) { PackStatus.Text = "Could not preview asset. " + ex.Message; }
        }

        private void PlayPackSound_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedPack?.Pack is not { } pack || PackAssetList.SelectedItem is not string target)
                return;
            try
            {
                _previewPlayer.Close();
                _previewPlayer.Open(new Uri(PackStore.AssetPath(pack.Id, target)));
                _previewPlayer.Play();
            }
            catch (Exception ex) when (IsPackError(ex)) { PackStatus.Text = "Could not preview sound. " + ex.Message; }
        }

        private static BitmapFrame DecodeCursor(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            return frame;
        }

        private static bool IsPackError(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FormatException or NotSupportedException
                or System.Runtime.InteropServices.COMException;
    }
}
