using System.Collections.Concurrent;

namespace Ryujinx.Graphics.Metal
{
    // 内存库化缓存: 按 hash 聚合 metallib，复用 MTLLibrary
    public static class MetalLibraryCache
    {
        private static readonly ConcurrentDictionary<string, byte[]> _cache = new();
        private static long _hits, _misses;

        public static bool TryGet(string hash, out byte[] metallib) => _cache.TryGetValue(hash, out metallib);

        public static void Add(string hash, byte[] metallib) => _cache[hash] = metallib;

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
