using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal
{
    static class MetalCommandBuffer
    {
        public static nint Create(nint commandQueue)
        {
            Console.WriteLine($"[MTLCommandBuffer] 创建: queue=0x{commandQueue:X}");
            return (nint)0xC0FFEE;
        }

        public static nint CreateRenderEncoder(nint commandBuffer, nint renderPassDescriptor)
        {
            Console.WriteLine($"[MTLRenderCommandEncoder] 创建: buffer=0x{commandBuffer:X}");
            return (nint)0xBEEF;
        }

        public static void EndEncoding(nint encoder)
        {
            Console.WriteLine($"[MTLRenderCommandEncoder] 结束: 0x{encoder:X}");
        }

        public static void Present(nint commandBuffer, nint drawable)
        {
            Console.WriteLine($"[MTLCommandBuffer] Present: buffer=0x{commandBuffer:X} drawable=0x{drawable:X}");
        }

        public static void Commit(nint commandBuffer)
        {
            Console.WriteLine($"[MTLCommandBuffer] 提交: 0x{commandBuffer:X}");
        }
    }
}
