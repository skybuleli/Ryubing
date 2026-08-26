using Ryujinx.Graphics.Shader.CodeGen.Glsl;
using Ryujinx.Graphics.Shader.StructuredIr;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ryujinx.Graphics.Shader.CodeGen.Slang
{
    /// <summary>
    /// Structured IR 到 Slang/HLSL 的源码 lowering。
    ///
    /// 当前复用 GLSL backend 的 AST/控制流/指令语义，再转换目标方言。
    /// 这样不会重新实现 Maxwell 指令语义；后续可将本类拆成直接 HLSL emitter，
    /// 但两条路径会继续共享 Structured IR 和资源分析结果。
    /// </summary>
    static class SlangGenerator
    {
        private static readonly Regex InterfaceDeclaration = new(
            @"layout\s*\(\s*location\s*=\s*(?<location>\d+)(?:\s*,\s*component\s*=\s*\d+)?(?:\s*,\s*index\s*=\s*\d+)?\s*\)\s*(?:(?:flat|noperspective|centroid|invariant)\s+)*(?<direction>in|out)\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)(?:\s+(?<precision>precise))?\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<array>\[[^]]*\])?\s*;",
            RegexOptions.Compiled);

        private static readonly Regex BufferDeclaration = new(
            @"layout\s*\([^)]*?\bbinding\s*=\s*(?<binding>\d+)[^)]*\)\s+(?<kind>uniform|buffer)\s+_[A-Za-z_][A-Za-z0-9_]*\s*\{(?<body>.*?)\}\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*;",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex TextureDeclaration = new(
            @"layout\s*\(\s*binding\s*=\s*(?<binding>\d+)[^)]*\)\s+uniform\s+(?<type>(?:sampler|texture)[A-Za-z0-9_]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<array>\[[^]]*\])?\s*;",
            RegexOptions.Compiled);

        private static readonly Regex ImageDeclaration = new(
            @"layout\s*\(\s*binding\s*=\s*(?<binding>\d+)[^)]*\)\s+uniform\s+(?<qualifier>coherent\s+)?(?<type>[iu]?image[A-Za-z0-9_]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<array>\[[^]]*\])?\s*;",
            RegexOptions.Compiled);

        private static readonly Regex MainSignature = new(
            @"(?<returnType>[A-Za-z_][A-Za-z0-9_]*)\s+main\s*\(\s*\)\s*\{",
            RegexOptions.Compiled);

        private readonly record struct InterfaceField(string Type, string Name, int Location, bool IsInput);

        public static string Generate(StructuredProgramInfo info, CodeGenParameters parameters)
        {
            if (info.Functions.Count == 0)
            {
                throw new InvalidOperationException("Structured shader contains no functions.");
            }

            // GLSL generator already consumes every StructuredFunction/AST node and performs
            // the resource and I/O analysis. It is used only as a semantic lowering source.
            string glsl = GlslGenerator.Generate(info, parameters);
            return LowerToSlang(glsl, parameters.Definitions);
        }

        private static string LowerToSlang(string glsl, ShaderDefinitions definitions)
        {
            List<InterfaceField> inputs = [];
            List<InterfaceField> outputs = [];

            string source = RemovePreprocessorDirectives(glsl);
            source = LowerBuffers(source);
            source = LowerTextures(source);
            source = LowerImages(source);
            source = LowerMemoryDeclarations(source);
            source = RemoveStaticFromConstantBuffers(source);
            source = RemoveInterfaceDeclarations(source, inputs, outputs);
            source = RemoveUnsupportedLayoutQualifiers(source);
            source = ConvertTypesAndIntrinsics(source);

            return definitions.Stage switch
            {
                ShaderStage.Vertex or ShaderStage.Fragment =>
                    LowerGraphicsEntryPoint(source, definitions.Stage, inputs, outputs),
                ShaderStage.Compute => LowerComputeEntryPoint(source, definitions),
                _ => throw new NotSupportedException($"Slang lowering for {definitions.Stage} is not implemented yet."),
            };
        }

        private static string RemovePreprocessorDirectives(string source)
        {
            return Regex.Replace(source, @"^\s*#(?:version|extension|pragma).*$(\r?\n)?", string.Empty, RegexOptions.Multiline);
        }

        private static string LowerBuffers(string source)
        {
            List<string> storageNames = [];
            List<string> uniformNames = [];

            source = BufferDeclaration.Replace(source, match =>
            {
                int binding = int.Parse(match.Groups["binding"].Value);
                string kind = match.Groups["kind"].Value;
                string name = match.Groups["name"].Value;
                string body = ConvertTypesAndIntrinsics(match.Groups["body"].Value.Trim());
                body = Regex.Replace(body, @"\bstatic\s+", string.Empty);

                if (kind == "uniform")
                {
                    uniformNames.Add(name);
                    return $"cbuffer {name} : register(b{binding})\n{{\n{body}\n}};";
                }

                // GLSL's buffer variable is a struct instance. HLSL's structured buffer is an
                // array of structs, so accesses emitted by the shared backend are rewritten to
                // the first element below. Dynamic field arrays remain indexed after the field.
                body = Regex.Replace(body, @"(?<type>[A-Za-z_][A-Za-z0-9_<>]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\[\s*\];", "${type} ${name}[1];");
                string structName = $"{name}_Storage";
                storageNames.Add(name);
                return $"struct {structName}\n{{\n{body}\n}};\nRWStructuredBuffer<{structName}> {name} : register(u{binding});";
            });

            foreach (string name in storageNames)
            {
                source = Regex.Replace(source, $@"\b{Regex.Escape(name)}\.", $"{name}[0].");
            }

            // GLSL exposes a named uniform-block instance (vp_c3.data), while HLSL cbuffer
            // members are visible directly in the surrounding scope (data). Keep the member
            // name and remove only the block-instance qualifier.
            foreach (string name in uniformNames)
            {
                source = Regex.Replace(source, $@"\b{Regex.Escape(name)}\.", string.Empty);
            }

            return source;
        }

        private static string LowerTextures(string source)
        {
            List<string> bufferNames = [];

            source = TextureDeclaration.Replace(source, match =>
            {
                int binding = int.Parse(match.Groups["binding"].Value);
                string glslType = match.Groups["type"].Value;
                string name = match.Groups["name"].Value;
                string array = match.Groups["array"].Value;

                if (glslType == "sampler")
                {
                    return $"SamplerState {name}{array} : register(s{binding});";
                }

                if (glslType is "samplerBuffer" or "textureBuffer")
                {
                    bufferNames.Add(name);
                    return $"Buffer<float4> {name}{array} : register(t{binding});";
                }

                string type = ToHlslTextureType(glslType);

                if (glslType.StartsWith("texture", StringComparison.Ordinal))
                {
                    // A GLSL texture* declaration is the texture half of a separate pair;
                    // its sampler is declared independently and appears in the constructor
                    // handled by LowerTextureCalls.
                    return $"{type} {name}{array} : register(t{binding});";
                }

                // Combined samplers become the native HLSL texture/sampler pair.
                return $"{type} {name}{array} : register(t{binding});\nSamplerState {name}_sampler{array} : register(s{binding});";
            });

            foreach (string name in bufferNames)
            {
                source = Regex.Replace(source, $@"\btexture\(\s*{Regex.Escape(name)}\s*,", $"{name}.Load(");
                source = Regex.Replace(source, $@"\btexelFetch\(\s*{Regex.Escape(name)}\s*,", $"{name}.Load(");
            }

            return source;
        }

        private static string LowerImages(string source)
        {
            return ImageDeclaration.Replace(source, match =>
            {
                int binding = int.Parse(match.Groups["binding"].Value);
                string type = ToHlslImageType(match.Groups["type"].Value);
                string name = match.Groups["name"].Value;
                string array = match.Groups["array"].Value;

                return $"{type} {name}{array} : register(u{binding});";
            });
        }

        private static string ToHlslImageType(string type)
        {
            string scalarType = type.StartsWith("iimage", StringComparison.Ordinal)
                ? "int4"
                : type.StartsWith("uimage", StringComparison.Ordinal) ? "uint4" : "float4";
            string dimension = (type.StartsWith("iimage", StringComparison.Ordinal) || type.StartsWith("uimage", StringComparison.Ordinal) ? type[1..] : type) switch
            {
                "image1D" => "RWTexture1D",
                "image2D" => "RWTexture2D",
                "image2DArray" => "RWTexture2DArray",
                "image2DMS" => "RWTexture2DMS",
                "image2DMSArray" => "RWTexture2DMSArray",
                "image3D" => "RWTexture3D",
                "imageCube" => "RWTextureCube",
                "imageCubeArray" => "RWTextureCubeArray",
                "imageBuffer" => "RWStructuredBuffer",
                _ => throw new NotSupportedException($"Image type {type} is not supported by Slang lowering."),
            };

            return $"{dimension}<{scalarType}>";
        }

        private static string LowerImageCalls(string source)
        {
            int searchIndex = 0;

            while (searchIndex < source.Length)
            {
                int functionIndex = source.IndexOf("imageLoad(", searchIndex, StringComparison.Ordinal);
                int storeIndex = source.IndexOf("imageStore(", searchIndex, StringComparison.Ordinal);
                int nextIndex = functionIndex < 0 ? storeIndex : storeIndex < 0 ? functionIndex : Math.Min(functionIndex, storeIndex);

                if (nextIndex < 0)
                {
                    break;
                }

                string function = source.AsSpan(nextIndex).StartsWith("imageLoad(", StringComparison.Ordinal) ? "imageLoad" : "imageStore";
                int openBrace = nextIndex + function.Length;
                int closeBrace = FindMatchingParenthesis(source, openBrace);
                List<string> arguments = SplitArguments(source[(openBrace + 1)..closeBrace]);

                if (arguments.Count < 2 || (function == "imageStore" && arguments.Count < 3))
                {
                    throw new InvalidOperationException($"Invalid {function} argument list in generated shader.");
                }

                string replacement = function == "imageLoad"
                    ? $"{arguments[0]}.Load({arguments[1]})"
                    : $"{arguments[0]}[{arguments[1]}] = {arguments[2]}";

                source = source[..nextIndex] + replacement + source[(closeBrace + 1)..];
                searchIndex = nextIndex + replacement.Length;
            }

            return source;
        }

        private static List<string> SplitArguments(string arguments)
        {
            List<string> result = [];
            int start = 0;
            int depth = 0;

            for (int index = 0; index < arguments.Length; index++)
            {
                switch (arguments[index])
                {
                    case '(':
                    case '[':
                        depth++;
                        break;
                    case ')':
                    case ']':
                        depth--;
                        break;
                    case ',' when depth == 0:
                        result.Add(arguments[start..index].Trim());
                        start = index + 1;
                        break;
                }
            }

            result.Add(arguments[start..].Trim());
            return result;
        }

        private static string LowerMemoryDeclarations(string source)
        {
            return Regex.Replace(
                source,
                @"(?m)^(?<indent>\s*)(?<shared>shared\s+)?(?<type>(?:precise\s+)?[A-Za-z_][A-Za-z0-9_]*)(?<vector>\d*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<array>\[([^]]*)\])\s*;",
                match =>
                {
                    string type = match.Groups["type"].Value + match.Groups["vector"].Value;
                    string arraySize = match.Groups["array"].Value;
                    if (arraySize == "[]")
                    {
                        arraySize = "[1]";
                    }

                    string storage = match.Groups["shared"].Success ? "groupshared " : "static ";
                    return $"{match.Groups["indent"].Value}{storage}{type} {match.Groups["name"].Value}{arraySize};";
                });
        }

        private static string RemoveStaticFromConstantBuffers(string source)
        {
            return Regex.Replace(
                source,
                @"(?<prefix>cbuffer\s+[A-Za-z_][A-Za-z0-9_]*\s*:\s*register\s*\([^)]*\)\s*\{)(?<body>.*?)(?<suffix>\})",
                match =>
                {
                    string body = Regex.Replace(match.Groups["body"].Value, @"\bstatic\s+", string.Empty);
                    return match.Groups["prefix"].Value + body + match.Groups["suffix"].Value;
                },
                RegexOptions.Compiled | RegexOptions.Singleline);
        }

        private static string RemoveInterfaceDeclarations(
            string source,
            List<InterfaceField> inputs,
            List<InterfaceField> outputs)
        {
            return InterfaceDeclaration.Replace(source, match =>
            {
                string type = ConvertType(match.Groups["type"].Value);
                string name = match.Groups["name"].Value;
                bool isInput = match.Groups["direction"].Value == "in";
                int location = int.Parse(match.Groups["location"].Value);

                if (match.Groups["array"].Success)
                {
                    throw new NotSupportedException($"Array interface {name} at location {location} requires geometry/tessellation lowering.");
                }

                (isInput ? inputs : outputs).Add(new InterfaceField(type, name, location, isInput));
                return string.Empty;
            });
        }

        private static string RemoveUnsupportedLayoutQualifiers(string source)
        {
            // The interface and buffer declarations were consumed above. Remove complete GLSL
            // stage declarations as well; removing only the layout(...) token would leave an
            // invalid dangling "in;" in compute shaders.
            source = Regex.Replace(
                source,
                @"layout\s*\([^)]*\)\s+(?:(?:in|out)\s+)?(?:[A-Za-z_][A-Za-z0-9_]*\s+)?gl_[A-Za-z_][A-Za-z0-9_]*\s*;",
                string.Empty);
            source = Regex.Replace(source, @"layout\s*\([^)]*\)\s*(?:in|out)\s*;", string.Empty);
            return Regex.Replace(source, @"layout\s*\([^)]*\)\s*", string.Empty);
        }

        private static string LowerGraphicsEntryPoint(
            string source,
            ShaderStage stage,
            List<InterfaceField> inputs,
            List<InterfaceField> outputs)
        {
            StringBuilder prefix = new();
            prefix.AppendLine("// Generated from Structured IR; semantic lowering source is the shared GLSL backend.");
            prefix.AppendLine();
            prefix.AppendLine("struct SlangInput");
            prefix.AppendLine("{");

            foreach (InterfaceField field in inputs.OrderBy(x => x.Location))
            {
                prefix.AppendLine($"    {field.Type} {field.Name} : TEXCOORD{field.Location};");
            }

            if (stage == ShaderStage.Vertex)
            {
                prefix.AppendLine("    uint vertexId : SV_VertexID;");
                prefix.AppendLine("    uint instanceId : SV_InstanceID;");
            }
            else
            {
                prefix.AppendLine("    float4 position : SV_Position;");
                prefix.AppendLine("    bool frontFacing : SV_IsFrontFace;");
            }

            prefix.AppendLine("};");
            prefix.AppendLine();
            prefix.AppendLine("struct SlangOutput");
            prefix.AppendLine("{");

            if (stage == ShaderStage.Vertex)
            {
                prefix.AppendLine("    float4 position : SV_Position;");
                prefix.AppendLine("    float pointSize : PSIZE;");
            }

            foreach (InterfaceField field in outputs.OrderBy(x => x.Location))
            {
                string semantic = stage == ShaderStage.Fragment ? $"SV_Target{field.Location}" : $"TEXCOORD{field.Location}";
                prefix.AppendLine($"    {field.Type} {field.Name} : {semantic};");
            }

            if (stage == ShaderStage.Fragment && source.Contains("gl_FragDepth", StringComparison.Ordinal))
            {
                prefix.AppendLine("    float depth : SV_Depth;");
            }

            prefix.AppendLine("};");
            prefix.AppendLine();

            string rewritten = RewriteMain(source, stage, inputs, outputs);
            prefix.Append(rewritten);

            return prefix.ToString();
        }

        private static string RewriteMain(
            string source,
            ShaderStage stage,
            List<InterfaceField> inputs,
            List<InterfaceField> outputs)
        {
            Match match = MainSignature.Match(source);
            if (!match.Success)
            {
                throw new InvalidOperationException("Generated semantic shader has no void main() entry point.");
            }

            int bodyStart = match.Index + match.Length;
            int bodyEnd = FindMatchingBrace(source, bodyStart - 1);
            string before = source[..match.Index];
            string body = source[bodyStart..bodyEnd];
            string after = source[(bodyEnd + 1)..];

            body = body.Replace("return;", "return output;", StringComparison.Ordinal);

            StringBuilder entry = new();
            entry.Append(before);
            entry.AppendLine(stage == ShaderStage.Vertex
                ? "SlangOutput main(SlangInput input)"
                : "SlangOutput main(SlangInput input)");
            entry.AppendLine("{");
            entry.AppendLine("    SlangOutput output = (SlangOutput)0;");

            foreach (InterfaceField field in inputs)
            {
                entry.AppendLine($"    #define {field.Name} input.{field.Name}");
            }

            foreach (InterfaceField field in outputs)
            {
                entry.AppendLine($"    #define {field.Name} output.{field.Name}");
            }

            if (stage == ShaderStage.Vertex)
            {
                entry.AppendLine("    #define gl_Position output.position");
                entry.AppendLine("    #define gl_PointSize output.pointSize");
                entry.AppendLine("    #define gl_VertexID input.vertexId");
                entry.AppendLine("    #define gl_VertexIndex input.vertexId");
                entry.AppendLine("    #define gl_InstanceID input.instanceId");
                entry.AppendLine("    #define gl_InstanceIndex input.instanceId");
            }
            else
            {
                entry.AppendLine("    #define gl_FragCoord input.position");
                entry.AppendLine("    #define gl_FragDepth output.depth");
                entry.AppendLine("    #define gl_FrontFacing input.frontFacing");
            }

            entry.Append(body);

            if (!body.Contains("return output;", StringComparison.Ordinal))
            {
                entry.AppendLine();
                entry.AppendLine("    return output;");
            }

            entry.AppendLine("}");
            entry.Append(after);
            return entry.ToString();
        }

        private static string LowerComputeEntryPoint(string source, ShaderDefinitions definitions)
        {
            Match match = MainSignature.Match(source);
            if (!match.Success)
            {
                throw new InvalidOperationException("Generated compute shader has no void main() entry point.");
            }

            int bodyStart = match.Index + match.Length;
            int bodyEnd = FindMatchingBrace(source, bodyStart - 1);
            string before = source[..match.Index];
            string body = source[bodyStart..bodyEnd].Replace("return;", "return;", StringComparison.Ordinal);
            string after = source[(bodyEnd + 1)..];

            StringBuilder result = new();
            result.AppendLine("// Generated from Structured IR compute semantics.");
            result.Append(before);
            result.AppendLine($"[numthreads({definitions.ComputeLocalSizeX}, {definitions.ComputeLocalSizeY}, {definitions.ComputeLocalSizeZ})]");
            result.AppendLine("void main(uint3 globalId : SV_DispatchThreadID, uint3 localId : SV_GroupThreadID, uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)");
            result.AppendLine("{");
            result.AppendLine("    #define gl_GlobalInvocationID globalId");
            result.AppendLine("    #define gl_LocalInvocationID localId");
            result.AppendLine("    #define gl_WorkGroupID groupId");
            result.AppendLine("    #define gl_LocalInvocationIndex groupIndex");
            result.AppendLine($"    #define gl_WorkGroupSize uint3({definitions.ComputeLocalSizeX}, {definitions.ComputeLocalSizeY}, {definitions.ComputeLocalSizeZ})");
            result.Append(body);
            result.AppendLine("}");
            result.Append(after);
            return result.ToString();
        }

        private static string ConvertTypesAndIntrinsics(string source)
        {
            string result = source;

            foreach ((string glsl, string hlsl) in new[]
            {
                ("dvec4", "double4"), ("dvec3", "double3"), ("dvec2", "double2"),
                ("uvec4", "uint4"), ("uvec3", "uint3"), ("uvec2", "uint2"),
                ("ivec4", "int4"), ("ivec3", "int3"), ("ivec2", "int2"),
                ("bvec4", "bool4"), ("bvec3", "bool3"), ("bvec2", "bool2"),
                ("vec4", "float4"), ("vec3", "float3"), ("vec2", "float2"),
                ("mat4", "float4x4"), ("mat3", "float3x3"), ("mat2", "float2x2"),
            })
            {
                result = Regex.Replace(result, $@"\b{glsl}\b", hlsl);
            }

            foreach ((string glsl, string hlsl) in new[]
            {
                ("floatBitsToInt", "asint"),
                ("floatBitsToUint", "asuint"),
                ("intBitsToFloat", "asfloat"),
                ("uintBitsToFloat", "asfloat"),
                ("dFdx", "ddx"),
                ("dFdy", "ddy"),
                ("inversesqrt", "rsqrt"),
                ("fract", "frac"),
                ("mix", "lerp"),
                ("mod", "fmod"),
                ("roundEven", "round"),
                ("bitCount", "countbits"),
                ("barrier", "GroupMemoryBarrierWithGroupSync"),
                ("groupMemoryBarrier", "GroupMemoryBarrier"),
                ("memoryBarrier", "AllMemoryBarrier"),
            })
            {
                result = Regex.Replace(result, $@"\b{glsl}\b", hlsl);
            }

            result = Regex.Replace(result, @"\bconst\s+int\b", "static const int");
            result = LowerImageCalls(result);
            return LowerTextureCalls(result);
        }

        private static string LowerTextureCalls(string source)
        {
            // A separate GLSL texture/sampler is emitted as sampler2D(texture, sampler).
            // Handle that complete call first so the sampler expression is preserved.
            source = Regex.Replace(
                source,
                @"\b(?<call>textureQueryLod|textureGatherOffsets|textureGatherOffset|textureGather|textureOffset|textureLod|textureGrad|texture)\(\s*sampler[A-Za-z0-9_]+\(\s*(?<texture>[A-Za-z_][A-Za-z0-9_]*(?:\[[^]]+\])?)\s*,\s*(?<sampler>[A-Za-z_][A-Za-z0-9_]*(?:\[[^]]+\])?)\s*\)\s*,",
                match => $"{match.Groups["texture"].Value}.{GetTextureMethod(match.Groups["call"].Value)}({match.Groups["sampler"].Value},",
                RegexOptions.Compiled);

            source = Regex.Replace(
                source,
                @"\b(?<call>textureQueryLod|textureGatherOffsets|textureGatherOffset|textureGather|textureOffset|textureLod|textureGrad)\(\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<index>\[[^]]+\])?\s*,",
                match => $"{match.Groups["name"].Value}{match.Groups["index"].Value}.{GetTextureMethod(match.Groups["call"].Value)}({GetSamplerExpression(match.Groups["name"].Value, match.Groups["index"].Value)},",
                RegexOptions.Compiled);

            source = Regex.Replace(
                source,
                @"\btexture\(\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<index>\[[^]]+\])?\s*,",
                match => $"{match.Groups["name"].Value}{match.Groups["index"].Value}.{GetTextureMethod("texture")}({GetSamplerExpression(match.Groups["name"].Value, match.Groups["index"].Value)},",
                RegexOptions.Compiled);

            return source;
        }

        private static string GetTextureMethod(string call)
        {
            return call switch
            {
                "textureQueryLod" => "CalculateLevel",
                "textureLod" => "SampleLevel",
                "textureGrad" => "SampleGrad",
                "textureGather" or "textureGatherOffset" or "textureGatherOffsets" => "Gather",
                _ => "Sample",
            };
        }

        private static string GetSamplerExpression(string textureName, string index)
        {
            return $"{textureName}_sampler{index}";
        }

        private static string ToHlslTextureType(string type)
        {
            return type switch
            {
                "sampler1D" or "texture1D" => "Texture1D",
                "sampler2D" or "texture2D" or "sampler2DShadow" => "Texture2D",
                "sampler2DArray" or "texture2DArray" => "Texture2DArray",
                "sampler3D" or "texture3D" => "Texture3D",
                "samplerCube" or "textureCube" => "TextureCube",
                "sampler2DMS" or "texture2DMS" => "Texture2DMS",
                "samplerBuffer" or "textureBuffer" => "Buffer<float4>",
                _ => throw new NotSupportedException($"Texture type {type} is not supported by Slang lowering."),
            };
        }

        private static string ConvertType(string type)
        {
            return type switch
            {
                "vec2" => "float2",
                "vec3" => "float3",
                "vec4" => "float4",
                "ivec2" => "int2",
                "ivec3" => "int3",
                "ivec4" => "int4",
                "uvec2" => "uint2",
                "uvec3" => "uint3",
                "uvec4" => "uint4",
                "bvec2" => "bool2",
                "bvec3" => "bool3",
                "bvec4" => "bool4",
                _ => type,
            };
        }

        private static int FindMatchingParenthesis(string source, int openingParenthesis)
        {
            int depth = 0;

            for (int index = openingParenthesis; index < source.Length; index++)
            {
                switch (source[index])
                {
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        if (depth == 0)
                        {
                            return index;
                        }
                        break;
                }
            }

            throw new InvalidOperationException("Unbalanced parentheses in generated shader.");
        }

        private static int FindMatchingBrace(string source, int openingBrace)
        {
            int depth = 0;

            for (int index = openingBrace; index < source.Length; index++)
            {
                switch (source[index])
                {
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 0)
                        {
                            return index;
                        }
                        break;
                }
            }

            throw new InvalidOperationException("Unbalanced braces in generated shader.");
        }
    }
}
