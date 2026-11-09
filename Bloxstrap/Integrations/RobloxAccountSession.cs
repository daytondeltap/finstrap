namespace Bloxstrap.Integrations
{
    // The caller owns the client and its in-memory, profile-specific cookie jar.
    internal sealed class RobloxAccountSession
    {
        private readonly HttpClient _client;
        private string? _csrf;

        public RobloxAccountSession(HttpClient client) => _client = client;

        public async Task<(long Id, string Name)?> GetIdentityAsync(CancellationToken token)
        {
            using var response = await _client.GetAsync("https://users.roblox.com/v1/users/authenticated", token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return null;
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            long id = body.RootElement.GetProperty("id").GetInt64();
            string name = body.RootElement.GetProperty("name").GetString() ?? "";
            if (id <= 0 || String.IsNullOrWhiteSpace(name))
                throw new InvalidDataException("Roblox returned an invalid account identity.");
            return (id, name);
        }

        public async Task<AccountLaunchTarget> ResolveShareAsync(AccountLaunchTarget target, CancellationToken token)
        {
            if (target.ShareCode is null)
                return target;
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { linkId = target.ShareCode, linkType = "Server" });
            using var response = await PostAsync("https://apis.roblox.com/sharelinks/v1/resolve-link", payload, token);
            if (!response.IsSuccessStatusCode)
                throw new FormatException("Roblox could not open this private-server link for this account. Check access and whether the link is still active.");
            return AccountLaunchTarget.FromPrivateShare(await response.Content.ReadAsStringAsync(token));
        }

        public async Task<string> GetTicketAsync(CancellationToken token)
        {
            using var response = await PostAsync("https://auth.roblox.com/v1/authentication-ticket/", null, token);
            response.EnsureSuccessStatusCode();
            if (!response.Headers.TryGetValues("rbx-authentication-ticket", out var tickets))
                throw new InvalidDataException("Roblox did not return a desktop launch ticket.");
            return tickets.Single();
        }

        private async Task<HttpResponseMessage> PostAsync(string url, byte[]? payload, CancellationToken token)
        {
            for (int attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("Origin", "https://www.roblox.com");
                request.Headers.Referrer = new Uri("https://www.roblox.com/");
                if (_csrf is not null)
                    request.Headers.Add("X-CSRF-TOKEN", _csrf);
                if (payload is null)
                    request.Headers.Add("RBXAuthenticationNegotiation", "1");
                else
                {
                    request.Content = new ByteArrayContent(payload);
                    request.Content.Headers.ContentType = new("application/json");
                }
                var response = await _client.SendAsync(request, token);
                if (attempt == 0 && response.StatusCode == HttpStatusCode.Forbidden &&
                    response.Headers.TryGetValues("x-csrf-token", out var values))
                {
                    _csrf = values.FirstOrDefault();
                    response.Dispose();
                    continue;
                }
                return response;
            }
        }
    }
}
