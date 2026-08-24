using Ryujinx.Graphics.Shader.Translation;
using System.Text;

namespace Ryujinx.Graphics.Shader.CodeGen.Slang
{
    static class SlangDeclarations
    {
        public static void DeclareCommon(StringBuilder sb, CodeGenParameters parameters)
        {
            // P1-2 占位：声明一组通用资源以保证 slangc 能以 sm_6_0 编译。
            // 后续 P1-3 将按 ResourceManager 精确生成 [[buffer(n)]] 绑定。
            sb.AppendLine("// Common resources placeholder");
            sb.AppendLine("cbuffer Constants : register(b0) { float4 cb0[64]; }");
            sb.AppendLine("cbuffer Constants1 : register(b1) { float4 cb1[64]; }");
            sb.AppendLine("StructuredBuffer<float4> storage0 : register(t0);");
            sb.AppendLine("RWStructuredBuffer<float4> storageRW0 : register(u0);");
            sb.AppendLine("Texture2D tex0 : register(t1); Texture2D tex1 : register(t2);");
            sb.AppendLine("SamplerState samp0 : register(s0); SamplerState samp1 : register(s1);");
            sb.AppendLine();
        }
    }
}
