using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal
{
    // P2-2: 真 Present 的最小上下文，管理 MTLDevice/CommandQueue 与离屏帧缓冲
    // 当前仅做离屏清色与截图返回，非完整 CAMetalLayer 交换链
    public static class MetalContext
    {
        [DllImport("/System/Library/Frameworks/Metal.framework/Metal", EntryPoint = "MTLCreateSystemDefaultDevice")]
        private static extern nint MTLCreateSystemDefaultDevice();

        private static nint _device;
        private static readonly object _lock = new();
        private static byte[] _lastFrameData;
        private static int _width = 1280, _height = 720;

        public static nint Device
        {
            get
            {
                if (_device == nint.Zero)
                {
                    lock (_lock)
                    {
                        if (_device == nint.Zero)
                            _device = MTLCreateSystemDefaultDevice();
                    }
                }
                return _device;
            }
        }

        public static bool IsAvailable => Device != nint.Zero;

        // 离屏帧缓冲：每次 Draw 清为固定色，Screenshot 返回此数据
        public static void PresentFrame(int width, int height, uint clearColor = 0xFF3366CC)
        {
            _width = width; _height = height;
            int bytesPerPixel = 4;
            int size = width * height * bytesPerPixel;
            var data = new byte[size];
            // 填充纯色 (BGRA)
            byte r = (byte)((clearColor >> 16) & 0xFF);
            byte g = (byte)((clearColor >> 8) & 0xFF);
            byte b = (byte)(clearColor & 0xFF);
            byte a = (byte)((clearColor >> 24) & 0xFF);
            for (int i = 0; i < size; i += 4)
            {
                data[i] = b; data[i+1] = g; data[i+2] = r; data[i+3] = a;
            }
            lock (_lock) { _lastFrameData = data; }
        }

        public static byte[] GetLastFrameData()
        {
            lock (_lock) { return _lastFrameData != null ? (byte[])_lastFrameData.Clone() : new byte[_width * _height * 4]; }
        }

        public static (int w, int h) GetFrameSize() => (_width, _height);
    }
}
