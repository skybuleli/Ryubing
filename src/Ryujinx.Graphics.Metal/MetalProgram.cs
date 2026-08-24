using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;

namespace Ryujinx.Graphics.Metal
{
    class MetalProgram : IProgram
    {
        private readonly ShaderSource[] _shaders;
        private readonly ShaderInfo _info;
        private byte[] _binary;

        public MetalProgram(ShaderSource[] shaders, ShaderInfo info)
        {
            _shaders = shaders;
            _info = info;

            // P1-1: 存根实现，不做实际编译。P1-3 将在此接 slangc + MSC。
            foreach (var s in shaders)
            {
                if (s.Language == Ryujinx.Graphics.Shader.Translation.TargetLanguage.Slang)
                {
                    // 占位：记录 slang 源码长度用于 evidence
                }
            }
        }

        public ProgramLinkStatus CheckProgramLink(bool blocking)
        {
            // 存根：始终成功，避免 ShaderCache 阻塞
            return ProgramLinkStatus.Success;
        }

        public byte[] GetBinary()
        {
            // 返回空或缓存的 metallib，二期回填真实 metallib
            return _binary ?? Array.Empty<byte>();
        }

        public void Dispose() { }
    }
}
