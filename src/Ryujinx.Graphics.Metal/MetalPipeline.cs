using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal
{
    class MetalPipeline : IPipeline
    {
        private readonly MetalRenderer _renderer;
        private readonly Dictionary<Format, MetalProgram> _drawTexturePrograms = new();
        private readonly Dictionary<Format, MetalProgram> _clearPrograms = new();
        private BufferHandle _drawTextureParameters = BufferHandle.Null;
        private BufferHandle _clearParameters = BufferHandle.Null;
        private int _width = 1280, _height = 720;
        private MetalTexture _colorTarget;
        private MetalTexture _depthTarget;
        private MetalProgram _program;
        private DepthTestDescriptor _depthTest;
        private StencilTestDescriptor _stencilTest;
        private BlendDescriptor _blendDescriptor;
        private uint _colorWriteMask = 0xF;
        private bool _cullEnable;
        private Face _cullMode;
        private FrontFace _frontFace = FrontFace.CounterClockwise;
        private bool _depthClamp;
        private bool _rasterizerDiscard;
        private PolygonModeMask _depthBiasEnables;
        private float _depthBiasFactor;
        private float _depthBiasUnits;
        private float _depthBiasClamp;
        private float _lineWidth = 1f;
        private float _pointSize = 1f;
        private bool _lineSmooth;
        private bool _primitiveRestart;
        private int _primitiveRestartIndex;
        private bool _alphaTestEnable;
        private float _alphaTestReference;
        private CompareOp _alphaTestOp;
        private bool[] _userClipDistances = Array.Empty<bool>();
        private int _patchVertices;
        private float[] _patchOuterLevels = Array.Empty<float>();
        private float[] _patchInnerLevels = Array.Empty<float>();
        private            MultisampleDescriptor _multisample;
        private bool _multisampleSet;
        private bool _logicOpEnable;
        private bool _logicOpSet;
        private LogicalOp _logicOp = LogicalOp.Copy;
        private bool _blendSet;
        private bool _colorWriteMaskSet;
        private PolygonMode _polygonFront = PolygonMode.Fill;
        private PolygonMode _polygonBack = PolygonMode.Fill;
        private VertexBufferDescriptor[] _vertexBuffers = Array.Empty<VertexBufferDescriptor>();
        private BufferRange _indexBuffer = BufferRange.Empty;
        private IndexType _indexType;
        private PrimitiveTopology _topology = PrimitiveTopology.Triangles;
        private VertexAttribDescriptor[] _vertexAttribs = Array.Empty<VertexAttribDescriptor>();
        private Viewport[] _viewports = Array.Empty<Viewport>();
        private Rectangle<int>[] _scissors = Array.Empty<Rectangle<int>>();
        private readonly Dictionary<(ShaderStage Stage, int Binding), (MetalTexture Texture, MetalSampler Sampler)> _textures = new();
        private readonly Dictionary<(ShaderStage Stage, int Binding), MetalTexture> _images = new();
        private readonly Dictionary<(ShaderStage Stage, int Binding), MetalTextureArray> _textureArrays = new();
        private readonly Dictionary<(ShaderStage Stage, int Binding), MetalImageArray> _imageArrays = new();
        private readonly Dictionary<(ShaderStage Stage, int Set), MetalTextureArray> _separateTextureArrays = new();
        private readonly Dictionary<(ShaderStage Stage, int Set), MetalImageArray> _separateImageArrays = new();
        private readonly Dictionary<int, BufferRange> _uniformBuffers = new();
        private readonly Dictionary<int, BufferRange> _storageBuffers = new();

        public MetalPipeline(MetalRenderer renderer)
        {
            _renderer = renderer;
        }

        public void Barrier() { }
        public void BeginTransformFeedback(PrimitiveTopology topology) { }
        public void ClearBuffer(BufferHandle destination, int offset, int size, uint value)
        {
            if (destination == BufferHandle.Null || size <= 0)
            {
                return;
            }

            Span<byte> values = stackalloc byte[4];
            MemoryMarshal.Write(values, value);
            byte[] data = new byte[size];
            for (int index = 0; index < data.Length; index++)
            {
                data[index] = values[index & 3];
            }
            _renderer.SetBufferData(destination, offset, data);
        }
        public void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color)
        {
            if (_colorTarget == null || _colorTarget.NativeTexture == nint.Zero)
            {
                return;
            }

            if ((componentMask & 0xF) == 0xF)
            {
                MetalContext.ClearTexture(_colorTarget, ToRgba32(color));
                return;
            }

            // 部分通道掩码：render pass 清除无法按通道过滤（与 Vulkan CmdClearAttachments 相同限制），
            // 走 helper 着色器绘制 + 写掩码 PSO，保持未清除通道不变。
            MetalProgram clearProgram = GetClearProgram(_colorTarget.Format);
            if (clearProgram == null || clearProgram.Pipeline == nint.Zero)
            {
                return;
            }

            if (_clearParameters == BufferHandle.Null)
            {
                _clearParameters = _renderer.CreateBuffer(16);
            }

            float[] parameters = [color.Red, color.Green, color.Blue, color.Alpha];
            _renderer.SetBufferData(_clearParameters, 0, MemoryMarshal.AsBytes(parameters.AsSpan()));

            // 写掩码是 PSO 状态；PSO 缓存使不同掩码组合只在首次创建。
            clearProgram.SetColorWriteMask(componentMask & 0xF);

            Viewport[] viewports =
            [
                new Viewport(
                    new Rectangle<float>(0, 0, _colorTarget.Width, _colorTarget.Height),
                    ViewportSwizzle.PositiveX,
                    ViewportSwizzle.PositiveY,
                    ViewportSwizzle.PositiveZ,
                    ViewportSwizzle.PositiveW,
                    0,
                    1),
            ];
            Dictionary<int, BufferRange> uniformBuffers = new()
            {
                [0] = new BufferRange(_clearParameters, 0, 16),
            };

            MetalContext.EncodeDraw(
                _renderer,
                clearProgram,
                _colorTarget,
                null,
                default,
                default,
                default,
                componentMask & 0xF,
                false,
                default,
                FrontFace.CounterClockwise,
                false,
                false,
                default,
                0,
                0,
                0,
                1,
                1,
                PolygonMode.Fill,
                PolygonMode.Fill,
                default,
                false,
                false,
                LogicalOp.Copy,
                false,
                Array.Empty<VertexBufferDescriptor>(),
                Array.Empty<VertexAttribDescriptor>(),
                viewports,
                Array.Empty<Rectangle<int>>(),
                new Dictionary<(ShaderStage Stage, int Binding), (MetalTexture Texture, MetalSampler Sampler)>(),
                new Dictionary<(ShaderStage Stage, int Binding), MetalTexture>(),
                new Dictionary<(ShaderStage Stage, int Binding), MetalTextureArray>(),
                new Dictionary<(ShaderStage Stage, int Binding), MetalImageArray>(),
                uniformBuffers,
                new Dictionary<int, BufferRange>(),
                BufferRange.Empty,
                IndexType.UShort,
                PrimitiveTopology.Triangles,
                3,
                1,
                0,
                0,
                0,
                false);
        }

        private static uint ToRgba32(ColorF color) =>
            ((uint)(color.Alpha * 255) << 24) | ((uint)(color.Red * 255) << 16) | ((uint)(color.Green * 255) << 8) | (uint)(color.Blue * 255);

        /// <summary>
        /// 部分掩码清除用的 helper 程序：全屏三角形输出 uniform 颜色。
        /// </summary>
        private MetalProgram GetClearProgram(Format targetFormat)
        {
            if (_clearPrograms.TryGetValue(targetFormat, out MetalProgram existing))
            {
                return existing;
            }

            const string vertex = """
                struct ClearOutput
                {
                    float4 position : SV_Position;
                };

                ClearOutput main(uint vertexId : SV_VertexID)
                {
                    ClearOutput output;
                    float x = (vertexId == 1) ? 3.0 : -1.0;
                    float y = (vertexId == 2) ? 3.0 : -1.0;
                    output.position = float4(x, y, 0.0, 1.0);
                    return output;
                }
                """;
            const string fragment = """
                cbuffer ClearParams : register(b0)
                {
                    float4 clearColor;
                };

                float4 main(float4 position : SV_Position) : SV_Target
                {
                    return clearColor;
                }
                """;

            ProgramPipelineState state = new();
            state.AttachmentEnable[0] = true;
            state.AttachmentFormats[0] = targetFormat;
            state.ColorWriteMask[0] = 0xF;
            state.BlendDescriptors[0] = new BlendDescriptor(
                false,
                default,
                BlendOp.Add,
                BlendFactor.One,
                BlendFactor.Zero,
                BlendOp.Add,
                BlendFactor.One,
                BlendFactor.Zero);

            MetalProgram program = _renderer.CreateProgram(
                [
                    new ShaderSource(vertex, ShaderStage.Vertex, TargetLanguage.Slang),
                    new ShaderSource(fragment, ShaderStage.Fragment, TargetLanguage.Slang),
                ],
                new ShaderInfo(0, default, state)) as MetalProgram;
            if (program?.CheckProgramLink(true) != ProgramLinkStatus.Success)
            {
                program?.Dispose();
                return null;
            }

            _clearPrograms[targetFormat] = program;
            return program;
        }
        public void ClearRenderTargetDepthStencil(int layer, int layerCount, float depthValue, bool depthMask, int stencilValue, int stencilMask)
        {
            MetalContext.ClearDepthStencil(_depthTarget, depthValue, stencilValue);
        }
        public void CommandBufferBarrier() { }
        public void CopyBuffer(BufferHandle source, BufferHandle destination, int srcOffset, int dstOffset, int size)
        {
            if (source == BufferHandle.Null || destination == BufferHandle.Null || size <= 0)
            {
                return;
            }

            using PinnedSpan<byte> data = _renderer.GetBufferData(source, srcOffset, size);
            _renderer.SetBufferData(destination, dstOffset, data.Get());
        }
        public void DispatchCompute(int groupsX, int groupsY, int groupsZ)
        {
            MetalContext.EncodeCompute(_renderer, _program, _textures, _images, _uniformBuffers, _storageBuffers, groupsX, groupsY, groupsZ);
        }
        public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance)
        {
            RefreshResourceArrays();
            ApplyDynamicProgramState();
            MetalContext.EncodeDraw(_renderer, _program, _colorTarget, _depthTarget, _depthTest, _stencilTest, _blendDescriptor, _colorWriteMask, _cullEnable, _cullMode, _frontFace, _depthClamp, _rasterizerDiscard, _depthBiasEnables, _depthBiasFactor, _depthBiasUnits, _depthBiasClamp, _lineWidth, _pointSize, _polygonFront, _polygonBack, _multisample, _multisampleSet, _logicOpEnable, _logicOp, _logicOpSet, _vertexBuffers, _vertexAttribs, _viewports, _scissors, _textures, _images, _textureArrays, _imageArrays, _uniformBuffers, _storageBuffers, _indexBuffer, _indexType, _topology, vertexCount, instanceCount, firstVertex, firstInstance, 0, false);
        }
        public void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int firstVertex, int firstInstance)
        {
            RefreshResourceArrays();
            ApplyDynamicProgramState();
            MetalContext.EncodeDraw(_renderer, _program, _colorTarget, _depthTarget, _depthTest, _stencilTest, _blendDescriptor, _colorWriteMask, _cullEnable, _cullMode, _frontFace, _depthClamp, _rasterizerDiscard, _depthBiasEnables, _depthBiasFactor, _depthBiasUnits, _depthBiasClamp, _lineWidth, _pointSize, _polygonFront, _polygonBack, _multisample, _multisampleSet, _logicOpEnable, _logicOp, _logicOpSet, _vertexBuffers, _vertexAttribs, _viewports, _scissors, _textures, _images, _textureArrays, _imageArrays, _uniformBuffers, _storageBuffers, _indexBuffer, _indexType, _topology, indexCount, instanceCount, firstVertex, firstInstance, firstIndex, true);
        }
        public void DrawIndexedIndirect(BufferRange indirectBuffer)
        {
            byte[] data = ReadIndirectBuffer(indirectBuffer);
            if (data.Length < 20)
            {
                return;
            }

            ReadOnlySpan<byte> command = data;
            DrawIndexed(
                checked((int)MemoryMarshal.Read<uint>(command[0..4])),
                checked((int)MemoryMarshal.Read<uint>(command[4..8])),
                checked((int)MemoryMarshal.Read<uint>(command[8..12])),
                MemoryMarshal.Read<int>(command[12..16]),
                checked((int)MemoryMarshal.Read<uint>(command[16..20])));
        }
        public void DrawIndexedIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
        {
            byte[] countData = ReadIndirectBuffer(parameterBuffer);
            if (countData.Length < 4)
            {
                return;
            }

            int drawCount = Math.Min(checked((int)MemoryMarshal.Read<uint>(countData)), Math.Max(0, maxDrawCount));
            int commandStride = stride > 0 ? stride : 20;
            byte[] commands = ReadIndirectBuffer(indirectBuffer);
            for (int index = 0; index < drawCount && index * commandStride + 20 <= commands.Length; index++)
            {
                ReadOnlySpan<byte> command = commands.AsSpan(index * commandStride, 20);
                DrawIndexed(
                    checked((int)MemoryMarshal.Read<uint>(command[0..4])),
                    checked((int)MemoryMarshal.Read<uint>(command[4..8])),
                    checked((int)MemoryMarshal.Read<uint>(command[8..12])),
                    MemoryMarshal.Read<int>(command[12..16]),
                    checked((int)MemoryMarshal.Read<uint>(command[16..20])));
            }
        }
        public void DrawIndirect(BufferRange indirectBuffer)
        {
            byte[] data = ReadIndirectBuffer(indirectBuffer);
            if (data.Length < 16)
            {
                return;
            }

            ReadOnlySpan<byte> command = data;
            Draw(
                checked((int)MemoryMarshal.Read<uint>(command[0..4])),
                checked((int)MemoryMarshal.Read<uint>(command[4..8])),
                checked((int)MemoryMarshal.Read<uint>(command[8..12])),
                checked((int)MemoryMarshal.Read<uint>(command[12..16])));
        }
        public void DrawIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
        {
            byte[] countData = ReadIndirectBuffer(parameterBuffer);
            if (countData.Length < 4)
            {
                return;
            }

            int drawCount = Math.Min(checked((int)MemoryMarshal.Read<uint>(countData)), Math.Max(0, maxDrawCount));
            int commandStride = stride > 0 ? stride : 16;
            byte[] commands = ReadIndirectBuffer(indirectBuffer);
            for (int index = 0; index < drawCount && index * commandStride + 16 <= commands.Length; index++)
            {
                ReadOnlySpan<byte> command = commands.AsSpan(index * commandStride, 16);
                Draw(
                    checked((int)MemoryMarshal.Read<uint>(command[0..4])),
                    checked((int)MemoryMarshal.Read<uint>(command[4..8])),
                    checked((int)MemoryMarshal.Read<uint>(command[8..12])),
                    checked((int)MemoryMarshal.Read<uint>(command[12..16])));
            }
        }

        private void RefreshResourceArrays()
        {
            foreach (((ShaderStage stage, int binding) key, MetalTextureArray array) in _textureArrays)
            {
                for (int index = 0; index < array?.Textures.Count; index++)
                {
                    _textures.Remove((key.stage, key.binding + index));
                }

                if (array != null)
                {
                    for (int index = 0; index < array.Textures.Count; index++)
                    {
                        _textures[(key.stage, key.binding + index)] = array.Textures[index];
                    }
                }
            }

            foreach (((ShaderStage stage, int binding) key, MetalImageArray array) in _imageArrays)
            {
                for (int index = 0; index < array?.Images.Count; index++)
                {
                    _images.Remove((key.stage, key.binding + index));
                }

                if (array != null)
                {
                    for (int index = 0; index < array.Images.Count; index++)
                    {
                        _images[(key.stage, key.binding + index)] = array.Images[index];
                    }
                }
            }
        }

        private byte[] ReadIndirectBuffer(BufferRange range)
        {
            if (range.Handle == BufferHandle.Null || range.Size <= 0)
            {
                return Array.Empty<byte>();
            }

            using PinnedSpan<byte> data = _renderer.GetBufferData(range.Handle, range.Offset, range.Size);
            return data.Get().ToArray();
        }
        public void DrawTexture(ITexture texture, ISampler sampler, Extents2DF srcRegion, Extents2DF dstRegion)
        {
            if (texture is not MetalTexture source || sampler is not MetalSampler sourceSampler || _colorTarget == null)
            {
                return;
            }

            MetalProgram helperProgram = GetDrawTextureProgram(_colorTarget.Format);
            if (helperProgram == null || helperProgram.Pipeline == nint.Zero)
            {
                return;
            }

            if (_drawTextureParameters == BufferHandle.Null)
            {
                _drawTextureParameters = _renderer.CreateBuffer(48);
            }

            float[] parameters =
            [
                source.Width <= 0 ? 0f : srcRegion.X1 / source.Width,
                source.Width <= 0 ? 1f : srcRegion.X2 / source.Width,
                source.Height <= 0 ? 0f : srcRegion.Y1 / source.Height,
                source.Height <= 0 ? 1f : srcRegion.Y2 / source.Height,
                dstRegion.X1,
                dstRegion.Y1,
                dstRegion.X2,
                dstRegion.Y2,
                _colorTarget.Width,
                _colorTarget.Height,
            ];
            _renderer.SetBufferData(_drawTextureParameters, 0, MemoryMarshal.AsBytes(parameters.AsSpan()));

            MetalContext.EncodeTextureBlit(
                _renderer,
                helperProgram,
                _colorTarget,
                source,
                sourceSampler,
                new BufferRange(_drawTextureParameters, 0, 48),
                dstRegion);
        }

        private MetalProgram GetDrawTextureProgram(Format targetFormat)
        {
            if (_drawTexturePrograms.TryGetValue(targetFormat, out MetalProgram existing))
            {
                return existing;
            }

            const string vertex = """
                struct DrawTextureOutput
                {
                    float4 position : SV_Position;
                    float2 texcoord : TEXCOORD0;
                };

                cbuffer DrawTextureParameters : register(b0)
                {
                    float4 sourceRect;
                    float4 destinationRect;
                    float2 targetSize;
                };

                DrawTextureOutput main(uint vertexId : SV_VertexID)
                {
                    DrawTextureOutput output;
                    float2 position;
                    float2 texcoord;
                    // Two CCW triangles. Using a triangle list avoids relying on
                    // backend-specific strip winding when the destination is flipped.
                    if (vertexId == 0)
                    {
                        position = destinationRect.xy;
                        texcoord = sourceRect.xy;
                    }
                    else if (vertexId == 1)
                    {
                        position = float2(destinationRect.z, destinationRect.y);
                        texcoord = float2(sourceRect.z, sourceRect.y);
                    }
                    else if (vertexId == 2 || vertexId == 3)
                    {
                        position = float2(destinationRect.x, destinationRect.w);
                        texcoord = float2(sourceRect.x, sourceRect.w);
                    }
                    else if (vertexId == 4)
                    {
                        position = float2(destinationRect.z, destinationRect.y);
                        texcoord = float2(sourceRect.z, sourceRect.y);
                    }
                    else
                    {
                        position = destinationRect.zw;
                        texcoord = sourceRect.zw;
                    }

                    output.position = float4(
                        position.x / targetSize.x * 2.0 - 1.0,
                        1.0 - position.y / targetSize.y * 2.0,
                        0.0,
                        1.0);
                    output.texcoord = texcoord;
                    return output;
                }
                """;
            const string fragment = """
                Texture2D sourceTexture : register(t0);
                SamplerState sourceSampler : register(s0);

                float4 main(float2 texcoord : TEXCOORD0) : SV_Target
                {
                    return sourceTexture.Sample(sourceSampler, texcoord);
                }
                """;

            ProgramPipelineState state = new();
            state.AttachmentEnable[0] = true;
            state.AttachmentFormats[0] = targetFormat;
            state.ColorWriteMask[0] = 0xF;
            state.BlendDescriptors[0] = new BlendDescriptor(
                false,
                default,
                BlendOp.Add,
                BlendFactor.One,
                BlendFactor.Zero,
                BlendOp.Add,
                BlendFactor.One,
                BlendFactor.Zero);

            MetalProgram program = _renderer.CreateProgram(
                [
                    new ShaderSource(vertex, ShaderStage.Vertex, TargetLanguage.Slang),
                    new ShaderSource(fragment, ShaderStage.Fragment, TargetLanguage.Slang),
                ],
                new ShaderInfo(0, default, state)) as MetalProgram;
            if (program?.CheckProgramLink(true) != ProgramLinkStatus.Success)
            {
                program?.Dispose();
                return null;
            }

            _drawTexturePrograms[targetFormat] = program;
            return program;
        }
        public void EndTransformFeedback() { }
        public void SetAlphaTest(bool enable, float reference, CompareOp op)
        {
            _alphaTestEnable = enable;
            _alphaTestReference = reference;
            _alphaTestOp = op;
        }
        public void SetBlendState(AdvancedBlendDescriptor blend)
        {
            // Metal's fixed-function blend state cannot represent GAL advanced blend
            // equations directly. Keep the state visible to capture and force the
            // conservative disabled/fixed path rather than silently retaining stale state.
            _blendSet = true;
            _blendDescriptor = new BlendDescriptor(
                false,
                default,
                BlendOp.Add,
                BlendFactor.One,
                BlendFactor.Zero,
                BlendOp.Add,
                BlendFactor.One,
                BlendFactor.Zero);
            _program?.SetBlendState(_blendDescriptor);
        }
        public void SetBlendState(int index, BlendDescriptor blend)
        {
            if (index == 0)
            {
                _blendDescriptor = blend;
                _blendSet = true;
                _program?.SetBlendState(blend);
            }
        }
        public void SetDepthBias(PolygonModeMask enables, float factor, float units, float clamp)
        {
            _depthBiasEnables = enables;
            _depthBiasFactor = factor;
            _depthBiasUnits = units;
            _depthBiasClamp = clamp;
        }
        public void SetDepthClamp(bool clamp) { _depthClamp = clamp; }
        public void SetDepthMode(DepthMode mode) { }
        public void SetDepthTest(DepthTestDescriptor depthTest) { _depthTest = depthTest; }
        public void SetFaceCulling(bool enable, Face face)
        {
            _cullEnable = enable;
            _cullMode = face;
        }
        public void SetFrontFace(FrontFace frontFace) { _frontFace = frontFace; }
        public void SetIndexBuffer(BufferRange buffer, IndexType type)
        {
            _indexBuffer = buffer;
            _indexType = type;
        }
        public void SetImage(ShaderStage stage, int binding, ITexture texture)
        {
            _images[(stage, binding)] = texture as MetalTexture;
        }
        public void SetImageArray(ShaderStage stage, int binding, IImageArray array)
        {
            _imageArrays[(stage, binding)] = array as MetalImageArray;
            RefreshResourceArrays();
        }
        public void SetImageArraySeparate(ShaderStage stage, int setIndex, IImageArray array)
        {
            _separateImageArrays[(stage, setIndex)] = array as MetalImageArray;
            SetImageArray(stage, setIndex, array);
        }
        public void SetLineParameters(float width, bool smooth)
        {
            _lineWidth = Math.Max(1f, width);
            _lineSmooth = smooth;
        }
        public void SetLogicOpState(bool enable, LogicalOp op)
        {
            _logicOpEnable = enable;
            _logicOpSet = true;
            _logicOp = op;
            _program?.SetLogicOpState(enable, op);
        }
        public void SetMultisampleState(MultisampleDescriptor multisample)
        {
            _multisample = multisample;
            _multisampleSet = true;
            _program?.SetMultisampleState(multisample);
        }
        public void SetPatchParameters(int vertices, ReadOnlySpan<float> defaultOuterLevel, ReadOnlySpan<float> defaultInnerLevel)
        {
            _patchVertices = Math.Max(0, vertices);
            _patchOuterLevels = defaultOuterLevel.ToArray();
            _patchInnerLevels = defaultInnerLevel.ToArray();
        }
        public void SetPointParameters(float size, bool isProgramPointSize, bool enablePointSprite, Origin origin)
        {
            _pointSize = Math.Max(1f, size);
        }
        public void SetPolygonMode(PolygonMode frontMode, PolygonMode backMode)
        {
            _polygonFront = frontMode;
            _polygonBack = backMode;
        }
        public void SetPrimitiveRestart(bool enable, int index)
        {
            _primitiveRestart = enable;
            _primitiveRestartIndex = index;
        }
        public void SetPrimitiveTopology(PrimitiveTopology topology) { _topology = topology; }
        public void SetProgram(IProgram program)
        {
            _program = program as MetalProgram;
            ApplyDynamicProgramState();
            _program?.SetVertexDescriptor(_vertexAttribs, _vertexBuffers);
        }

        private void ApplyDynamicProgramState()
        {
            if (_blendSet)
            {
                _program?.SetBlendState(_blendDescriptor);
            }
            if (_colorWriteMaskSet)
            {
                _program?.SetColorWriteMask(_colorWriteMask);
            }
            if (_multisampleSet)
            {
                _program?.SetMultisampleState(_multisample);
            }
            if (_logicOpSet)
            {
                _program?.SetLogicOpState(_logicOpEnable, _logicOp);
            }
        }
        public void SetRasterizerDiscard(bool discard) { _rasterizerDiscard = discard; }
        public void SetRenderTargetColorMasks(ReadOnlySpan<uint> componentMask)
        {
            if (componentMask.Length > 0)
            {
                _colorWriteMask = componentMask[0];
                _colorWriteMaskSet = true;
                _program?.SetColorWriteMask(_colorWriteMask);
            }
        }
        public void SetRenderTargets(Span<ITexture> colors, ITexture depthStencil)
        {
            _colorTarget = colors.Length > 0 ? colors[0] as MetalTexture : null;
            _depthTarget = depthStencil as MetalTexture;

            if (_colorTarget != null)
            {
                _width = _colorTarget.Width;
                _height = _colorTarget.Height;
            }
            else if (depthStencil != null)
            {
                _width = depthStencil.Width;
                _height = depthStencil.Height;
            }
        }
        public void SetScissors(ReadOnlySpan<Rectangle<int>> regions) { _scissors = regions.ToArray(); }
        public void SetStencilTest(StencilTestDescriptor stencilTest)
        {
            _stencilTest = stencilTest;
            // The descriptor is consumed when each encoder is created; keeping it in
            // pipeline state ensures later draws cannot inherit an older stencil test.
        }
        public void SetStorageBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _storageBuffers.Clear();
            foreach (BufferAssignment buffer in buffers)
            {
                _storageBuffers[buffer.Binding] = buffer.Range;
            }
        }
        public void SetTextureAndSampler(ShaderStage stage, int binding, ITexture texture, ISampler sampler)
        {
            _textures[(stage, binding)] = (texture as MetalTexture, sampler as MetalSampler);
        }
        public void SetTextureArray(ShaderStage stage, int binding, ITextureArray array)
        {
            _textureArrays[(stage, binding)] = array as MetalTextureArray;
            RefreshResourceArrays();
        }
        public void SetTextureArraySeparate(ShaderStage stage, int setIndex, ITextureArray array)
        {
            _separateTextureArrays[(stage, setIndex)] = array as MetalTextureArray;
            SetTextureArray(stage, setIndex, array);
        }
        public void SetTransformFeedbackBuffers(ReadOnlySpan<BufferRange> buffers)
        {
            // MetalPipeline does not currently expose transform-feedback emulation;
            // retain the ranges in the existing storage-buffer state so resource lifetime
            // and capture are not silently discarded.
            _storageBuffers.Clear();
            for (int index = 0; index < buffers.Length; index++)
            {
                _storageBuffers[index] = buffers[index];
            }
        }
        public void SetUniformBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            foreach (BufferAssignment buffer in buffers)
            {
                if (buffer.Range.Handle != BufferHandle.Null && buffer.Range.Size > 0)
                {
                    _uniformBuffers[buffer.Binding] = buffer.Range;
                }
                else
                {
                    _uniformBuffers.Remove(buffer.Binding);
                }
            }
        }
        public void SetUserClipDistance(int index, bool enableClip)
        {
            if (index < 0)
            {
                return;
            }

            if (index >= _userClipDistances.Length)
            {
                Array.Resize(ref _userClipDistances, index + 1);
            }
            _userClipDistances[index] = enableClip;
        }
        public void SetVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
        {
            _vertexAttribs = vertexAttribs.ToArray();
            _program?.SetVertexDescriptor(_vertexAttribs, _vertexBuffers);
        }
        public void SetVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
        {
            _vertexBuffers = vertexBuffers.ToArray();
            _program?.SetVertexDescriptor(_vertexAttribs, _vertexBuffers);
        }
        public void SetViewports(ReadOnlySpan<Viewport> viewports)
        {
            _viewports = viewports.ToArray();
            if (viewports.Length > 0) { _width = (int)viewports[0].Region.Width; _height = (int)viewports[0].Region.Height; }
        }
        public void TextureBarrier() => MetalContext.WaitForIdle();
        public void TextureBarrierTiled() => MetalContext.WaitForIdle();
        public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual) => false;
        public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual) => false;
        public void EndHostConditionalRendering() { }

        internal void Dispose()
        {
            foreach (MetalProgram program in _drawTexturePrograms.Values)
            {
                program.Dispose();
            }
            _drawTexturePrograms.Clear();

            foreach (MetalProgram program in _clearPrograms.Values)
            {
                program.Dispose();
            }
            _clearPrograms.Clear();

            if (_drawTextureParameters != BufferHandle.Null)
            {
                _renderer.DeleteBuffer(_drawTextureParameters);
                _drawTextureParameters = BufferHandle.Null;
            }

            if (_clearParameters != BufferHandle.Null)
            {
                _renderer.DeleteBuffer(_clearParameters);
                _clearParameters = BufferHandle.Null;
            }
        }
    }
}
