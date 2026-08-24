using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal
{
    static class MetalPipelineState
    {
        public static nint CreateLibrary(nint device, byte[] metallib)
        {
            Console.WriteLine($"[MTLLibrary] 创建: device=0x{device:X} metallib={metallib.Length}B");
            // 真 MTLLibrary 创建：device newLibraryWithData:metallib.length error:nil
            // 此处先以日志占位，下一步通过 Objective-C 运行时真建
            return nint.Zero;
        }

        public static nint CreateRenderPipeline(nint device, nint library, string vertexFunc = "main", string fragmentFunc = "main")
        {
            Console.WriteLine($"[MTLRenderPipelineState] 创建: library=0x{library:X} vs={vertexFunc} fs={fragmentFunc}");
            return nint.Zero;
        }
    }
}
