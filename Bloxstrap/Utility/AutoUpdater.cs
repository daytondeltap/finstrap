using System.Security.Cryptography;

namespace Bloxstrap.Utility
{
    internal static class AutoUpdater
    {
        private static bool _checked;

        private static bool OtherFinstrapRunning()
        {
            var processes = Process.GetProcessesByName(App.ProjectName);
            try { return processes.Any(process => process.Id != Environment.ProcessId); }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        internal static async Task<bool> TryUpdateAsync(LaunchMode launchMode, Action? onDownloading = null)
        {
            const string LOG_IDENT = "AutoUpdater";

            if (!App.Settings.Prop.CheckForUpdates || App.LaunchSettings.UpgradeFlag.Active || _checked || OtherFinstrapRunning())
                return false;

            _checked = true;
#if !DEBUG_UPDATER
            using var lookupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var releaseInfo = await App.GetLatestRelease(lookupTimeout.Token);

            if (releaseInfo is null || !Version.TryParse(releaseInfo.TagName.TrimStart('v'), out var releaseVersion))
                return false;

            var asset = releaseInfo.Assets?.FirstOrDefault(item =>
                string.Equals(item.Name, "Finstrap.exe", StringComparison.OrdinalIgnoreCase));
            if (asset is null || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps || downloadUri.Host != "github.com" ||
                downloadUri.AbsolutePath != $"/{App.ProjectRepository}/releases/download/{releaseInfo.TagName}/Finstrap.exe")
            {
                App.Logger.WriteLine(LOG_IDENT, "The latest release has no Finstrap.exe asset.");
                return false;
            }

            var versionComparison = Utilities.CompareVersions(App.Version, releaseInfo.TagName);

            // check if we aren't using a deployed build, so we can update to one if a new version comes out
            if ((App.IsProductionBuild && versionComparison == VersionComparison.Equal) || versionComparison == VersionComparison.GreaterThan)
            {
                App.Logger.WriteLine(LOG_IDENT, "No updates found");
                return false;
            }

            string version = releaseInfo.TagName;
#else
            string version = App.Version;
#endif

            onDownloading?.Invoke();

            try
            {
#if DEBUG_UPDATER
                string downloadLocation = Path.Combine(Paths.TempUpdates, $"{App.ProjectName}.exe");

                Directory.CreateDirectory(Paths.TempUpdates);

                File.Copy(Paths.Process, downloadLocation, true);
#else
                string updateDirectory = Path.Combine(Paths.TempUpdates, releaseVersion.ToString());
                string downloadLocation = Path.Combine(updateDirectory, "Finstrap.exe");

                Directory.CreateDirectory(updateDirectory);

                App.Logger.WriteLine(LOG_IDENT, $"Downloading {releaseInfo.TagName}...");

                string partial = downloadLocation + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    using var response = await App.HttpClient.GetAsync(downloadUri);
                    response.EnsureSuccessStatusCode();

                    await using (var fileStream = File.Create(partial))
                        await response.Content.CopyToAsync(fileStream);

                    if (asset.Digest is { Length: > 0 } digest)
                    {
                        await using var downloaded = File.OpenRead(partial);
                        string actual = "sha256:" + Convert.ToHexString(await SHA256.HashDataAsync(downloaded));
                        if (!actual.Equals(digest, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The Finstrap release download did not match its SHA-256 digest.");
                    }

                    if (OtherFinstrapRunning())
                        return false;

                    File.Move(partial, downloadLocation, overwrite: true);
                }
                finally
                {
                    File.Delete(partial);
                }
#endif

                App.Logger.WriteLine(LOG_IDENT, $"Starting {version}...");

                ProcessStartInfo startInfo = new()
                {
                    FileName = downloadLocation,
                };

                foreach (string arg in App.LaunchSettings.Args)
                    startInfo.ArgumentList.Add(arg);

                startInfo.ArgumentList.Add("-upgrade");

                if (launchMode == LaunchMode.Player && !startInfo.ArgumentList.Contains("-player"))
                    startInfo.ArgumentList.Add("-player");
                else if (launchMode == LaunchMode.Studio && !startInfo.ArgumentList.Contains("-studio"))
                    startInfo.ArgumentList.Add("-studio");

                using var updateLock = new InterProcessLock("AutoUpdater");
                if (!updateLock.IsAcquired)
                    return false;

                using var updater = Process.Start(startInfo);
                return updater is not null;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "An exception occurred when running the auto-updater");
                App.Logger.WriteException(LOG_IDENT, ex);
            }

            return false;
        }
    }
}
