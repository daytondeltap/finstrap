namespace Bloxstrap.Integrations
{
    internal sealed record RobloxGame(long PlaceId, string Name);

    internal sealed class RobloxGameSearch(HttpClient client)
    {
        public async Task<IReadOnlyList<RobloxGame>> SearchAsync(string query, CancellationToken token)
        {
            string url = "https://apis.roblox.com/search-api/omni-search?pageType=all" +
                $"&sessionId={Guid.NewGuid():D}&searchQuery={Uri.EscapeDataString(query)}";
            using var data = JsonDocument.Parse(await client.GetStringAsync(url, token));
            var root = data.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("searchResults", out var groups) ||
                groups.ValueKind != JsonValueKind.Array)
                throw new JsonException("Roblox returned an unexpected search response.");

            var results = new Dictionary<long, RobloxGame>();
            foreach (var group in groups.EnumerateArray())
            {
                if (ServerRegionLookup.ReadString(group, "contentGroupType") != "Game" ||
                    !group.TryGetProperty("contents", out var items) || items.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in items.EnumerateArray())
                {
                    string? name = ServerRegionLookup.ReadString(item, "name");
                    if (string.IsNullOrWhiteSpace(name) || !item.TryGetProperty("rootPlaceId", out var id) ||
                        id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out long place) || place <= 0)
                        continue;
                    if (!item.TryGetProperty("isSponsored", out var sponsored) || sponsored.ValueKind != JsonValueKind.True)
                        results.TryAdd(place, new(place, name));
                }
            }
            return results.Values.Take(20).ToArray();
        }
    }
}
