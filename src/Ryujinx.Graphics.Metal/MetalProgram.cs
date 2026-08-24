using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;

namespace Ryujinx.Graphics.Metal
{
    class MetalProgram : IProgram
    {
        private readonly ShaderSource[] _shaders;
        private readonly ShaderInfo _info;
        private readonly byte[] _metallib;

        public MetalProgram(ShaderSource[] shaders, ShaderInfo info, byte[] metallib = null)
        {
            _shaders = shaders;
            _info = info;
            _metallib = metallib;
            if (metallib != null && metallib.Length > 0)
            {
                nint device = MetalContext.Device;
                nint lib = MetalPipelineState.CreateLibrary(device, metallib);
                nint pipeline = MetalPipelineState.CreateRenderPipeline(device, lib);
                Console.WriteLine($"[MetalProgram] 管线创建: metallib={metallib.Length}B library=0x{lib:X} pipeline=0x{pipeline:X} shaders={shaders.Length}");
            }
        }

        public ProgramLinkStatus CheckProgramLink(bool blocking) => ProgramLinkStatus.Success;

        public byte[] GetBinary() => _metallib ?? Array.Empty<byte>();

        public void Dispose() { }
    }
}
