using Bloxstrap.Integrations;
using Bloxstrap.Models.Persistable;

internal static class BuildAndRejoinChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        string servers = Path.Combine(root, "servers");
        Guid firstJob = Guid.Parse("af51dc4a-0bb9-469d-b2f1-46991023c0e6");
        Guid secondJob = Guid.Parse("750acc89-f00f-478d-9fee-ebef3d647ac2");
        LastPublicServer.Record(servers, 11, 101, firstJob.ToString());
        LastPublicServer.Record(servers, 22, 202, secondJob.ToString());
        LastPublicServer.Record(servers, 11, 303, Guid.Empty.ToString());
        var first = LastPublicServer.Read(servers, 11);
        check(first == new LastPublicServer(101, firstJob) &&
            LastPublicServer.Read(servers, 22) == new LastPublicServer(202, secondJob),
            "last public server is durably isolated by account and rejects empty job IDs");
        var target = first!.ToTarget();
        string expected = $"request=RequestGameJob&placeId=101&gameId={firstJob:D}";
        check(target.PlaceId == 101 && target.JobId == firstJob.ToString("D") &&
            target.GetPlaceLauncherUrl()?.EndsWith(expected, StringComparison.Ordinal) == true,
            "rejoin uses the exact recorded place and server in the Roblox launch request");
        LastPublicServer.Record(servers, 11, 202, secondJob.ToString());
        check(LastPublicServer.Read(servers, 11) == new LastPublicServer(202, secondJob) &&
            LastPublicServer.Read(servers, 999) is null && !Directory.EnumerateFiles(servers, "*.tmp").Any(),
            "a later confirmed public server replaces the previous one without leaving a partial record");
        File.WriteAllText(Path.Combine(servers, "11.json"), "{");
        check(LastPublicServer.Read(servers, 11) is null, "corrupt server history is treated as unavailable");

        const string older = "version-1234567890abcdef";
        const string newer = "version-0000000000000001";
        var history = PlayerBuilds.ParseHistory(
            $"New WindowsPlayer {older} at 1/1/2026 1:00:00 PM, file version: 0, 1, 0, 2...Done!\r\n" +
            "New WindowsPlayer version-hidden at 1/2/2026 1:00:00 PM, file version: 0, 2, 0, 2\n" +
            "New Studio64 version-0000000000000000 at 1/2/2026 1:00:00 PM, file version: 0, 2, 0, 2\n" +
            $"New WindowsPlayer {newer} at 1/3/2026 1:00:00 PM, file version: 0, 3, 0, 2\n" +
            $"New WindowsPlayer {newer} at 1/3/2026 1:00:00 PM, file version: 0, 3, 0, 2\n");
        check(history.SequenceEqual(new[] { (newer, "0.3.0.2"), (older, "0.1.0.2") }),
            "official history shows unique published Windows Player builds newest first");
        check(!PlayerBuilds.IsBuildId("../" + older) && !PlayerBuilds.IsBuildId(older + "\n") &&
            PlayerBuilds.ParseChannel(" Prod_TEST ") == "prod_test" && PlayerBuilds.ParseChannel("") is null,
            "version IDs and named channels are normalized or strictly validated");
        bool rejected = false;
        try { PlayerBuilds.ParseChannel("../../production"); }
        catch (FormatException) { rejected = true; }
        check(rejected, "channel input cannot change the Roblox API path");

        string installed = Path.Combine(root, older);
        Directory.CreateDirectory(installed);
        string executable = Path.Combine(installed, PlayerBuilds.ExecutableName);
        File.WriteAllText(executable, "");
        check(PlayerBuilds.Read(installed) is null, "an incomplete install is not available for pinning");
        var state = new DistributionState
        {
            VersionGuid = older,
            KeepPlayerBuild = true,
            PackageHashes = new() { ["RobloxApp.zip"] = "test-signature" },
            ModManifest = new() { "content/example" }
        };
        PlayerBuilds.Save(installed, state);
        var retained = PlayerBuilds.Read(installed);
        check(retained?.KeepPlayerBuild == true && retained.VersionGuid == older &&
            retained.PackageHashes.SequenceEqual(state.PackageHashes) && retained.ModManifest.SequenceEqual(state.ModManifest),
            "retained builds restore their own package hashes and mod manifest");
        File.Delete(executable);
        check(PlayerBuilds.Read(installed) is null, "a retained entry with a missing client cannot be activated");
    }
}
