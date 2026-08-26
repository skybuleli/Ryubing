using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ryujinx.Graphics.Metal
{
    class MetalProgram : IProgram
    {
        private readonly ShaderSource[] _shaders;
        private readonly ShaderInfo _info;
        private readonly byte[] _binary;
        private readonly MetalCompiledShader[] _compiledShaders;
        private readonly Dictionary<(ShaderStage Stage, ResourceType Type, int Binding), int> _argumentLocations = new();
        private readonly Dictionary<ShaderStage, int> _argumentBufferSizes = new();
        private readonly List<nint> _libraries = new();
        private nint _pipeline;
        private ProgramLinkStatus _linkStatus = ProgramLinkStatus.Incomplete;
        private readonly MTLSize _threadsPerThreadgroup;
        private VertexAttribDescriptor[] _currentVertexAttribs = Array.Empty<VertexAttribDescriptor>();
        private VertexBufferDescriptor[] _currentVertexBuffers = Array.Empty<VertexBufferDescriptor>();
        private BlendDescriptor? _blendOverride;
        private uint? _colorWriteMaskOverride;
        private MultisampleDescriptor? _multisampleOverride;
        private bool? _logicOperationEnabledOverride;
        private LogicalOp _logicOperationOverride = LogicalOp.Copy;

        // PSO 缓存：混合/写掩码/多重采样/逻辑运算/顶点布局的每种组合只创建一次
        // MTLRenderPipelineState。游戏每帧切换混合状态数百次，无缓存时会反复重建。
        private readonly Dictionary<long, nint> _psoCache = new();
        private long _lastPsoKey = long.MinValue;

        public nint Pipeline => _pipeline;
        internal MTLSize ThreadsPerThreadgroup => _threadsPerThreadgroup;
        internal IReadOnlyList<ShaderSource> Sources => _shaders;
        internal IReadOnlyList<MetalCompiledShader> CompiledShaders => _compiledShaders;
        internal int GetArgumentBufferSize(ShaderStage stage) => _argumentBufferSizes.TryGetValue(stage, out int size) ? size : 0;
        internal bool TryGetArgumentLocation(ShaderStage stage, ResourceType type, int binding, out int offset) =>
            _argumentLocations.TryGetValue((stage, type, binding), out offset);
        public bool IsCompute => _shaders.Length == 1 && _shaders[0].Stage == ShaderStage.Compute;

        internal bool UsesResource(ResourceType type, ShaderStage stage, int binding)
        {
            ReadOnlyCollection<ResourceDescriptorCollection> sets = _info.ResourceLayout.Sets;
            if (sets == null)
            {
                // A default ShaderInfo is used by isolated tests and by legacy callers. Keep
                // the old permissive behavior when no layout was supplied.
                return true;
            }

            ResourceStages stageFlag = stage switch
            {
                ShaderStage.Compute => ResourceStages.Compute,
                ShaderStage.Vertex => ResourceStages.Vertex,
                ShaderStage.TessellationControl => ResourceStages.TessellationControl,
                ShaderStage.TessellationEvaluation => ResourceStages.TessellationEvaluation,
                ShaderStage.Geometry => ResourceStages.Geometry,
                ShaderStage.Fragment => ResourceStages.Fragment,
                _ => ResourceStages.None,
            };

            foreach (ResourceDescriptorCollection set in sets)
            {
                if (set.Descriptors == null)
                {
                    continue;
                }

                foreach (ResourceDescriptor descriptor in set.Descriptors)
                {
                    if (descriptor.Binding == binding && (descriptor.Stages & stageFlag) != 0 && ResourceTypeMatches(type, descriptor.Type))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ResourceTypeMatches(ResourceType requested, ResourceType declared)
        {
            return requested == declared ||
                   requested == ResourceType.Texture && declared == ResourceType.TextureAndSampler ||
                   requested == ResourceType.TextureAndSampler && declared == ResourceType.Texture ||
                   requested == ResourceType.Image && declared == ResourceType.BufferImage ||
                   requested == ResourceType.BufferImage && declared == ResourceType.Image;
        }

        public MetalProgram(ShaderSource[] shaders, ShaderInfo info, IReadOnlyList<MetalCompiledShader> compiledShaders = null)
        {
            _shaders = shaders;
            _info = info;
            _compiledShaders = compiledShaders?.ToArray() ?? Array.Empty<MetalCompiledShader>();
            _binary = _compiledShaders.Length == 1 ? _compiledShaders[0]?.Metallib : null;
            _threadsPerThreadgroup = GetThreadsPerThreadgroup(shaders);

            for (int index = 0; index < _shaders.Length && index < _compiledShaders.Length; index++)
            {
                if (_compiledShaders[index] != null)
                {
                    string dumpPath = Environment.GetEnvironmentVariable("RYUJINX_METAL_SHADER_DUMP_PATH");
                    if (!string.IsNullOrWhiteSpace(dumpPath) && !string.IsNullOrWhiteSpace(_compiledShaders[index].ReflectionJson))
                    {
                        Directory.CreateDirectory(dumpPath);
                        File.WriteAllText(Path.Combine(dumpPath, $"program-{_shaders[index].Stage}-{Guid.NewGuid():N}.reflection.json"), _compiledShaders[index].ReflectionJson);
                    }
                    ReadReflection(_shaders[index].Stage, _compiledShaders[index].ReflectionJson);
                }
            }

            if (compiledShaders == null || compiledShaders.Count == 0)
            {
                _linkStatus = ProgramLinkStatus.Failure;
                return;
            }

            nint pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                foreach (MetalCompiledShader compiledShader in compiledShaders)
                {
                    nint library = MetalPipelineState.CreateLibrary(MetalContext.Device, compiledShader?.Metallib);
                    if (library == nint.Zero)
                    {
                        _linkStatus = ProgramLinkStatus.Failure;
                        return;
                    }

                    _libraries.Add(library);
                }

                if (IsCompute)
                {
                    _pipeline = MetalPipelineState.CreateComputePipeline(MetalContext.Device, _libraries[0]);
                }
                else
                {
                    // 走缓存化重建路径：初始 PSO 同样进入缓存，后续状态切换直接复用。
                    RebuildRenderPipeline();
                }

                _linkStatus = _pipeline != nint.Zero ? ProgramLinkStatus.Success : ProgramLinkStatus.Failure;
                Console.WriteLine($"[MetalProgram] PSO 状态={_linkStatus} pipeline=0x{_pipeline:X} shaders={shaders.Length}");
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        private void ReadReflection(ShaderStage stage, string reflectionJson)
        {
            if (string.IsNullOrWhiteSpace(reflectionJson))
            {
                return;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(reflectionJson);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("TopLevelArgumentBuffer", out JsonElement argumentBuffer))
                {
                    Console.WriteLine($"[MetalProgram][AB] reflection lacks TopLevelArgumentBuffer: stage={stage}");
                    return;
                }

                foreach (JsonElement location in argumentBuffer.EnumerateArray())
                {
                    if (!location.TryGetProperty("Slot", out JsonElement slotElement) ||
                        !location.TryGetProperty("Type", out JsonElement typeElement) ||
                        !location.TryGetProperty("EltOffset", out JsonElement offsetElement))
                    {
                        continue;
                    }

                    int binding = slotElement.GetInt32();
                    int offset = offsetElement.GetInt32();
                    string type = typeElement.GetString();
                    ResourceType[] resourceTypes = type switch
                    {
                        "CBV" => [ResourceType.UniformBuffer],
                        // Automatic MSC reflection reports both typed and structured read-only
                        // resources as SRV. Keep both aliases; the GAL resource map disambiguates
                        // which one is bound at a given slot.
                        "SRV" => [ResourceType.Texture, ResourceType.StorageBuffer],
                        "SMP" or "Sampler" => [ResourceType.Sampler],
                        "UAV" => [ResourceType.Image, ResourceType.StorageBuffer],
                        _ => Array.Empty<ResourceType>(),
                    };

                    foreach (ResourceType resourceType in resourceTypes)
                    {
                        _argumentLocations[(stage, resourceType, binding)] = offset;
                    }

                    _argumentBufferSizes[stage] = Math.Max(_argumentBufferSizes.GetValueOrDefault(stage), offset + 24);
                    Console.WriteLine($"[MetalProgram][AB] stage={stage} binding={binding} type={type} offset={offset}");
                }
            }
            catch (JsonException exception)
            {
                Console.WriteLine($"[MetalProgram] MSC reflection 解析失败 stage={stage}: {exception.Message}");
            }
        }

        private static MTLSize GetThreadsPerThreadgroup(ShaderSource[] shaders)
        {
            if (shaders.Length == 1 && shaders[0].Code != null)
            {
                Match match = Regex.Match(shaders[0].Code, @"\[numthreads\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)\]");
                if (match.Success)
                {
                    return new MTLSize(ulong.Parse(match.Groups[1].Value), ulong.Parse(match.Groups[2].Value), ulong.Parse(match.Groups[3].Value));
                }
            }

            return new MTLSize(1, 1, 1);
        }

        private int FindStage(ShaderStage stage)
        {
            for (int index = 0; index < _shaders.Length; index++)
            {
                if (_shaders[index].Stage == stage)
                {
                    return index < _libraries.Count ? index : -1;
                }
            }

            return -1;
        }

        internal void SetVertexDescriptor(VertexAttribDescriptor[] attributes, VertexBufferDescriptor[] buffers)
        {
            _currentVertexAttribs = attributes ?? Array.Empty<VertexAttribDescriptor>();
            _currentVertexBuffers = buffers ?? Array.Empty<VertexBufferDescriptor>();
            RebuildRenderPipeline();
        }

        internal void SetBlendState(BlendDescriptor blend)
        {
            _blendOverride = blend;
            RebuildRenderPipeline();
        }

        internal void SetColorWriteMask(uint mask)
        {
            _colorWriteMaskOverride = mask;
            RebuildRenderPipeline();
        }

        internal void SetMultisampleState(MultisampleDescriptor multisample)
        {
            _multisampleOverride = multisample;
            RebuildRenderPipeline();
        }

        internal void SetLogicOpState(bool enable, LogicalOp op)
        {
            _logicOperationEnabledOverride = enable;
            _logicOperationOverride = op;
            RebuildRenderPipeline();
        }

        /// <summary>
        /// 计算当前 PSO 状态签名的稳定哈希：混合状态、写掩码、多重采样、逻辑运算与
        /// 顶点布局（属性位置/格式/偏移 + 缓冲步长/分频）。签名不变则直接复用缓存。
        /// </summary>
        private long ComputePsoKey(
            VertexAttribDescriptor[] attribs,
            VertexBufferDescriptor[] buffers)
        {
            long hash = 1469598103934665603L;
            void Mix(ulong value)
            {
                hash = unchecked((long)((ulong)hash ^ value) * 1099511628211L);
            }

            BlendDescriptor blend = _blendOverride ?? (_info.State.HasValue ? _info.State.Value.BlendDescriptors[0] : default);
            Mix(blend.Enable ? 1UL : 0UL);
            Mix((ulong)blend.ColorOp << 32 ^ (ulong)blend.AlphaOp);
            Mix((ulong)blend.ColorSrcFactor << 32 ^ (ulong)blend.ColorDstFactor);
            Mix((ulong)blend.AlphaSrcFactor << 32 ^ (ulong)blend.AlphaDstFactor);

            uint writeMask = _colorWriteMaskOverride ?? (_info.State.HasValue ? _info.State.Value.ColorWriteMask[0] : 0xF);
            Mix(writeMask);

            bool multisampleEnabled = _multisampleOverride.HasValue || (_info.State is { SamplesCount: > 1 });
            Mix(multisampleEnabled ? 1UL : 0UL);
            if (_multisampleOverride.HasValue)
            {
                Mix(_multisampleOverride.Value.AlphaToCoverageEnable ? 1UL << 1 : 0UL);
                Mix(_multisampleOverride.Value.AlphaToOneEnable ? 1UL << 2 : 0UL);
            }

            bool logicEnabled = _logicOperationEnabledOverride ?? (_info.State?.LogicOpEnable ?? false);
            Mix(logicEnabled ? 1UL : 0UL);
            if (logicEnabled)
            {
                Mix((ulong)(_logicOperationEnabledOverride.HasValue ? _logicOperationOverride : (_info.State?.LogicOp ?? LogicalOp.Copy)));
            }

            Mix((ulong)attribs.Length << 32 ^ (uint)buffers.Length);
            foreach (VertexAttribDescriptor attribute in attribs)
            {
                Mix((ulong)attribute.BufferIndex << 48 ^ (ulong)attribute.Offset << 24 ^ (ulong)(attribute.IsZero ? 1UL : 0UL) << 20 ^ (ulong)attribute.Format);
            }

            foreach (VertexBufferDescriptor buffer in buffers)
            {
                // 仅步长与分频影响 PSO；Buffer 句柄/偏移是绘制期绑定，不参与。
                Mix((ulong)Math.Max(0, buffer.Stride) << 32 ^ (ulong)Math.Max(0, buffer.Divisor));
            }

            return hash;
        }

        private void RebuildRenderPipeline()
        {
            if (IsCompute || _libraries.Count == 0)
            {
                return;
            }

            int vertexIndex = FindStage(ShaderStage.Vertex);
            int fragmentIndex = FindStage(ShaderStage.Fragment);
            if (vertexIndex < 0 || fragmentIndex < 0)
            {
                return;
            }

            VertexAttribDescriptor[] attribs = _currentVertexAttribs;
            VertexBufferDescriptor[] buffers = _currentVertexBuffers;
            if (attribs.Length == 0 && _info.State.HasValue)
            {
                ProgramPipelineState state = _info.State.Value;
                int attributeCount = Math.Clamp(state.VertexAttribCount, 0, state.VertexAttribs.AsSpan().Length);
                int bufferCount = Math.Clamp(state.VertexBufferCount, 0, state.VertexBuffers.AsSpan().Length);
                if (attributeCount > 0 && bufferCount > 0)
                {
                    attribs = state.VertexAttribs.AsSpan()[..attributeCount].ToArray();
                    buffers = new VertexBufferDescriptor[bufferCount];
                    for (int index = 0; index < bufferCount; index++)
                    {
                        BufferPipelineDescriptor buffer = state.VertexBuffers[index];
                        buffers[index] = new VertexBufferDescriptor(BufferRange.Empty, buffer.Stride, buffer.Divisor);
                    }
                }
            }

            long key = ComputePsoKey(attribs, buffers);
            if (key == _lastPsoKey && _pipeline != nint.Zero)
            {
                return;
            }

            if (_psoCache.TryGetValue(key, out nint cached))
            {
                _pipeline = cached;
                _lastPsoKey = key;
                return;
            }

            nint pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                nint vertexDescriptor = MetalPipelineState.CreateVertexDescriptor(attribs, buffers);
                nint pipeline = MetalPipelineState.CreateRenderPipeline(
                    MetalContext.Device,
                    _libraries[vertexIndex],
                    _libraries[fragmentIndex],
                    vertexDescriptor,
                    pipelineState: _info.State,
                    blendOverride: _blendOverride,
                    colorWriteMaskOverride: _colorWriteMaskOverride,
                    multisampleOverride: _multisampleOverride,
                    logicOperationEnabledOverride: _logicOperationEnabledOverride,
                    logicOperationOverride: _logicOperationOverride);

                if (vertexDescriptor != nint.Zero)
                {
                    MetalNative.SendVoid(vertexDescriptor, MetalNative.Sel("release"));
                }

                if (pipeline != nint.Zero)
                {
                    _psoCache[key] = pipeline;
                    _pipeline = pipeline;
                    _lastPsoKey = key;
                    _linkStatus = ProgramLinkStatus.Success;
                }
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        public ProgramLinkStatus CheckProgramLink(bool blocking) => _linkStatus;

        public byte[] GetBinary() => _binary ?? Array.Empty<byte>();

        public void Dispose()
        {
            nint pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                // 缓存持有全部 PSO 的所有权，统一释放（_pipeline 必在缓存中）。
                foreach (nint pipeline in _psoCache.Values)
                {
                    MetalNative.SendVoid(pipeline, MetalNative.Sel("release"));
                }

                _psoCache.Clear();
                _pipeline = nint.Zero;
                _lastPsoKey = long.MinValue;

                foreach (nint library in _libraries)
                {
                    MetalNative.SendVoid(library, MetalNative.Sel("release"));
                }

                _libraries.Clear();
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }
    }
}
