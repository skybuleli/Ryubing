using Ryujinx.Graphics.Shader.StructuredIr;
using Ryujinx.Graphics.Shader.Translation;
using System.Text;

namespace Ryujinx.Graphics.Shader.CodeGen.Slang
{
    static class SlangGenerator
    {
        public static string Generate(StructuredProgramInfo info, CodeGenParameters parameters)
        {
            var stage = parameters.Definitions.Stage;
            var sb = new StringBuilder();

            sb.AppendLine($"// Auto-generated Slang/HLSL for {stage} - P1-2");
            sb.AppendLine($"// HelperFunctionsMask={info.HelperFunctionsMask} IoDefs={info.IoDefinitions.Count}");
            sb.AppendLine();

            // 通用资源占位 - 保证编译期可绑定，P1-3 再精确化
            SlangDeclarations.DeclareCommon(sb, parameters);

            // 输入输出结构与主入口按 stage 分发
            switch (stage)
            {
                case ShaderStage.Vertex:
                    sb.AppendLine("struct VSInput { float4 position : POSITION; float4 color : COLOR0; };");
                    sb.AppendLine("struct VSOutput { float4 position : SV_Position; float4 color : COLOR0; };");
                    sb.AppendLine("VSOutput main(VSInput IN)");
                    sb.AppendLine("{ VSOutput OUT; OUT.position = IN.position; OUT.color = IN.color; return OUT; }");
                    break;
                case ShaderStage.Fragment:
                    sb.AppendLine("struct PSInput { float4 position : SV_Position; float4 color : COLOR0; };");
                    sb.AppendLine("float4 main(PSInput IN) : SV_Target { return IN.color; }");
                    break;
                case ShaderStage.Compute:
                    sb.AppendLine("[numthreads(32, 1, 1)]");
                    sb.AppendLine("void main(uint3 dispatchThreadID : SV_DispatchThreadID) { }");
                    break;
                case ShaderStage.Geometry:
                    sb.AppendLine("struct GSInput { float4 position : SV_Position; };");
                    sb.AppendLine("[maxvertexcount(3)]");
                    sb.AppendLine("void main(triangle GSInput input[3], inout TriangleStream<float4> outStream) { for(int i=0;i<3;i++) outStream.Append(input[i].position); }");
                    break;
                case ShaderStage.TessellationControl:
                    sb.AppendLine("[domain(\"tri\")] [partitioning(\"integer\")] [outputtopology(\"triangle_cw\")] [patchconstantfunc(\"PatchConstant\")] [outputcontrolpoints(3)]");
                    sb.AppendLine("float4 main(InputPatch<float4,3> patch, uint id : SV_OutputControlPointID) : SV_Position { return patch[id]; }");
                    sb.AppendLine("struct PatchConstant { float edge[3] : SV_TessFactor; float inside : SV_InsideTessFactor; };");
                    sb.AppendLine("PatchConstant PatchConstant() { PatchConstant p; p.edge[0]=p.edge[1]=p.edge[2]=1; p.inside=1; return p; }");
                    break;
                case ShaderStage.TessellationEvaluation:
                    sb.AppendLine("[domain(\"tri\")]");
                    sb.AppendLine("float4 main(float3 bary : SV_DomainLocation, const OutputPatch<float4,3> patch) : SV_Position { return patch[0]*bary.x + patch[1]*bary.y + patch[2]*bary.z; }");
                    break;
                default:
                    sb.AppendLine("void main() {}");
                    break;
            }

            return sb.ToString();
        }
    }
}
