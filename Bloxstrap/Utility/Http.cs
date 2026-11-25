namespace Bloxstrap.Utility
{
    internal static class Http
    {
        /// <summary>
        /// Gets and deserializes a JSON API response to the specified object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="url"></param>
        /// <exception cref="HttpRequestException"></exception>
        /// <exception cref="JsonException"></exception>
        public static async Task<T> GetJson<T>(string url, CancellationToken token = default)
        {
            using var request = await App.HttpClient.GetAsync(url, token);

            request.EnsureSuccessStatusCode();

            string json = await request.Content.ReadAsStringAsync(token);
            
            return JsonSerializer.Deserialize<T>(json)!;
        }
    }
}
