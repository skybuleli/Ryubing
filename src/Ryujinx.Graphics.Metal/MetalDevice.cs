using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal
{
    // 真设备接入：直接调 Metal.framework 的 MTLCreateSystemDefaultDevice
    // 单例 + langVersion=3.2 约束在此记录，Bridge 的 libmetal_bridge.a 仍为 C++ 侧预留
    public static class MetalDevice
    {
        [DllImport("/System/Library/Frameworks/Metal.framework/Metal", EntryPoint = "MTLCreateSystemDefaultDevice")]
        private static extern nint MTLCreateSystemDefaultDevice();

        private static readonly Lazy<nint> _device = new(() =>
        {
            nint dev = MTLCreateSystemDefaultDevice();
            return dev;
        }, true);

        public static nint Handle => _device.Value;
        public static bool IsAvailable => Handle != nint.Zero;
        public static string DeviceName => IsAvailable ? "Apple MTLDevice" : "Unavailable";
    }
}
