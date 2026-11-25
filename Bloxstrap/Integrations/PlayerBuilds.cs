using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bloxstrap.Models.Persistable;

namespace Bloxstrap.Integrations
{
    internal static class PlayerBuilds
    {
        private const string SnapshotName = ".finstrap-player-build.json";
        internal const string ExecutableName = "RobloxPlayerBeta.exe";

        public static bool IsBuildId(string? value) =>
            value is not null && Regex.IsMatch(value, @"\Aversion-[0-9a-f]{16}\z", RegexOptions.CultureInvariant);

        public static string? ParseChannel(string? value)
        {
            string channel = value?.Trim().ToLowerInvariant() ?? "";
            if (channel.Length == 0)
                return null;
            if (!Regex.IsMatch(channel, @"\A[a-z0-9][a-z0-9_-]{0,63}\z", RegexOptions.CultureInvariant))
                throw new FormatException("Enter a Roblox channel name using letters, numbers, hyphens or underscores.");
            return channel;
        }

        public static (string Id, string Version)[] ParseHistory(string history) =>
            Regex.Matches(history, @"^New WindowsPlayer (version-[0-9a-f]{16}) at [^\r\n]+, file version: (\d+, \d+, \d+, \d+)",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant)
                .Cast<Match>().Reverse()
                .Select(match => (Id: match.Groups[1].Value, Version: match.Groups[2].Value.Replace(", ", ".")))
                .DistinctBy(build => build.Id)
                .ToArray();

        public static DistributionState? Read(string directory)
        {
            if (!IsBuildId(Path.GetFileName(directory)) || !File.Exists(Path.Combine(directory, ExecutableName)))
                return null;

            try
            {
                var state = JsonSerializer.Deserialize<DistributionState>(File.ReadAllText(Path.Combine(directory, SnapshotName)));
                return state?.VersionGuid == Path.GetFileName(directory) &&
                    state.PackageHashes is { Count: > 0 } && state.ModManifest is not null ? state : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        public static void Save(string directory, DistributionState state)
        {
            string path = Path.Combine(directory, SnapshotName);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(state));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }
}
