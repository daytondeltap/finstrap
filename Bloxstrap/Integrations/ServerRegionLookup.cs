using System.Web;

namespace Bloxstrap.Integrations
{
    internal sealed record ServerLocation(Guid JobId, string Country, string? Region, string? City)
    {
        public override string ToString() => string.Join(", ",
            new[] { City, Region, Country }.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct());
    }

    internal sealed class ServerRegionLookup(HttpClient publicClient, RobloxAccountSession account)
    {
        private const int MaxProbes = 12;
        private readonly Dictionary<IPAddress, (string Country, string? Region, string? City)> _locations = new();

        public async Task<ServerLocation> FindAsync(AccountLaunchTarget target, string? country, CancellationToken token)
        {
            if (target.PlaceId <= 0 || target.UserId != 0 || target.Username is not null ||
                target.ShareCode is not null || target.LinkCode is not null || target.AccessCode is not null)
                throw new FormatException("Server location lookup needs a public game or public server link.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                if (target.JobId is not null)
                {
                    var result = await LocateAsync(target.PlaceId, Guid.Parse(target.JobId), timeout.Token);
                    if (result is null)
                        throw new FormatException("Roblox did not provide a usable location for this server.");
                    if (!Matches(result, country))
                        throw new FormatException($"This server is estimated to be in {result}, outside the selected country ({country}).");
                    return result;
                }

                var seen = new HashSet<Guid>();
                string? cursor = null;
                for (int page = 0; page < 2; page++)
                {
                    var url = $"https://games.roblox.com/v1/games/{target.PlaceId}/servers/Public?sortOrder=Desc&excludeFullGames=true&limit=10";
                    if (!string.IsNullOrEmpty(cursor))
                        url += "&cursor=" + Uri.EscapeDataString(cursor);
                    using var list = JsonDocument.Parse(await publicClient.GetStringAsync(url, timeout.Token));
                    foreach (var server in list.RootElement.GetProperty("data").EnumerateArray())
                    {
                        if (!Guid.TryParse(server.GetProperty("id").GetString(), out Guid job) ||
                            server.GetProperty("playing").GetInt32() >= server.GetProperty("maxPlayers").GetInt32() ||
                            !seen.Add(job))
                            continue;
                        var result = await LocateAsync(target.PlaceId, job, timeout.Token);
                        if (result is not null && Matches(result, country))
                            return result;
                        if (seen.Count >= MaxProbes)
                            break;
                    }
                    cursor = list.RootElement.GetProperty("nextPageCursor").GetString();
                    if (seen.Count >= MaxProbes || string.IsNullOrEmpty(cursor))
                        break;
                }

                throw new FormatException(country is null
                    ? "No available public server returned a location estimate."
                    : $"No available public server matched {country} in the servers checked. Try again or choose another country.");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new FormatException("Server location lookup timed out. Try again.");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new FormatException("Server location lookups are temporarily rate limited. Wait a moment and try again.");
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new FormatException("The server location service returned an unexpected response.");
            }
        }

        private static bool Matches(ServerLocation location, string? country) =>
            country is null || string.Equals(country, location.Country, StringComparison.OrdinalIgnoreCase);

        private async Task<ServerLocation?> LocateAsync(long placeId, Guid jobId, CancellationToken token)
        {
            var ip = await account.GetServerAddressAsync(placeId, jobId, token);
            if (ip is null)
                return null;

            if (!_locations.TryGetValue(ip, out var location))
            {
                using var data = JsonDocument.Parse(await publicClient.GetStringAsync($"https://ipinfo.io/{ip}/json", token));
                var root = data.RootElement;
                string? country = ReadString(root, "country");
                if (country is null || !Regex.IsMatch(country, @"\A[A-Z]{2}\z"))
                    return null;
                location = (country, ReadString(root, "region"), ReadString(root, "city"));
                _locations[ip] = location;
            }

            return new(jobId, location.Country, location.Region, location.City);
        }

        internal static string? ReadString(JsonElement value, string key) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) &&
            field.ValueKind == JsonValueKind.String ? field.GetString() : null;

        internal static IPAddress? ReadAddress(JsonElement script)
        {
            if (TryPublicIPv4(ReadString(script, "MachineAddress")) is { } machine)
                return machine;

            if (script.ValueKind == JsonValueKind.Object && script.TryGetProperty("UdmuxEndpoints", out var endpoints) &&
                endpoints.ValueKind == JsonValueKind.Array)
                foreach (var endpoint in endpoints.EnumerateArray())
                    if (TryPublicIPv4(ReadString(endpoint, "Address")) is { } relay)
                        return relay;
            return null;
        }

        private static IPAddress? TryPublicIPv4(string? value)
        {
            if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return null;

            byte[] b = ip.GetAddressBytes();
            return b[0] is 0 or 10 or 127 or >= 224 ||
                (b[0] == 100 && b[1] is >= 64 and <= 127) ||
                (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                (b[0] == 192 && b[1] == 168) || (b[0] == 198 && b[1] is 18 or 19) ? null : ip;
        }

        internal static AccountLaunchTarget ParseBrowserPlay(string args)
        {
            if (args.StartsWith("roblox://experiences/start?", StringComparison.OrdinalIgnoreCase) &&
                AccountLaunchTarget.Parse(args) is { PlaceId: > 0, UserId: 0, LinkCode: null, AccessCode: null } direct)
                return direct;
            if (!args.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Open Finstrap and enter a public game link to use the region filter.");
            const string field = "placelauncherurl:";
            var fields = args.Split('+').Where(part => part.StartsWith(field, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            string? encoded = fields.Length == 1 ? fields[0] : null;
            if (encoded is null || !Uri.TryCreate(Uri.UnescapeDataString(encoded[field.Length..]), UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.Host != "assetgame.roblox.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                !uri.AbsolutePath.Equals("/game/PlaceLauncher.ashx", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("This Roblox Play link has no supported public game destination.");
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (query["linkCode"] is not null || query["privateServerLinkCode"] is not null || query["accessCode"] is not null)
                throw new FormatException("Region filtering supports public games and public server links.");
            if (!long.TryParse(query["placeId"], NumberStyles.None, CultureInfo.InvariantCulture, out long place) || place <= 0)
                throw new FormatException("This Roblox Play link has no valid place ID.");
            return query["request"] switch
            {
                "RequestGame" => new(PlaceId: place),
                "RequestGameJob" when Guid.TryParse(query["gameId"], out Guid job) => new(PlaceId: place, JobId: job.ToString("D")),
                _ => throw new FormatException("Region filtering supports public games and public server links.")
            };
        }
    }
}
