using System.IO.Compression;
using System.Security.Cryptography;

namespace Bloxstrap.Integrations
{
    internal sealed record ModPackAsset(string Target, string Sha256);
    internal sealed record InstalledModPack(Guid Id, string Name, string? Author, string? Version, List<ModPackAsset> Assets);

    internal sealed class ModPackStore(string root)
    {
        internal const int MaxAssetBytes = 4 * 1024 * 1024;
        private static readonly JsonSerializerOptions ManifestOptions = new() { PropertyNameCaseInsensitive = true };
        private sealed record PackManifest(int FormatVersion, string? Name, string? Author, string? Version);

        internal static readonly string[] SupportedTargets =
        [
            "content/textures/Cursors/KeyboardMouse/ArrowCursor.png",
            "content/textures/Cursors/KeyboardMouse/ArrowFarCursor.png",
            "content/sounds/action_footsteps_plastic.mp3",
            "content/sounds/action_jump.mp3",
            "content/sounds/action_get_up.mp3",
            "content/sounds/action_jump_land.mp3",
            "content/sounds/action_swim.mp3",
            "content/sounds/impact_water.mp3"
        ];

        public InstalledModPack Import(string archive, Action<string, byte[]>? validatePreview = null)
        {
            using var stream = File.OpenRead(archive);
            if (stream.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("The ZIP exceeds 32 MB.");

            using var zip = new ZipArchive(stream);
            if (zip.Entries.Count > 64)
                throw new InvalidDataException("The ZIP contains too many entries.");

            PackManifest? manifest = null;
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            foreach (var entry in zip.Entries)
            {
                string path = entry.FullName.Replace('\\', '/');
                string normalized = path.TrimEnd('/');
                if (!paths.Add(normalized) || normalized.Split('/').Any(segment =>
                    segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                    segment.IndexOfAny([':', '<', '>', '|', '?', '*', '"']) >= 0 || segment.Any(char.IsControl)) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                    (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Unsafe or duplicate ZIP path: {path}");

                if (path.EndsWith('/'))
                {
                    if (entry.Length != 0 || !SupportedTargets.Any(target => target.StartsWith(path, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException($"Unsupported pack folder: {path}");
                    continue;
                }

                if (path.Equals("finstrap-mod.json", StringComparison.OrdinalIgnoreCase))
                {
                    manifest = JsonSerializer.Deserialize<PackManifest>(ReadEntry(entry, 8192), ManifestOptions);
                    if (manifest is not { FormatVersion: 1 } || string.IsNullOrWhiteSpace(manifest.Name))
                        throw new InvalidDataException("finstrap-mod.json needs formatVersion 1 and a name.");
                    continue;
                }

                string? target = SupportedTargets.FirstOrDefault(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                    throw new InvalidDataException($"Unsupported pack file: {path}");
                byte[] bytes = ReadEntry(entry, MaxAssetBytes);
                total += bytes.Length;
                if (total > 16 * 1024 * 1024)
                    throw new InvalidDataException("The expanded assets exceed 16 MB.");
                ValidateAsset(target, bytes);
                validatePreview?.Invoke(target, bytes);
                files.Add(target, bytes);
            }

            if (files.Count == 0)
                throw new InvalidDataException("The ZIP has no supported cursor or sound files.");

            var pack = new InstalledModPack(Guid.NewGuid(), Label(manifest?.Name ?? Path.GetFileNameWithoutExtension(archive)) ?? "Mod pack",
                Label(manifest?.Author), Label(manifest?.Version),
                files.Select(file => new ModPackAsset(file.Key, Hash(file.Value))).ToList());
            Directory.CreateDirectory(root);
            string location = PackDirectory(pack.Id);
            string stage = location + ".import";
            Directory.CreateDirectory(stage);
            try
            {
                foreach (var file in files)
                    File.WriteAllBytes(Path.Combine(stage, Path.GetFileName(file.Key)), file.Value);
                File.WriteAllText(Path.Combine(stage, "pack.json"), JsonSerializer.Serialize(pack));
                Directory.Move(stage, location);
            }
            finally
            {
                if (Directory.Exists(stage))
                    Directory.Delete(stage, recursive: true);
            }
            return pack;
        }

        public InstalledModPack Read(Guid id)
        {
            var pack = JsonSerializer.Deserialize<InstalledModPack>(ReadFile(SafePath(PackDirectory(id), "pack.json"), 8192));
            if (pack is null || pack.Id != id || pack.Assets is null || pack.Assets.Count == 0 ||
                pack.Assets.Any(asset => asset is null || !SupportedTargets.Contains(asset.Target)) ||
                pack.Assets.Select(asset => asset.Target).Distinct().Count() != pack.Assets.Count)
                throw new InvalidDataException("The installed pack manifest is invalid.");
            return pack;
        }

        public string AssetPath(Guid id, string target)
        {
            if (!SupportedTargets.Contains(target))
                throw new InvalidDataException("This asset slot is unsupported.");
            return SafePath(PackDirectory(id), Path.GetFileName(target));
        }

        internal byte[] ReadAsset(Guid id, ModPackAsset asset)
        {
            byte[] bytes = ReadFile(AssetPath(id, asset.Target), MaxAssetBytes);
            if (Hash(bytes) != asset.Sha256)
                throw new InvalidDataException($"The installed pack asset changed: {asset.Target}");
            return bytes;
        }

        internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        internal static string SafePath(string root, string relative)
        {
            string path = root;
            RejectLink(path);
            foreach (string part in relative.Split('/'))
            {
                path = Path.Combine(path, part);
                RejectLink(path);
            }
            return path;
        }

        private static void RejectLink(string path)
        {
            try
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Mod packs cannot use linked files or folders.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        private string PackDirectory(Guid id) => SafePath(root, id.ToString("N"));

        internal static byte[] ReadFile(string path, int limit)
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > limit)
                throw new InvalidDataException("A mod file exceeds its size limit.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }

        private static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
        {
            if (entry.Length < 1 || entry.Length > limit)
                throw new InvalidDataException($"ZIP entry has an invalid size: {entry.FullName}");
            using var stream = entry.Open();
            var bytes = new byte[(int)entry.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw new InvalidDataException($"ZIP entry exceeds its declared size: {entry.FullName}");
            return bytes;
        }

        private static string? Label(string? value)
        {
            value = value?.Trim();
            return string.IsNullOrEmpty(value) ? null : new string(value.Where(c => !char.IsControl(c)).Take(100).ToArray());
        }

        private static void ValidateAsset(string target, byte[] bytes)
        {
            if (target.EndsWith(".png", StringComparison.Ordinal))
            {
                if (bytes.Length >= 33 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
                    bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) &&
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) is > 0 and <= 512 &&
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)) is > 0 and <= 512)
                    return;
            }
            else
            {
                int position = 0;
                if (bytes.Length >= 10 && bytes.AsSpan(0, 3).SequenceEqual("ID3"u8) &&
                    bytes.Skip(6).Take(4).All(item => item < 128))
                {
                    position = 10 + (bytes[6] << 21) + (bytes[7] << 14) + (bytes[8] << 7) + bytes[9];
                    if ((bytes[5] & 0x10) != 0 && bytes[3] == 4)
                        position += 10;
                }
                if (position <= bytes.Length - 4 && bytes[position] == 0xFF &&
                    (bytes[position + 1] & 0xE0) == 0xE0 && (bytes[position + 1] & 0x06) == 0x02 &&
                    (bytes[position + 1] & 0x18) != 0x08 &&
                    (bytes[position + 2] & 0xF0) is > 0 and < 0xF0 && (bytes[position + 2] & 0x0C) != 0x0C)
                    return;
            }
            throw new InvalidDataException($"Invalid cursor PNG or MP3 data: {target}");
        }
    }
}
