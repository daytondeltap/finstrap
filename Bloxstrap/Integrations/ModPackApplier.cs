using Bloxstrap.Models.Persistable;

namespace Bloxstrap.Integrations
{
    internal sealed class ModPackApplier(string versionDirectory, string modificationsDirectory, ModPackStore store)
    {
        private const int MaxStateBytes = 128 * 1024 * 1024;
        private sealed record AppliedAsset(byte[] Original, string AppliedHash);
        private sealed record PendingChange(Dictionary<string, byte[]> Before, byte[]? PreviousState);

        private string JournalDirectory => ModPackStore.SafePath(versionDirectory, ".finstrap-packs");
        private string StatePath => ModPackStore.SafePath(JournalDirectory, "state.json");
        private string PendingPath => ModPackStore.SafePath(JournalDirectory, "pending.json");

        public void Recover()
        {
            if (!File.Exists(PendingPath))
                return;

            var pending = JsonSerializer.Deserialize<PendingChange>(ModPackStore.ReadFile(PendingPath, MaxStateBytes))
                ?? throw new InvalidDataException("Mod pack recovery data is invalid.");
            ValidateTargets(pending.Before.Keys);
            foreach (var entry in pending.Before)
                WriteAtomically(ModPackStore.SafePath(versionDirectory, entry.Key), entry.Value);
            if (pending.PreviousState is null)
                File.Delete(StatePath);
            else
                WriteAtomically(StatePath, pending.PreviousState);
            File.Delete(PendingPath);
        }

        // Call under the Player launch lock after manual overrides and presets have been applied.
        public IReadOnlyList<string> Apply(IEnumerable<ModPackSelection> selections)
        {
            Recover();
            if (!File.Exists(StatePath) && !selections.Any(pack => pack.Enabled))
                return Array.Empty<string>();

            byte[]? previous = File.Exists(StatePath) ? ModPackStore.ReadFile(StatePath, MaxStateBytes) : null;
            var applied = previous is null ? new Dictionary<string, AppliedAsset>() :
                JsonSerializer.Deserialize<Dictionary<string, AppliedAsset>>(previous)
                    ?? throw new InvalidDataException("Mod pack state is invalid.");
            ValidateTargets(applied.Keys);

            var desired = new Dictionary<string, (Guid Id, ModPackAsset Asset)>();
            foreach (var selection in selections.Where(pack => pack.Enabled))
            {
                var pack = store.Read(selection.Id);
                foreach (var asset in pack.Assets)
                    desired.TryAdd(asset.Target, (pack.Id, asset));
            }

            var next = new Dictionary<string, AppliedAsset>();
            var before = new Dictionary<string, byte[]>();
            var changes = new Dictionary<string, byte[]>();
            var unavailable = new List<string>();
            foreach (string target in applied.Keys.Union(desired.Keys))
            {
                // Legacy overrides already won in the bootstrapper. Never restore over them.
                if (File.Exists(ModPackStore.SafePath(modificationsDirectory, target)))
                    continue;

                string path = ModPackStore.SafePath(versionDirectory, target);
                if (!File.Exists(path))
                {
                    if (desired.ContainsKey(target))
                        unavailable.Add(target);
                    continue;
                }

                byte[] current = ModPackStore.ReadFile(path, ModPackStore.MaxAssetBytes);
                string currentHash = ModPackStore.Hash(current);
                bool previouslyApplied = applied.TryGetValue(target, out var old) && old.AppliedHash == currentHash;
                byte[] original = previouslyApplied ? old!.Original : current;
                byte[] replacement;
                if (desired.TryGetValue(target, out var selection))
                {
                    replacement = store.ReadAsset(selection.Id, selection.Asset);
                    next[target] = new(original, selection.Asset.Sha256);
                }
                else if (previouslyApplied)
                {
                    replacement = original;
                }
                else
                {
                    continue;
                }

                if (!replacement.AsSpan().SequenceEqual(current))
                {
                    before[target] = current;
                    changes[target] = replacement;
                }
            }

            byte[] nextState = JsonSerializer.SerializeToUtf8Bytes(next);
            if (changes.Count == 0 && previous is not null && nextState.AsSpan().SequenceEqual(previous))
                return unavailable;
            if (changes.Count == 0 && previous is null && next.Count == 0)
                return unavailable;

            Directory.CreateDirectory(JournalDirectory);
            WriteAtomically(PendingPath, JsonSerializer.SerializeToUtf8Bytes(new PendingChange(before, previous)));
            try
            {
                foreach (var entry in changes)
                    WriteAtomically(ModPackStore.SafePath(versionDirectory, entry.Key), entry.Value);
                if (next.Count == 0)
                    File.Delete(StatePath);
                else
                    WriteAtomically(StatePath, nextState);
                File.Delete(PendingPath);
            }
            catch
            {
                Recover();
                throw;
            }
            return unavailable;
        }

        public bool HasPendingRecovery => File.Exists(PendingPath);

        private static void ValidateTargets(IEnumerable<string> paths)
        {
            if (paths.Any(path => !ModPackStore.SupportedTargets.Contains(path)))
                throw new InvalidDataException("Mod pack state contains an unsupported asset path.");
        }

        private static void WriteAtomically(string path, byte[] data)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(data);
                    output.Flush(flushToDisk: true);
                }
                if (File.Exists(path))
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }
}
