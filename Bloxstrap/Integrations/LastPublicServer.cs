using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Bloxstrap.Integrations
{
    internal sealed record LastPublicServer(long PlaceId, Guid JobId)
    {
        public AccountLaunchTarget ToTarget() => new(PlaceId: PlaceId, JobId: JobId.ToString("D"));

        public static LastPublicServer? Read(string directory, long userId)
        {
            if (userId <= 0)
                return null;

            try
            {
                using var stream = new FileStream(GetPath(directory, userId), FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete);
                var server = JsonSerializer.Deserialize<LastPublicServer>(stream);
                return server is { PlaceId: > 0 } && server.JobId != Guid.Empty ? server : null;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
            {
                return null;
            }
        }

        public static void Record(string directory, long userId, long placeId, string jobId)
        {
            if (userId <= 0 || placeId <= 0 || !Guid.TryParseExact(jobId, "D", out Guid instanceId) || instanceId == Guid.Empty)
                return;

            Directory.CreateDirectory(directory);
            string destination = GetPath(directory, userId);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new LastPublicServer(placeId, instanceId)));
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        private static string GetPath(string directory, long userId) =>
            Path.Combine(directory, userId.ToString(CultureInfo.InvariantCulture) + ".json");
    }
}
