namespace Bloxstrap.Integrations
{
    internal sealed class RobloxAccountLaunch
    {
        public long UserId { get; }
        public string Username { get; }
        public string ProfileKey { get; }
        public string Arguments { get; }
        public bool OpenDesktopHome { get; }

        public RobloxAccountLaunch(long userId, string username, string profileKey, string ticket, AccountLaunchTarget target)
        {
            if (userId <= 0 || !Regex.IsMatch(ticket, @"\A[A-Za-z0-9_.~=-]{16,4096}\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Roblox returned an invalid desktop launch ticket.");

            UserId = userId;
            Username = username;
            ProfileKey = profileKey;
            string? launcher = target.GetPlaceLauncherUrl();
            OpenDesktopHome = launcher is null;
            string mode = OpenDesktopHome ? "app" : "play";
            string launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            Arguments = $"roblox-player:1+launchmode:{mode}+gameinfo:{ticket}+launchtime:{launchTime}+LaunchExp:InApp";
            if (launcher is not null)
                Arguments += "+placelauncherurl:" + Uri.EscapeDataString(launcher);
        }
    }
}
