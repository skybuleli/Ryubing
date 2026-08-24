using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Metal
{
    public sealed class MetalRenderer : IRenderer
    {
        private readonly MetalPipeline _pipeline;
        private readonly MetalWindow _window;
        private ulong _nextHandle = 1;
        private readonly Dictionary<BufferHandle, byte[]> _buffers = new();
        private readonly Dictionary<ulong, byte[]> _syncs = new();
        private ulong _currentSync;
        private uint _clearColor = 0xFF3366CC; // 默认清色
        private int _frameWidth = 1280, _frameHeight = 720;

        public MetalRenderer()
        {
            _pipeline = new MetalPipeline();
            _window = new MetalWindow();
        }

        public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;

        public bool PreferThreading => false;
        public IPipeline Pipeline => _pipeline;
        public IWindow Window => _window;
        public uint ProgramCount => 0;

        public void BackgroundContextAction(Action action, bool alwaysBackground = false) => action();

        public BufferHandle CreateBuffer(int size, BufferAccess access = BufferAccess.Default)
        {
            var handle = NewHandle();
            _buffers[handle] = new byte[size];
            return handle;
        }

        public BufferHandle CreateBuffer(nint pointer, int size)
        {
            var handle = NewHandle();
            var data = new byte[size];
            unsafe { new Span<byte>((void*)pointer, size).CopyTo(data); }
            _buffers[handle] = data;
            return handle;
        }

        public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            // P1-1 存根：按首个 range 大小分配
            int size = 0;
            foreach (var r in storageBuffers) size = Math.Max(size, r.Size);
            return CreateBuffer(size);
        }

        public IImageArray CreateImageArray(int size, bool isBuffer) => new MetalImageArray();
        public ITextureArray CreateTextureArray(int size, bool isBuffer) => new MetalTextureArray();

        public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
        {
            // P1-4: 三级缓存 + 库化: 内存 -> 磁盘 -> 编译
            var metallibs = new List<byte[]>();
            foreach (var s in shaders)
            {
                try
                {
                    string code = s.Code ?? "";
                    string stageStr = s.Stage.ToString();
                    string hash = MetalDiskCache.GetHash(code, stageStr);

                    // 1. 内存库化
                    if (MetalLibraryCache.TryGet(hash, out var cachedLib))
                    {
                        MetalLibraryCache.RecordHit();
                        metallibs.Add(cachedLib);
                        continue;
                    }
                    // 2. 磁盘
                    if (MetalDiskCache.TryGet(hash, out var diskLib))
                    {
                        MetalLibraryCache.Add(hash, diskLib);
                        MetalLibraryCache.RecordHit();
                        metallibs.Add(diskLib);
                        continue;
                    }
                    MetalLibraryCache.RecordMiss();

                    // 3. 编译
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    byte[] dxil = null;
                    if (s.Language == TargetLanguage.Slang && !string.IsNullOrEmpty(s.Code))
                    {
                        dxil = SlangCompiler.Compile(s.Code, s.Stage);
                    }
                    else if (s.BinaryCode != null && s.BinaryCode.Length > 0)
                    {
                        dxil = s.BinaryCode;
                    }
                    if (dxil != null)
                    {
                        byte[] metallib = MscConverter.Convert(dxil);
                        sw.Stop();
                        MetalDiskCache.Save(hash, code, dxil, metallib, stageStr, sw.ElapsedMilliseconds);
                        MetalLibraryCache.Add(hash, metallib);
                        metallibs.Add(metallib);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Metal] CreateProgram 失败 stage={s.Stage} lang={s.Language}: {ex.Message}");
                }
            }
            byte[] combined = metallibs.Count == 0 ? null : metallibs.Count == 1 ? metallibs[0] : CombineMetallibs(metallibs);
            return new MetalProgram(shaders, info, combined);
        }

        private static byte[] CombineMetallibs(List<byte[]> libs)
        {
            int total = libs.Sum(b => b.Length);
            var outBytes = new byte[total];
            int offset = 0;
            foreach (var b in libs) { Buffer.BlockCopy(b, 0, outBytes, offset, b.Length); offset += b.Length; }
            return outBytes;
        }

        public ISampler CreateSampler(SamplerCreateInfo info) => new MetalSampler();
        public ITexture CreateTexture(TextureCreateInfo info) => new MetalTexture(info);

        public bool PrepareHostMapping(nint address, ulong size) => false;
        public void CreateSync(ulong id, bool strict) => _syncs[id] = Array.Empty<byte>();
        public void DeleteBuffer(BufferHandle buffer) => _buffers.Remove(buffer);

        public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
        {
            if (_buffers.TryGetValue(buffer, out var data))
            {
                return PinnedSpan<byte>.UnsafeFromSpan(data.AsSpan(offset, size));
            }
            return new PinnedSpan<byte>();
        }

        public Capabilities GetCapabilities()
        {
            // UMA 设备，Metal 原生能力。对标 VulkanRenderer 但简化为固定集。
            return new Capabilities(
                api: TargetApi.Metal,
                vendorName: "Apple",
                memoryType: SystemMemoryType.UnifiedMemory,
                hasFrontFacingBug: false,
                hasVectorIndexingBug: false,
                needsFragmentOutputSpecialization: false,
                reduceShaderPrecision: false,
                supportsAstcCompression: true,
                supportsBc123Compression: true,
                supportsBc45Compression: true,
                supportsBc67Compression: true,
                supportsEtc2Compression: true,
                supports3DTextureCompression: true,
                supportsBgraFormat: true,
                supportsR4G4Format: false,
                supportsR4G4B4A4Format: false,
                supportsScaledVertexFormats: true,
                supportsSnormBufferTextureFormat: true,
                supports5BitComponentFormat: true,
                supportsSparseBuffer: false,
                supportsBlendEquationAdvanced: false,
                supportsFragmentShaderInterlock: false,
                supportsFragmentShaderOrderingIntel: false,
                supportsGeometryShader: false,
                supportsGeometryShaderPassthrough: false,
                supportsTransformFeedback: false,
                supportsImageLoadFormatted: true,
                supportsLayerVertexTessellation: true,
                supportsMismatchingViewFormat: true,
                supportsCubemapView: true,
                supportsNonConstantTextureOffset: true,
                supportsQuads: false,
                supportsSeparateSampler: true,
                supportsShaderBallot: false, // Wave->SIMD 待验证，先禁以触发标量降级
                supportsShaderBarrierDivergence: true,
                supportsShaderFloat64: false,
                supportsShaderNonUniformIndexing: true,
                supportsTextureGatherOffsets: true,
                supportsTextureShadowLod: true,
                supportsVertexStoreAndAtomics: true,
                supportsViewportIndexVertexTessellation: true,
                supportsViewportMask: true,
                supportsViewportSwizzle: true,
                supportsIndirectParameters: true,
                supportsDepthClipControl: true,
                uniformBufferSetIndex: 0,
                storageBufferSetIndex: 1,
                textureSetIndex: 2,
                imageSetIndex: 3,
                extraSetBaseIndex: 4,
                maximumExtraSets: 4,
                maximumUniformBuffersPerStage: 18,
                maximumStorageBuffersPerStage: 8,
                maximumTexturesPerStage: 32,
                maximumImagesPerStage: 8,
                maximumComputeSharedMemorySize: 32768,
                maximumSupportedAnisotropy: 16f,
                shaderSubgroupSize: 32,
                storageBufferOffsetAlignment: 256,
                textureBufferOffsetAlignment: 256,
                gatherBiasPrecision: 8,
                maximumGpuMemory: 8UL * 1024 * 1024 * 1024);
        }

        public ulong GetCurrentSync() => _currentSync;
        public HardwareInfo GetHardwareInfo() => new("Apple", "Apple M1", "Metal 3.2");

        public IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info)
        {
            // P1-4 缓存回放：programBinary 为 metallib
            return new MetalProgram(Array.Empty<ShaderSource>(), info);
        }

        public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
        {
            if (_buffers.TryGetValue(buffer, out var dst))
            {
                data.CopyTo(dst.AsSpan(offset));
            }
        }
        public void UpdateCounters() { }
        public void PreFrame() { }
        public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved) => new MetalCounterEvent();
        public void ResetCounter(CounterType type) { }
        public void WaitSync(ulong id) { }
        public void Initialize(GraphicsDebugLevel logLevel) { }
        public void SetInterruptAction(Action<Action> interruptAction) { }
        public void Screenshot()
        {
            var data = MetalContext.GetLastFrameData();
            var (w, h) = MetalContext.GetFrameSize();
            ScreenCaptured?.Invoke(this, new ScreenCaptureImageInfo(w, h, true, data, false, false));
        }
        public void Dispose() { }

        private BufferHandle NewHandle()
        {
            ulong h = _nextHandle++;
            return Unsafe.As<ulong, BufferHandle>(ref h);
        }
    }
}
