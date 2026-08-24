using Ryujinx.Graphics.Shader.Translation;
using System.Text;

namespace Ryujinx.Graphics.Shader.CodeGen.Slang
{
    static class SlangDeclarations
    {
        public static void DeclareCommon(StringBuilder sb, CodeGenParameters parameters)
        {
            // P1-4 定版: buffer(0)=rootTable, buffer(1)=sampler, buffer(2)=perDraw
            // HLSL 映射: b0/b1 -> buffer(0) 常量表, t0/u0 -> buffer(0) SRV/UAV, t1/t2 -> buffer(0) 纹理, s0/s1 -> buffer(1) 采样器
            sb.AppendLine("// Common resources - buffer(0)=rootTable b0/b1/t0/u0/t1/t2, buffer(1)=sampler s0/s1, buffer(2)=perDraw");
            sb.AppendLine("cbuffer RootConstants : register(b0) { float4 cb0[64]; } // buffer(0) rootTable");
            sb.AppendLine("cbuffer PerDrawConstants : register(b2) { float4 cbPerDraw[16]; } // buffer(2) perDraw");
            sb.AppendLine("StructuredBuffer<float4> storage0 : register(t0); // buffer(0) SRV");
            sb.AppendLine("RWStructuredBuffer<float4> storageRW0 : register(u0); // buffer(0) UAV");
            sb.AppendLine("Texture2D tex0 : register(t1); Texture2D tex1 : register(t2); // buffer(0)");
            sb.AppendLine("SamplerState samp0 : register(s0); SamplerState samp1 : register(s1); // buffer(1)");
            sb.AppendLine();
        }
    }
}
