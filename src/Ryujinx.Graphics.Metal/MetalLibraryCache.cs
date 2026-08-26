using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ryujinx.Graphics.Metal
{
    // 内存库化缓存: 按 hash 聚合编译产物（metallib + MSC reflection），复用编译结果
    public static class MetalLibraryCache
    {
        private static readonly ConcurrentDictionary<string, MetalCompiledShader> _cache = new();
        private static long _hits, _misses;

        public static bool TryGet(string hash, out MetalCompiledShader shader) => _cache.TryGetValue(hash, out shader);

        public static void Add(string hash, MetalCompiledShader shader) => _cache[hash] = shader;

        public static void RecordHit() => Interlocked.Increment(ref _hits);
        public static void RecordMiss() => Interlocked.Increment(ref _misses);

        public static (long hits, long misses, double hitRate) Stats()
        {
            long h = _hits, m = _misses;
            double rate = (h + m) == 0 ? 0 : (double)h / (h + m) * 100;
            return (h, m, rate);
        }

        public static int Count => _cache.Count;
        public static void Clear() { _cache.Clear(); _hits = 0; _misses = 0; }
    }
}
