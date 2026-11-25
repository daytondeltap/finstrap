using System.Windows;
using Bloxstrap.Integrations;
using Bloxstrap.RobloxInterfaces;

namespace Bloxstrap.UI.Elements.Settings.Pages
{
    public partial class RobloxVersionsPage
    {
        private sealed record BuildChoiceItem(string Id, string Version);
        private BuildChoiceItem[]? _history;

        public RobloxVersionsPage()
        {
            InitializeComponent();
            ChannelName.Text = App.Settings.Prop.PlayerChannel ?? "";
            var installed = ReadInstalledState();
            BuildChoice.Text = App.Settings.Prop.PinnedPlayerBuild ?? installed.VersionGuid;
            RefreshInstalled();
        }

        private static DistributionState ReadInstalledState()
        {
            var state = new JsonManager<DistributionState>("PlayerState");
            state.Load(alertFailure: false);
            return state.Prop;
        }

        private void RefreshInstalled()
        {
            var installed = ReadInstalledState();
            var retained = Directory.Exists(Paths.Versions) ? Directory.EnumerateDirectories(Paths.Versions)
                .Select(PlayerBuilds.Read).OfType<DistributionState>().Where(build => build.VersionGuid != installed.VersionGuid).ToArray()
                : Array.Empty<DistributionState>();
            long totalSize = installed.Size + retained.Sum(build => (long)build.Size);
            InstalledBuild.Text = $"Active build: {(String.IsNullOrEmpty(installed.VersionGuid) ? "None" : installed.VersionGuid)}\n" +
                $"Pinned build: {App.Settings.Prop.PinnedPlayerBuild ?? "None"}\n" +
                $"Saved builds: {retained.Length} · Approximate install size: {totalSize / 1024:N0} MB";
            string entered = BuildChoice.Text;
            BuildChoice.ItemsSource = retained.Select(build => new BuildChoiceItem(build.VersionGuid, "Downloaded"))
                .Concat(_history ?? Array.Empty<BuildChoiceItem>()).DistinctBy(build => build.Id).ToArray();
            BuildChoice.Text = entered;
        }

        private string ReadBuildId()
        {
            string id = BuildChoice.Text.Trim().ToLowerInvariant();
            if (!PlayerBuilds.IsBuildId(id))
                throw new FormatException("Select a Windows Player build or enter its full version ID.");
            return id;
        }

        private void SaveSelection(string? channel, string? pin)
        {
            var settings = App.Settings.Prop;
            string? oldChannel = settings.PlayerChannel;
            string? oldPin = settings.PinnedPlayerBuild;
            settings.PlayerChannel = channel;
            settings.PinnedPlayerBuild = pin;
            App.Settings.Save();
            if (!App.Settings.LastSaveSucceeded)
            {
                settings.PlayerChannel = oldChannel;
                settings.PinnedPlayerBuild = oldPin;
                throw new IOException("Finstrap could not save the Roblox version selection.");
            }
            ChannelName.Text = channel ?? "";
            RefreshInstalled();
        }

        private async Task<ClientVersion> LookupChannel(string? channel)
        {
            string path = "/v2/client-version/WindowsPlayer";
            if (channel is not null && channel != Deployment.DefaultChannel)
                path += "/channel/" + channel;

            var version = await Http.GetJson<ClientVersion>("https://clientsettingscdn.roblox.com" + path);
            if (version is null || !PlayerBuilds.IsBuildId(version.VersionGuid))
                throw new InvalidDataException("Roblox did not return a valid build ID for this channel.");
            return version;
        }

        private async Task RunActionAsync(string progress, Func<Task<string>> action)
        {
            VersionControls.IsEnabled = false;
            VersionStatus.Text = progress;
            try
            {
                VersionStatus.Text = await action();
            }
            catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException or InvalidDataException
                or HttpRequestException or NotSupportedException or ChecksumFailedException or ArgumentException or TaskCanceledException
                or JsonException or ICSharpCode.SharpZipLib.Zip.ZipException)
            {
                VersionStatus.Text = ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.NotFound }
                    ? "Roblox did not make this channel or build available. Choose another."
                    : ex.Message;
                App.Logger.WriteException(nameof(RobloxVersionsPage), ex);
            }
            finally
            {
                VersionControls.IsEnabled = true;
            }
        }

        private async void SaveChannel_Click(object sender, RoutedEventArgs e) => await RunActionAsync("Checking channel…", async () =>
        {
            string? channel = PlayerBuilds.ParseChannel(ChannelName.Text);
            if (channel is not null)
                await LookupChannel(channel);
            SaveSelection(channel, null);
            return "Saved for your next Roblox launch.";
        });

        private async void Automatic_Click(object sender, RoutedEventArgs e) => await RunActionAsync("Saving…", () =>
        {
            SaveSelection(null, null);
            return Task.FromResult("Roblox will select the channel at your next launch.");
        });

        private async void CheckChannel_Click(object sender, RoutedEventArgs e) => await RunActionAsync("Checking channel…", async () =>
        {
            string? channel = PlayerBuilds.ParseChannel(ChannelName.Text);
            var build = await LookupChannel(channel);
            string label = channel ?? "Production";
            return $"{label} currently offers {build.Version} ({build.VersionGuid}). " +
                (ReadInstalledState().VersionGuid == build.VersionGuid ? "This build is active." : "A different build is active.");
        });

        private async void History_Click(object sender, RoutedEventArgs e) => await RunActionAsync("Loading Roblox deployment history…", async () =>
        {
            string history = await App.HttpClient.GetStringAsync("https://setup.rbxcdn.com/DeployHistory.txt");
            _history = PlayerBuilds.ParseHistory(history).Select(build => new BuildChoiceItem(build.Id, build.Version)).ToArray();
            RefreshInstalled();
            return $"{_history.Length:N0} published build IDs are in the build list. Availability is checked when you download.";
        });

        private async Task<string> DownloadAsync(string id)
        {
            var bootstrapper = new Bloxstrap.Bootstrapper(LaunchMode.Player);
            await Task.Run(() => bootstrapper.StagePlayerBuildAsync(id));
            BuildChoice.Text = id;
            RefreshInstalled();
            return $"Build {id} is ready.";
        }

        private async void DownloadBuild_Click(object sender, RoutedEventArgs e) =>
            await RunActionAsync("Verifying and downloading Roblox packages…", () => DownloadAsync(ReadBuildId()));

        private async void DownloadChannel_Click(object sender, RoutedEventArgs e) =>
            await RunActionAsync("Verifying and downloading Roblox packages…",
                async () => await DownloadAsync((await LookupChannel(PlayerBuilds.ParseChannel(ChannelName.Text))).VersionGuid));

        private async void Pin_Click(object sender, RoutedEventArgs e) => await RunActionAsync("Saving…", () =>
        {
            string id = ReadBuildId();
            string directory = Path.Combine(Paths.Versions, id);
            if (PlayerBuilds.Read(directory) is null &&
                (ReadInstalledState().VersionGuid != id || !File.Exists(Path.Combine(directory, PlayerBuilds.ExecutableName))))
                throw new IOException("Download the build before pinning it.");
            SaveSelection(App.Settings.Prop.PlayerChannel, id);
            return Task.FromResult($"Build {id} is pinned for your next Roblox launch.");
        });

        private async void Remove_Click(object sender, RoutedEventArgs e) => await RunActionAsync("Removing build…", async () =>
        {
            string id = ReadBuildId();
            var bootstrapper = new Bloxstrap.Bootstrapper(LaunchMode.Player);
            await Task.Run(() => bootstrapper.RemoveRetainedPlayerBuildAsync(id));
            RefreshInstalled();
            return $"Build {id} is no longer stored as a download.";
        });
    }
}
