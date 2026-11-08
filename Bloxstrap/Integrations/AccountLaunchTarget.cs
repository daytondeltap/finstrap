using System.Net.Http.Json;
using System.Web;

namespace Bloxstrap.Integrations
{
    internal sealed record AccountLaunchTarget(long PlaceId = 0, long UserId = 0, string? Username = null,
        string? JobId = null, string? LinkCode = null, string? AccessCode = null, string? ShareCode = null)
    {
        private const string PlaceLauncher = "https://assetgame.roblox.com/game/PlaceLauncher.ashx";

        public static AccountLaunchTarget? Parse(string input)
        {
            input = input.Trim();
            if (input.Length == 0)
                return null;
            if (long.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out long placeId))
                return new(PlaceId: placeId);

            if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
                ((uri.Scheme == "https" && uri.Host is "roblox.com" or "www.roblox.com") || uri.Scheme == "roblox"))
            {
                var query = HttpUtility.ParseQueryString(uri.Query);
                string path = uri.AbsolutePath;
                if ((uri.Scheme == "https" && path.TrimEnd('/') == "/share") ||
                    (uri.Scheme == "roblox" && uri.Host == "navigation" && path == "/share_links"))
                {
                    if (string.Equals(query["type"], "Server", StringComparison.OrdinalIgnoreCase))
                        return new(ShareCode: RequireCode(query["code"]));
                    throw new FormatException("Paste a Roblox private-server share link.");
                }

                var profile = Regex.Match(path, @"\A/users/([0-9]+)/profile/?\z");
                if (uri.Scheme == "https" && profile.Success)
                    return new(UserId: RequireId(profile.Groups[1].Value));

                var game = Regex.Match(path, @"\A/games/([0-9]+)(?:/|\z)");
                if (uri.Scheme == "https" && game.Success)
                    placeId = RequireId(game.Groups[1].Value);
                else if (uri.Scheme == "roblox" && uri.Host == "experiences" && path == "/start")
                {
                    if (query["userId"] is string user)
                        return new(UserId: RequireId(user));
                    placeId = RequireId(query["placeId"]);
                }
                else
                    throw new FormatException("Paste a Roblox game, server, or player profile link.");

                string? link = query["privateServerLinkCode"] ?? query["linkCode"];
                string? access = query["accessCode"];
                string? job = query["gameInstanceId"];
                if (link is not null || access is not null)
                    return new(PlaceId: placeId, LinkCode: link is null ? null : RequireCode(link),
                        AccessCode: access is null ? null : RequireCode(access));
                if (job is not null && !Guid.TryParseExact(job, "D", out _))
                    throw new FormatException("The Roblox link has an invalid server instance ID.");
                return new(PlaceId: placeId, JobId: job);
            }

            string name = input.StartsWith('@') ? input[1..] : input;
            if (Regex.IsMatch(name, @"\A[A-Za-z0-9_]{3,20}\z") && !name.All(ch => ch is >= '0' and <= '9'))
                return new(Username: name);
            throw new FormatException("Enter 0 for Home, a place ID, a Roblox server link, or a friend's username.");
        }

        public async Task<AccountLaunchTarget> ResolveFriendAsync()
        {
            if (Username is null)
                return this;
            using var response = await App.HttpClient.PostAsJsonAsync("https://users.roblox.com/v1/usernames/users",
                new { usernames = new[] { Username }, excludeBannedUsers = true });
            response.EnsureSuccessStatusCode();
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            foreach (var item in body.RootElement.GetProperty("data").EnumerateArray())
            {
                if (string.Equals(item.GetProperty("name").GetString(), Username, StringComparison.OrdinalIgnoreCase))
                    return new(UserId: RequireId(item.GetProperty("id").GetRawText()));
            }
            throw new FormatException("Roblox could not find that username. Use the account username or paste its profile link.");
        }

        public static AccountLaunchTarget FromPrivateShare(string json)
        {
            using var response = JsonDocument.Parse(json);
            var root = response.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("Roblox did not return a private server for this link.");
            JsonElement data = root;
            foreach (string key in new[] { "privateServerInviteData", "linkData", "data" })
            {
                if (root.TryGetProperty(key, out var invite) && invite.ValueKind == JsonValueKind.Object)
                {
                    data = invite;
                    break;
                }
            }
            var place = ReadField(data, "placeId");
            var link = ReadField(data, "linkCode");
            var access = ReadField(data, "accessCode");
            if (access is null && Guid.TryParseExact(link, "D", out _))
                (link, access) = (null, link);
            if (link is null && access is null)
                throw new FormatException("Roblox did not return a server code for this account.");
            return new(PlaceId: RequireId(place), LinkCode: link is null ? null : RequireCode(link),
                AccessCode: access is null ? null : RequireCode(access));
        }

        public string? GetPlaceLauncherUrl()
        {
            if (ShareCode is not null || Username is not null)
                throw new InvalidOperationException("The Roblox destination needs to be resolved before launch.");
            if (UserId > 0)
                return $"{PlaceLauncher}?request=RequestFollowUser&userId={UserId.ToString(CultureInfo.InvariantCulture)}";
            if (PlaceId <= 0)
                return null;

            string place = PlaceId.ToString(CultureInfo.InvariantCulture);
            if (LinkCode is not null || AccessCode is not null)
            {
                string url = $"{PlaceLauncher}?request=RequestPrivateGame&placeId={place}";
                if (LinkCode is not null)
                    url += "&linkCode=" + Uri.EscapeDataString(LinkCode);
                if (AccessCode is not null)
                    url += "&accessCode=" + Uri.EscapeDataString(AccessCode);
                return url;
            }
            if (JobId is not null)
                return $"{PlaceLauncher}?request=RequestGameJob&placeId={place}&gameId={Uri.EscapeDataString(JobId)}";
            return $"{PlaceLauncher}?request=RequestGame&placeId={place}";
        }

        private static long RequireId(string? value) =>
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0
                ? id : throw new FormatException("The Roblox link is missing a valid ID.");

        private static string RequireCode(string? code) =>
            code is not null && Regex.IsMatch(code, @"\A[A-Za-z0-9_-]{1,128}\z")
                ? code : throw new FormatException("The Roblox link has an invalid server code.");

        private static string? ReadField(JsonElement data, string key) =>
            data.TryGetProperty(key, out var field) && field.ValueKind is JsonValueKind.String or JsonValueKind.Number
                ? field.ToString() : null;
    }
}
