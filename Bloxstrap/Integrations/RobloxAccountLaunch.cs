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
            if (userId <= 0 || ticket.Length is 0 or > 8192 || ticket.Any(ch => ch < '!' || ch > '~'))
                throw new InvalidDataException("Roblox returned an invalid desktop launch ticket.");

            UserId = userId;
            Username = username;
            ProfileKey = profileKey;
            string? launcher = target.GetPlaceLauncherUrl();
            OpenDesktopHome = launcher is null;
            string mode = OpenDesktopHome ? "app" : "play";
            string launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            Arguments = $"roblox-player:1+launchmode:{mode}+gameinfo:{Uri.EscapeDataString(ticket)}+launchtime:{launchTime}+LaunchExp:InApp";
            if (launcher is not null)
                Arguments += "+placelauncherurl:" + Uri.EscapeDataString(launcher);
        }
    }
}
