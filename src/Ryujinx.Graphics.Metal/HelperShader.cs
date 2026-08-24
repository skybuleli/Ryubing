using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;

namespace Ryujinx.Graphics.Metal
{
    // P1-2 占位：HelperShader 原 Vulkan 为多组 SPIR-V，此处为 Metal 预编译 metallib 占位
    // P1-3 将替换为真实 slang->dxil->metallib 产物
    static class MetalHelperShader
    {
        public static ShaderSource GetBlitVertexShader()
        {
            // 占位：返回一个可编译的 Slang 源码，实际 metallib 在 P1-3 生成
            const string code = @"
struct VSInput { float4 pos : POSITION; };
struct VSOutput { float4 pos : SV_Position; };
VSOutput main(VSInput IN) { VSOutput OUT; OUT.pos = IN.pos; return OUT; }";
            return new ShaderSource(code, ShaderStage.Vertex, TargetLanguage.Slang);
        }

        public static ShaderSource GetBlitFragmentShader()
        {
            const string code = @"float4 main(float4 pos : SV_Position) : SV_Target { return pos; }";
            return new ShaderSource(code, ShaderStage.Fragment, TargetLanguage.Slang);
        }
    }
}
