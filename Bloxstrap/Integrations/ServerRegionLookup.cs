using System.Web;

namespace Bloxstrap.Integrations
{
    internal sealed record ServerLocation(Guid JobId, string Country, string? Region, string? City)
    {
        public override string ToString() => string.Join(", ",
            new[] { City, Region, Country }.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct());
    }

    internal sealed class ServerRegionLookup(HttpClient publicClient, RobloxAccountSession account, RobloxDatacenters datacenters)
    {
        private const int MaxProbes = 36;
        private const int CandidatesPerPage = 9;
        private sealed class ServerListRateLimitException : Exception;
        private sealed record CandidatePage(List<Guid> Candidates, string? NextCursor);

        public async Task<ServerLocation> FindAsync(AccountLaunchTarget target, string? country, CancellationToken token)
        {
            if (target.PlaceId <= 0 || target.UserId != 0 || target.Username is not null ||
                target.ShareCode is not null || target.LinkCode is not null || target.AccessCode is not null)
                throw new FormatException("Server location lookup needs a public game or public server link.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            var seen = new HashSet<Guid>();
            try
            {
                if (target.JobId is not null)
                {
                    var result = await LocateAsync(target.PlaceId, Guid.Parse(target.JobId), country is null, timeout.Token);
                    if (result is null)
                        throw new FormatException("No usable location was found for this server.");
                    if (!Matches(result, country))
                        throw new FormatException($"This server is estimated to be in {result}, outside the selected country ({country}).");
                    return result;
                }

                var first = await ReadPageAsync(target.PlaceId, "Asc", null, timeout.Token);
                var pages = new List<CandidatePage> { first };
                int firstLimit = first.NextCursor is null ? MaxProbes : CandidatesPerPage;
                if (await ProbePageAsync(first, firstLimit) is { } firstMatch)
                    return firstMatch;

                bool listRateLimited = false;
                if (first.NextCursor is { } firstCursor)
                {
                    try
                    {
                        var fullest = await ReadPageAsync(target.PlaceId, "Desc", null, timeout.Token);
                        pages.Add(fullest);
                        if (await ProbePageAsync(fullest, CandidatesPerPage) is { } fullMatch)
                            return fullMatch;

                        var next = await ReadPageAsync(target.PlaceId, "Asc", firstCursor, timeout.Token);
                        pages.Add(next);
                        if (await ProbePageAsync(next, CandidatesPerPage) is { } nextMatch)
                            return nextMatch;

                        if (fullest.NextCursor is { } highCursor)
                        {
                            var nextFullest = await ReadPageAsync(target.PlaceId, "Desc", highCursor, timeout.Token);
                            pages.Add(nextFullest);
                            if (await ProbePageAsync(nextFullest, CandidatesPerPage) is { } lastMatch)
                                return lastMatch;
                        }
                    }
                    catch (ServerListRateLimitException) { listRateLimited = true; }
                }

                // Reuse candidates already downloaded when pages overlap or Roblox throttles the listing API.
                var remaining = pages.SelectMany(page => page.Candidates).Distinct().Where(job => !seen.Contains(job)).ToList();
                foreach (var job in Sample(remaining, MaxProbes - seen.Count))
                    if (await ProbeAsync(job) is { } remainingMatch)
                        return remainingMatch;
                if (listRateLimited)
                    throw new ServerListRateLimitException();

                throw new FormatException(country is null
                    ? $"No available public server returned a location estimate ({seen.Count} checked)."
                    : $"No available public server matched {country} in {seen.Count} checks. Try again or choose another country.");

                async Task<ServerLocation?> ProbePageAsync(CandidatePage page, int limit)
                {
                    foreach (var job in Sample(page.Candidates.Where(job => !seen.Contains(job)).ToList(), limit))
                        if (await ProbeAsync(job) is { } result)
                            return result;
                    return null;
                }

                async Task<ServerLocation?> ProbeAsync(Guid job)
                {
                    if (seen.Count >= MaxProbes || !seen.Add(job))
                        return null;
                    try
                    {
                        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                        probeTimeout.CancelAfter(TimeSpan.FromSeconds(7));
                        var location = await LocateAsync(target.PlaceId, job, country is null, probeTimeout.Token);
                        return location is not null && Matches(location, country) ? location : null;
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode is null or >= HttpStatusCode.InternalServerError)
                    {
                        return null;
                    }
                    catch (JsonException) { return null; }
                    catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { return null; }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new FormatException($"Server location lookup timed out after {seen.Count} checks. Try again.");
            }
            catch (Exception ex) when (ex is ServerListRateLimitException or HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
            {
                throw new FormatException("Roblox is limiting server lookups. Wait a moment and try again.");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new FormatException("Roblox did not authorize server lookup. Sign in again and retry.");
            }
            catch (JsonException)
            {
                throw new FormatException("The server location service returned an unexpected response.");
            }
        }

        private async Task<CandidatePage> ReadPageAsync(long placeId, string order, string? cursor, CancellationToken token)
        {
            var url = $"https://games.roblox.com/v1/games/{placeId}/servers/Public?sortOrder={order}&excludeFullGames=true&limit=100";
            if (cursor is not null)
                url += "&cursor=" + Uri.EscapeDataString(cursor);
            using var response = await publicClient.GetAsync(url, token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ServerListRateLimitException();
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                throw new JsonException("Missing public server list.");
            var candidates = new List<Guid>();
            foreach (var server in list.EnumerateArray())
            {
                if (Guid.TryParse(ReadString(server, "id"), out Guid job) && job != Guid.Empty &&
                    server.TryGetProperty("playing", out var playing) && playing.ValueKind == JsonValueKind.Number &&
                    server.TryGetProperty("maxPlayers", out var max) && max.ValueKind == JsonValueKind.Number &&
                    playing.TryGetInt32(out int players) && max.TryGetInt32(out int capacity) &&
                    players >= 0 && capacity > players)
                    candidates.Add(job);
            }
            string? next = ReadString(root, "nextPageCursor");
            return new(candidates, string.IsNullOrEmpty(next) || next == cursor ? null : next);
        }

        private static IReadOnlyList<Guid> Sample(List<Guid>? candidates, int limit)
        {
            if (limit <= 0 || candidates is null)
                return Array.Empty<Guid>();
            if (candidates.Count <= limit)
                return candidates;
            // Keep one high-ranked candidate, then spread the rest across the result page.
            var sample = new List<Guid>(limit) { candidates[0] };
            for (int i = 1; i < limit; i++)
            {
                int start = i * candidates.Count / limit;
                int end = (i + 1) * candidates.Count / limit;
                sample.Add(candidates[Random.Shared.Next(start, end)]);
            }
            return sample;
        }

        private static bool Matches(ServerLocation location, string? country) =>
            country is null || string.Equals(country, location.Country, StringComparison.OrdinalIgnoreCase);

        private async Task<ServerLocation?> LocateAsync(long placeId, Guid jobId, bool allowIpEstimate, CancellationToken token)
        {
            var metadata = await account.GetServerMetadataAsync(placeId, jobId, token);
            if (metadata is null)
                return null;
            var location = await datacenters.ResolveAsync(metadata, token);
            if (location is not null)
                return new(jobId, location.Country, location.Region, location.City);

            // Generic geolocation can label a relay with its registration country. Only use it to display an estimate.
            if (!allowIpEstimate || metadata.Addresses.FirstOrDefault() is not { } ip)
                return null;
            using var data = JsonDocument.Parse(await publicClient.GetStringAsync($"https://ipinfo.io/{ip}/json", token));
            var root = data.RootElement;
            string? country = ReadString(root, "country");
            return country is not null && Regex.IsMatch(country, @"\A[A-Z]{2}\z")
                ? new(jobId, country, ReadString(root, "region"), ReadString(root, "city")) : null;
        }

        internal static string? ReadString(JsonElement value, string key) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) &&
            field.ValueKind == JsonValueKind.String ? field.GetString() : null;

        internal static IReadOnlyList<IPAddress> ReadAddresses(JsonElement script)
        {
            var addresses = new List<IPAddress>();
            if (script.TryGetProperty("UdmuxEndpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Array)
                foreach (var endpoint in endpoints.EnumerateArray())
                    if (TryPublicIPv4(ReadString(endpoint, "Address")) is { } relay)
                        addresses.Add(relay);
            if (TryPublicIPv4(ReadString(script, "MachineAddress")) is { } machine)
                addresses.Add(machine);
            return addresses.Distinct().ToArray();
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
            const string legacy = "roblox://placeId=";
            if (args.StartsWith(legacy, StringComparison.OrdinalIgnoreCase))
            {
                var parameters = HttpUtility.ParseQueryString(args["roblox://".Length..]);
                if (parameters.AllKeys.All(key => key is "placeId" or "gameInstanceId") &&
                    long.TryParse(parameters["placeId"], NumberStyles.None, CultureInfo.InvariantCulture, out long placeId) &&
                    placeId > 0 && (parameters["gameInstanceId"] is null ||
                        Guid.TryParseExact(parameters["gameInstanceId"], "D", out _)))
                    return new(PlaceId: placeId, JobId: parameters["gameInstanceId"]);
                throw new FormatException("This Roblox Play link has no supported public game destination.");
            }
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
