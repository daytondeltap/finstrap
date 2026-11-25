using Bloxstrap.Integrations;
using Bloxstrap.RobloxInterfaces;

namespace Bloxstrap
{
    public partial class Bootstrapper
    {
        internal string? PlayerChannelOverride { get; init; }

        private string? _managedPlayerChannel;
        private string? _pinnedPlayerBuild;

        private bool HasManagedPlayerSelection => _managedPlayerChannel is not null || _pinnedPlayerBuild is not null;

        private void ReadPlayerSelection()
        {
            if (IsStudioLaunch || App.LaunchSettings.VersionFlag.Active || App.LaunchSettings.ChannelFlag.Active)
                return;

            _managedPlayerChannel = PlayerBuilds.ParseChannel(PlayerChannelOverride ?? App.Settings.Prop.PlayerChannel);
            if (PlayerChannelOverride is null && !String.IsNullOrEmpty(App.Settings.Prop.PinnedPlayerBuild))
            {
                _pinnedPlayerBuild = App.Settings.Prop.PinnedPlayerBuild;
                if (!PlayerBuilds.IsBuildId(_pinnedPlayerBuild))
                    throw new FormatException("The pinned Roblox build is invalid. Choose another build in Roblox Versions.");
            }
        }

        private void RetainCurrentPlayerBuild()
        {
            if (IsStudioLaunch || AppData.DistributionState.VersionGuid == _latestVersionGuid)
                return;

            var state = AppData.DistributionState;
            if (!PlayerBuilds.IsBuildId(state.VersionGuid) || state.PackageHashes is not { Count: > 0 } ||
                !File.Exists(AppData.ExecutablePath))
                return;

            state.KeepPlayerBuild |= state.VersionGuid == App.Settings.Prop.PinnedPlayerBuild;
            PlayerBuilds.Save(AppData.Directory, state);
        }

        private bool TryUseRetainedPlayerBuild()
        {
            if (IsStudioLaunch || App.LaunchSettings.ForceFlag.Active || App.State.Prop.ForceReinstall ||
                String.IsNullOrEmpty(AppData.DistributionState.VersionGuid))
                return false;

            var retained = PlayerBuilds.Read(_latestVersionDirectory);
            if (retained is null || retained.PackageHashes.Count != _versionPackageManifest.Count ||
                _versionPackageManifest.Any(package => retained.PackageHashes.GetValueOrDefault(package.Name) != package.Signature))
                return false;

            RetainCurrentPlayerBuild();
            ActivateDistribution(retained);
            CleanupVersionsFolder();
            return true;
        }

        private void ActivateDistribution(DistributionState next)
        {
            var previous = AppData.DistributionState;
            AppData.DistributionStateManager.Prop = next;
            AppData.DistributionStateManager.Save();
            if (!AppData.DistributionStateManager.LastSaveSucceeded)
            {
                AppData.DistributionStateManager.Prop = previous;
                throw new IOException("Finstrap could not activate the Roblox build.");
            }

            if (!String.IsNullOrEmpty(previous.VersionGuid) && previous.VersionGuid != next.VersionGuid)
                MigrateCompatibilityFlags(previous.VersionGuid);
        }

        internal async Task StagePlayerBuildAsync(string buildId)
        {
            if (IsStudioLaunch || !PlayerBuilds.IsBuildId(buildId))
                throw new FormatException("Select a valid Windows Player build.");

            await using var mutex = new AsyncMutex(false, MutexName);
            await mutex.AcquireAsync(_cancelTokenSource.Token);
            if (Utilities.DoesMutexExist(BackgroundUpdaterMutexName))
                throw new IOException("Roblox is updating in the background. Try again once it finishes.");

            AppData.DistributionStateManager.Load(alertFailure: false);
            _latestVersionGuid = buildId;
            _latestVersionDirectory = Path.Combine(Paths.Versions, buildId);
            var active = AppData.DistributionState;
            if (active.VersionGuid == buildId && File.Exists(AppData.ExecutablePath))
            {
                if (active.KeepPlayerBuild)
                    return;

                active.KeepPlayerBuild = true;
                AppData.DistributionStateManager.Save();
                if (!AppData.DistributionStateManager.LastSaveSucceeded)
                {
                    active.KeepPlayerBuild = false;
                    throw new IOException("Finstrap could not keep the installed build.");
                }
                return;
            }
            if (PlayerBuilds.Read(_latestVersionDirectory) is { } retained)
            {
                retained.KeepPlayerBuild = true;
                PlayerBuilds.Save(_latestVersionDirectory, retained);
                return;
            }

            if (App.StudioState.Prop.VersionGuid == buildId || Directory.Exists(_latestVersionDirectory))
                throw new IOException("This build folder is already in use or incomplete. Launch the current Roblox client before trying again.");

            if (await Deployment.InitializeConnectivity() is { } connectionError)
                throw new IOException("Finstrap could not connect to the Roblox download servers.", connectionError);

            var manifest = new PackageManifest(await App.HttpClient.GetStringAsync(
                Deployment.GetLocation($"/{buildId}-rbxPkgManifest.txt")));
            if (!manifest.Any(package => package.Name == "RobloxApp.zip") ||
                manifest.Any(package => (!AppData.PackageDirectoryMap.ContainsKey(package.Name) && package.Name != "RobloxPlayerInstaller.exe") ||
                    !Regex.IsMatch(package.Signature, @"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant) ||
                    package.Size < 0 || package.PackedSize < 0))
                throw new InvalidDataException("Roblox returned an unsupported Windows Player package manifest.");

            var packages = manifest.ToDictionary(package => package.Name, package => package.Signature);
            long requiredBytes = manifest.Sum(package => (long)package.Size + package.PackedSize);
            if (Filesystem.GetFreeDiskSpace(Paths.Base) < requiredBytes)
                throw new IOException("There is insufficient disk space for this Roblox build.");

            string finalDirectory = _latestVersionDirectory;
            _latestVersionDirectory = Path.Combine(Paths.Downloads, ".player-build-" + Guid.NewGuid().ToString("N") + ".staging");
            try
            {
                Directory.CreateDirectory(_latestVersionDirectory);
                foreach (var package in manifest.Where(package => AppData.PackageDirectoryMap.ContainsKey(package.Name) &&
                    package.Name != "WebView2RuntimeInstaller.zip"))
                {
                    await DownloadPackage(package, verifiedStaging: true);
                    ExtractPackage(package);
                }
                if (!_packageExtractionSuccess || !File.Exists(Path.Combine(_latestVersionDirectory, AppData.ExecutableName)))
                    throw new IOException("Roblox's Windows Player packages could not be completely extracted.");

                await File.WriteAllTextAsync(Path.Combine(_latestVersionDirectory, "AppSettings.xml"), AppSettings);
                PlayerBuilds.Save(_latestVersionDirectory, new DistributionState
                {
                    VersionGuid = buildId,
                    PackageHashes = packages,
                    KeepPlayerBuild = true,
                    Size = (int)(requiredBytes / 1024)
                });
                Directory.CreateDirectory(Paths.Versions);
                Directory.Move(_latestVersionDirectory, finalDirectory);
            }
            finally
            {
                if (Directory.Exists(_latestVersionDirectory))
                    Directory.Delete(_latestVersionDirectory, recursive: true);
            }
        }

        internal async Task RemoveRetainedPlayerBuildAsync(string buildId)
        {
            if (!PlayerBuilds.IsBuildId(buildId))
                return;

            await using var mutex = new AsyncMutex(false, MutexName);
            await mutex.AcquireAsync(_cancelTokenSource.Token);
            App.PlayerState.Load(alertFailure: false);
            var settings = new JsonManager<Settings>();
            settings.Load(alertFailure: false);
            if (settings.Prop.PinnedPlayerBuild == buildId || App.PlayerState.Prop.VersionGuid == buildId ||
                App.StudioState.Prop.VersionGuid == buildId || RobloxPlayerProcesses.HasActivePlayer() ||
                Utilities.DoesMutexExist(BackgroundUpdaterMutexName))
                throw new IOException("Unpin the build, switch Roblox to another build, and close running Roblox windows before removing it.");

            string directory = Path.Combine(Paths.Versions, buildId);
            if (PlayerBuilds.Read(directory) is not null)
                Directory.Delete(directory, recursive: true);
        }
    }
}
