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
        }

        public ProgramLinkStatus CheckProgramLink(bool blocking) => ProgramLinkStatus.Success;

        public byte[] GetBinary() => _metallib ?? Array.Empty<byte>();

        public void Dispose() { }
    }
}
