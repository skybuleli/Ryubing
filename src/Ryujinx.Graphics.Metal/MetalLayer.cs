using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal
{
    static class MetalLayer
    {
        private static nint _layer;
        private static readonly object _lock = new();

        public static nint GetOrCreate(nint device, int width, int height)
        {
            if (_layer != nint.Zero) return _layer;
            lock (_lock)
            {
                if (_layer != nint.Zero) return _layer;
                // 通过 Objective-C 创建 CAMetalLayer 并绑定 device
                Console.WriteLine($"[CAMetalLayer] 创建: device=0x{device:X} {width}x{height}");
                _layer = (nint)0xCAFE1234; // 占位句柄，下一迭代通过 ObjectiveC.Object 真建
                return _layer;
            }
        }

        public static nint GetDrawable(nint layer)
        {
            Console.WriteLine($"[CAMetalDrawable] 获取: layer=0x{layer:X}");
            return (nint)0xD00D1234; // 占位
        }

        public static void Present(nint drawable)
        {
            Console.WriteLine($"[Metal] Present Drawable: 0x{drawable:X}");
        }
    }
}
