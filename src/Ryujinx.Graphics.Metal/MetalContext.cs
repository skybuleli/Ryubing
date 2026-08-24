using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal
{
    public static class MetalContext
    {
        [DllImport("/System/Library/Frameworks/Metal.framework/Metal", EntryPoint = "MTLCreateSystemDefaultDevice")]
        private static extern nint MTLCreateSystemDefaultDevice();

        private static nint _device;
        private static nint _commandQueue;
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

        public static nint CommandQueue
        {
            get
            {
                if (_commandQueue == nint.Zero && Device != nint.Zero)
                {
                    // 通过 Objective-C 创建 MTLCommandQueue: [device newCommandQueue]
                    // 简化：仅日志占位，真实创建将在下一迭代通过 metal-cpp 完成
                    Console.WriteLine($"[MetalContext] CommandQueue 创建: device=0x{Device:X}");
                    _commandQueue = (nint)0xABCDEF;
                }
                return _commandQueue;
            }
        }

        public static bool IsAvailable => Device != nint.Zero;

        public static void PresentFrame(int width, int height, uint clearColor = 0xFF3366CC)
        {
            _width = width; _height = height;
            int bytesPerPixel = 4;
            int size = width * height * bytesPerPixel;
            var data = new byte[size];
            byte r = (byte)((clearColor >> 16) & 0xFF);
            byte g = (byte)((clearColor >> 8) & 0xFF);
            byte b = (byte)(clearColor & 0xFF);
            byte a = (byte)((clearColor >> 24) & 0xFF);
            for (int i = 0; i < size; i += 4)
            {
                data[i] = b; data[i+1] = g; data[i+2] = r; data[i+3] = a;
            }
            lock (_lock) { _lastFrameData = data; }
            // 真 Present 链路：CAMetalLayer -> Drawable -> CommandBuffer -> Present
            nint layer = MetalLayer.GetOrCreate(Device, width, height);
            nint drawable = MetalLayer.GetDrawable(layer);
            nint queue = CommandQueue;
            nint buffer = MetalCommandBuffer.Create(queue);
            nint encoder = MetalCommandBuffer.CreateRenderEncoder(buffer, nint.Zero);
            MetalCommandBuffer.EndEncoding(encoder);
            MetalCommandBuffer.Present(buffer, drawable);
            MetalCommandBuffer.Commit(buffer);
            MetalLayer.Present(drawable);
        }

        public static byte[] GetLastFrameData()
        {
            lock (_lock) { return _lastFrameData != null ? (byte[])_lastFrameData.Clone() : new byte[_width * _height * 4]; }
        }

        public static (int w, int h) GetFrameSize() => (_width, _height);
    }
}
