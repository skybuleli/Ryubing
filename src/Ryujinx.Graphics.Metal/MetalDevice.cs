using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal
{
    // P1-3: Bridge 单例占位，MTL4Compiler langVersion=3.2 约束在此管理
    // 当前 P1-3 使用 CLI (metal-shaderconverter) 间接调用 libmetalirconverter，
    // 下一迭代替换为直接 P/Invoke IRCompiler API
    static class MetalDevice
    {
        private static readonly Lazy<nint> _device = new(() => nint.Zero, true);

        public static nint Handle => _device.Value;

        // 预留 P/Invoke，直连 libmetalirconverter.dylib 时启用
        // [DllImport("libmetalirconverter", EntryPoint = "IRCompilerCreate")]
        // private static extern nint IRCompilerCreate();
    }
}
