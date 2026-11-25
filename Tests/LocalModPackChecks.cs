using System.IO.Compression;
using Bloxstrap.Integrations;
using Bloxstrap.Models.Persistable;

internal static class LocalModPackChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        const string cursor = "content/textures/Cursors/KeyboardMouse/ArrowCursor.png";
        const string sound = "content/sounds/action_jump.mp3";
        string installed = Path.Combine(root, "packs");
        string version = Path.Combine(root, "player");
        string legacy = Path.Combine(root, "legacy");
        var store = new ModPackStore(installed);
        var applier = new ModPackApplier(version, legacy, store);
        byte[] a = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ModPackCursorA.png"));
        byte[] b = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ModPackCursorB.png"));
        byte[] audio = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ModPackSound.mp3"));
        byte[] stock = [42, 2, 32];
        string zip = Path.Combine(root, "fixtures.zip");

        void Archive(params (string Name, byte[] Contents)[] files)
        {
            using var file = File.Create(zip);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            foreach (var input in files)
            {
                using var output = archive.CreateEntry(input.Name).Open();
                output.Write(input.Contents);
            }
        }

        string Under(string folder, string target) => Path.Combine(folder, target.Replace('/', Path.DirectorySeparatorChar));
        void Write(string folder, string target, byte[] bytes)
        {
            string path = Under(folder, target);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        bool CursorIs(byte[] expected) => File.ReadAllBytes(Under(version, cursor)).SequenceEqual(expected);

        Archive((cursor, a), (sound, audio),
            ("finstrap-mod.json", """{"formatVersion":1,"name":"First","author":"Example","version":"1.0"}"""u8.ToArray()));
        var first = store.Import(zip);
        Archive((cursor, b));
        var second = store.Import(zip);
        check(store.Read(first.Id).Assets.Count == 2 && first.Author == "Example", "valid cursor and MP3 pack imports with metadata");

        foreach (var rejected in new[] { "../outside.png", cursor.ToLowerInvariant(), "RobloxPlayerBeta.exe" })
        {
            Archive((cursor, a), (rejected, b));
            try { store.Import(zip); throw new Exception("Expected unsafe ZIP rejection"); }
            catch (InvalidDataException) { check(Directory.GetDirectories(installed).Length == 2, $"rejects {rejected} without importing partial files"); }
        }
        Archive((cursor, audio));
        try { store.Import(zip); throw new Exception("Expected mismatched asset rejection"); }
        catch (InvalidDataException) { check(true, "rejects MP3 content in a cursor slot"); }

        Write(version, cursor, stock);
        var firstEnabled = new ModPackSelection { Id = first.Id, Enabled = true };
        var secondEnabled = new ModPackSelection { Id = second.Id, Enabled = true };
        check(applier.Apply([firstEnabled, secondEnabled]).SequenceEqual([sound]) && CursorIs(a) &&
            !File.Exists(Under(version, sound)), "applies the winner and leaves missing Roblox destinations untouched");
        applier.Apply([secondEnabled, firstEnabled]);
        check(CursorIs(b), "changing pack priority replaces the asset");
        applier.Apply([firstEnabled]);
        check(CursorIs(a), "disabling the winner applies the next pack");
        applier.Apply([]);
        check(CursorIs(stock), "disabling all packs restores the original asset");

        Write(legacy, cursor, b);
        Write(version, cursor, b);
        applier.Apply([firstEnabled]);
        check(CursorIs(b), "manual or preset asset takes precedence");
        File.Delete(Under(legacy, cursor));
        Write(version, cursor, stock);

        string journal = Path.Combine(version, ".finstrap-packs");
        Write(journal, "pending.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            Before = new Dictionary<string, byte[]> { [cursor] = stock },
            PreviousState = (byte[]?)null
        }));
        Write(version, cursor, a);
        new ModPackApplier(version, legacy, store).Recover();
        check(CursorIs(stock) && !applier.HasPendingRecovery, "a new launcher rolls back an interrupted apply");

        Write(version, sound, stock);
        File.WriteAllBytes(store.AssetPath(first.Id, sound), [0]);
        try { applier.Apply([firstEnabled]); throw new Exception("Expected corrupt installed asset rejection"); }
        catch (InvalidDataException) { check(CursorIs(stock) && !applier.HasPendingRecovery, "corrupt installed pack leaves the prior installation intact"); }
    }
}
