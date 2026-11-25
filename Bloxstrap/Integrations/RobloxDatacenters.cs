namespace Bloxstrap.Integrations
{
    internal sealed record RobloxServerMetadata(int? DatacenterId, IReadOnlyList<IPAddress> Addresses);
    internal sealed record DatacenterLocation(string Country, string? Region, string? City);

    // Fetch the public map as a whole. No Roblox credentials, job IDs, or server IPs are sent to RoSeal.
    internal sealed class RobloxDatacenters(HttpClient client)
    {
        private const string MapUrl = "https://data.roseal.live/v2/grouped-datacenters.json";
        private readonly SemaphoreSlim _refreshLock = new(1);
        private Map? _map;
        private DateTimeOffset _refreshAfter;

        public async Task<DatacenterLocation?> ResolveAsync(RobloxServerMetadata metadata, CancellationToken token)
        {
            await _refreshLock.WaitAsync(token);
            try
            {
                if (DateTimeOffset.UtcNow >= _refreshAfter)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(4));
                    try
                    {
                        using var data = JsonDocument.Parse(await client.GetStringAsync(MapUrl, deadline.Token));
                        _map = Map.Read(data.RootElement);
                        _refreshAfter = DateTimeOffset.UtcNow.AddHours(6);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested &&
                        ex is HttpRequestException or JsonException or OperationCanceledException)
                    {
                        _refreshAfter = DateTimeOffset.UtcNow.AddMinutes(1);
                    }
                }
                return _map?.Resolve(metadata);
            }
            finally { _refreshLock.Release(); }
        }

        private sealed class Map
        {
            private readonly Dictionary<int, DatacenterLocation?> _ids = new();
            private readonly Dictionary<IPAddress, DatacenterLocation?> _addresses = new();

            public static Map Read(JsonElement root)
            {
                if (root.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Unexpected datacenter map.");
                var map = new Map();
                foreach (var entry in root.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("location", out var location))
                        continue;
                    string? country = ServerRegionLookup.ReadString(location, "country");
                    if (country is null || !Regex.IsMatch(country, @"\A[A-Z]{2}\z"))
                        continue;
                    var result = new DatacenterLocation(country, ServerRegionLookup.ReadString(location, "region"),
                        ServerRegionLookup.ReadString(location, "city"));
                    if (entry.TryGetProperty("dataCenterIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                        foreach (var id in ids.EnumerateArray())
                            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out int key) && key > 0)
                                Add(map._ids, key, result);
                    if (entry.TryGetProperty("robloxIps", out var addresses) && addresses.ValueKind == JsonValueKind.Array)
                        foreach (var address in addresses.EnumerateArray())
                            if (address.ValueKind == JsonValueKind.String && IPAddress.TryParse(address.GetString(), out var ip))
                                Add(map._addresses, ip, result);
                }
                if (map._ids.Count == 0 && map._addresses.Count == 0)
                    throw new JsonException("Datacenter map contained no locations.");
                return map;
            }

            private static void Add<TKey>(Dictionary<TKey, DatacenterLocation?> entries, TKey key, DatacenterLocation location)
                where TKey : notnull
            {
                if (!entries.TryAdd(key, location) && entries[key] != location)
                    entries[key] = null;
            }

            public DatacenterLocation? Resolve(RobloxServerMetadata metadata)
            {
                if (metadata.DatacenterId is int id && _ids.TryGetValue(id, out var known))
                    return known;

                var knownAddresses = metadata.Addresses.Where(_addresses.ContainsKey).Select(ip => _addresses[ip]).ToArray();
                if (knownAddresses.Length == 0 || knownAddresses[0] is not { } first ||
                    knownAddresses.Any(location => location is null || location.Country != first.Country))
                    return null;
                return knownAddresses.All(location => location == first) ? first : new(first.Country, null, null);
            }
        }
    }
}
