namespace Bloxstrap.Integrations
{
    internal sealed class RobloxAccountLaunch
    {
        public long UserId { get; }
        public string Username { get; }
        public string ProfileKey { get; }
        public string Arguments { get; }

        public RobloxAccountLaunch(long userId, string username, string profileKey, string ticket)
        {
            if (userId <= 0 || !Regex.IsMatch(ticket, @"\A[A-Za-z0-9_.~=-]{16,4096}\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Roblox returned an invalid desktop launch ticket.");

            UserId = userId;
            Username = username;
            ProfileKey = profileKey;
            Arguments = $"roblox-player:1+launchmode:app+gameinfo:{ticket}+launchtime:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}+LaunchExp:InApp";
        }
    }
}
