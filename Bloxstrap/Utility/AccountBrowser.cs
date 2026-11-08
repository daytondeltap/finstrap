using System.Runtime.InteropServices;

namespace Bloxstrap.Utility
{
    internal static class AccountBrowser
    {
        private const uint AssocIsProtocol = 0x00001000;
        private const uint AssocExecutable = 2;

        public static string GetDefaultExecutable()
        {
            uint length = 0;
            AssocQueryStringW(AssocIsProtocol, AssocExecutable, "https", "open", null, ref length);
            if (length != 0)
            {
                var executable = new StringBuilder((int)length);
                if (AssocQueryStringW(AssocIsProtocol, AssocExecutable, "https", "open", executable, ref length) == 0 &&
                    File.Exists(executable.ToString()))
                    return executable.ToString();
            }

            throw new InvalidOperationException("Finstrap could not find the Windows default browser. Choose a supported browser in Windows Default apps.");
        }

        public static ProcessStartInfo CreateStartInfo(string executable, long userId, long placeId)
        {
            string browser = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
            if (browser is "opera" or "opera_gx" or "launcher")
            {
                string? product = FileVersionInfo.GetVersionInfo(executable).ProductName;
                if (browser != "launcher" || product?.StartsWith("Opera", StringComparison.OrdinalIgnoreCase) == true)
                    browser = browser == "opera_gx" || product?.Contains("GX", StringComparison.OrdinalIgnoreCase) == true ? "opera-gx" : "opera";
            }

            if (browser is not ("opera" or "opera-gx" or "chrome" or "chromium" or "brave" or "vivaldi" or "firefox"))
                throw new InvalidOperationException("Choose Opera, Chrome, Brave, Vivaldi, or Firefox as your Windows default browser to open an isolated Roblox account profile.");

            // Keep each browser's data separate from the normal profile and the former Edge profiles.
            string profile = Path.Combine(Paths.Base, "AccountProfiles", browser, userId.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(profile);

            var start = new ProcessStartInfo(executable);
            if (browser == "firefox")
            {
                start.ArgumentList.Add("-profile");
                start.ArgumentList.Add(profile);
                start.ArgumentList.Add("-new-window");
            }
            else
            {
                start.ArgumentList.Add($"--user-data-dir={profile}");
                start.ArgumentList.Add("--new-window");
            }

            start.ArgumentList.Add(placeId > 0 ? $"https://www.roblox.com/games/{placeId.ToString(CultureInfo.InvariantCulture)}" : "https://www.roblox.com/home");
            return start;
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int AssocQueryStringW(uint flags, uint kind, string association, string verb,
            StringBuilder? output, ref uint outputLength);
    }
}
