using System.Security.Cryptography;
using System.Text.Json;

namespace Ryujinx.Graphics.Metal
{
    public static class MetalDiskCache
    {
        private static readonly string BaseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "ryubing", "metal");

        private static readonly string ManifestPath = Path.Combine(BaseDir, "manifest.json");
        private static readonly object _lock = new();

        public static string GetHash(string slang, string stage)
        {
            using var sha = SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(stage + ":" + slang);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }

        public static bool TryGet(string hash, out byte[] metallib)
        {
            string path = Path.Combine(BaseDir, $"{hash}.metallib");
            if (File.Exists(path))
            {
                metallib = File.ReadAllBytes(path);
                UpdateHit(hash);
                return true;
            }
            metallib = null;
            return false;
        }

        public static void Save(string hash, string slang, byte[] dxil, byte[] metallib, string stage, long elapsedMs)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(BaseDir);
                File.WriteAllText(Path.Combine(BaseDir, $"{hash}.slang"), slang);
                if (dxil != null) File.WriteAllBytes(Path.Combine(BaseDir, $"{hash}.dxil"), dxil);
                File.WriteAllBytes(Path.Combine(BaseDir, $"{hash}.metallib"), metallib);

                var manifest = LoadManifest();
                var entry = manifest.FirstOrDefault(e => e.Hash == hash);
                if (entry == null)
                {
                    entry = new ManifestEntry { Hash = hash, Stage = stage };
                    manifest.Add(entry);
                }
                entry.SlangSize = slang.Length;
                entry.DxilSize = dxil?.Length ?? 0;
                entry.MetallibSize = metallib.Length;
                entry.CompileMs = elapsedMs;
                entry.HitCount++;
                entry.LastAccess = DateTime.UtcNow.ToString("o");
                SaveManifest(manifest);
            }
        }

        public static List<ManifestEntry> LoadManifest()
        {
            if (!File.Exists(ManifestPath)) return new List<ManifestEntry>();
            try { return JsonSerializer.Deserialize<List<ManifestEntry>>(File.ReadAllText(ManifestPath)) ?? new(); }
            catch { return new(); }
        }

        private static void SaveManifest(List<ManifestEntry> manifest)
        {
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ManifestPath, json);
        }

        private static void UpdateHit(string hash)
        {
            lock (_lock)
            {
                var manifest = LoadManifest();
                var e = manifest.FirstOrDefault(x => x.Hash == hash);
                if (e != null) { e.HitCount++; e.LastAccess = DateTime.UtcNow.ToString("o"); SaveManifest(manifest); }
            }
        }

        public static void Clear() { try { Directory.Delete(BaseDir, true); } catch { } }

        public class ManifestEntry
        {
            public string Hash { get; set; }
            public string Stage { get; set; }
            public int SlangSize { get; set; }
            public int DxilSize { get; set; }
            public int MetallibSize { get; set; }
            public long CompileMs { get; set; }
            public int HitCount { get; set; }
            public string LastAccess { get; set; }
        }
    }
}
