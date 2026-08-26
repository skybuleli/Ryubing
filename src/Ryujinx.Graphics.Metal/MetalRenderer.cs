using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal
{
    public sealed class MetalRenderer : IRenderer
    {
        private readonly MetalPipeline _pipeline;
        private readonly MetalWindow _window;
        private ulong _nextHandle = 1;
        private readonly Dictionary<BufferHandle, byte[]> _buffers = new();
        private readonly Dictionary<BufferHandle, nint> _nativeBuffers = new();
        private readonly Dictionary<ulong, byte[]> _syncs = new();
        private int _frameWidth = 1280, _frameHeight = 720;

        public MetalRenderer() : this(nint.Zero) { }

        /// <summary>
        /// metalLayer: UI 侧真实 CAMetalLayer 句柄（EmbeddedWindowMetal.GetMetalLayer）。
        /// nint.Zero 表示无窗口（headless），呈现退化为仅 CPU 帧数据。
        /// </summary>
        public MetalRenderer(nint metalLayer)
        {
            MetalContext.Initialize(metalLayer);
            MetalContext.NativeBufferResolver = GetNativeBuffer;
            _pipeline = new MetalPipeline(this);
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

            if (MetalContext.IsAvailable)
            {
                nint pool = MetalNative.objc_autoreleasePoolPush();
                try
                {
                    _nativeBuffers[handle] = MetalNative.SendObject(
                        MetalContext.Device,
                        MetalNative.Sel("newBufferWithLength:options:"),
                        (ulong)size,
                        0);
                }
                finally
                {
                    MetalNative.objc_autoreleasePoolPop(pool);
                }
            }

            return handle;
        }

        // UMA Shared 零拷：直接引用主机内存，不做 CopyTo
        private readonly Dictionary<BufferHandle, (nint ptr, int size)> _hostMapped = new();

        public unsafe BufferHandle CreateBuffer(nint pointer, int size)
        {
            var handle = NewHandle();
            // Shared 模式：记录指针，GetBufferData 时直返主机内存
            _hostMapped[handle] = (pointer, size);
            _buffers[handle] = Array.Empty<byte>();

            if (MetalContext.IsAvailable && pointer != nint.Zero && size > 0)
            {
                nint pool = MetalNative.objc_autoreleasePoolPush();
                try
                {
                    _nativeBuffers[handle] = MetalNative.SendObject(
                        MetalContext.Device,
                        MetalNative.Sel("newBufferWithBytes:length:options:"),
                        pointer,
                        (ulong)size,
                        0);
                }
                finally
                {
                    MetalNative.objc_autoreleasePoolPop(pool);
                }
            }

            return handle;
        }
        public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            // P1-1 存根：按首个 range 大小分配
            int size = 0;
            foreach (var r in storageBuffers) size = Math.Max(size, r.Size);
            return CreateBuffer(size);
        }

        public IImageArray CreateImageArray(int size, bool isBuffer) => new MetalImageArray(size);
        public ITextureArray CreateTextureArray(int size, bool isBuffer) => new MetalTextureArray(size);

        public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
        {
            Console.WriteLine($"[Metal] CreateProgram 调用: shaders={shaders.Length} stages={string.Join(",", shaders.Select(s => s.Stage))}");
            // Each stage keeps its metallib and MSC reflection together. The reflection
            // describes the top-level argument-buffer offsets used by that stage.
            var compiledShaders = new List<MetalCompiledShader>(shaders.Length);

            foreach (var s in shaders)
            {
                MetalCompiledShader compiledShader = null;

                try
                {
                    string code = s.Code ?? "";
                    string stageStr = s.Stage.ToString();
                    string hash = MetalDiskCache.GetHash(code, stageStr);

                    if (MetalLibraryCache.TryGet(hash, out var cachedShader))
                    {
                        MetalLibraryCache.RecordHit();
                        compiledShader = cachedShader;
                    }
                    else if (MetalDiskCache.TryGet(hash, out var diskShader))
                    {
                        MetalLibraryCache.Add(hash, diskShader);
                        MetalLibraryCache.RecordHit();
                        compiledShader = diskShader;
                    }
                    else
                    {
                        MetalLibraryCache.RecordMiss();
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
                            compiledShader = MscConverter.Convert(dxil);
                            sw.Stop();
                            MetalDiskCache.Save(hash, code, dxil, compiledShader, stageStr, sw.ElapsedMilliseconds);
                            MetalLibraryCache.Add(hash, compiledShader);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Metal] CreateProgram 失败 stage={s.Stage} lang={s.Language}: {ex.Message}");
                }

                compiledShaders.Add(compiledShader);
            }

            return new MetalProgram(shaders, info, compiledShaders);
        }

        public ISampler CreateSampler(SamplerCreateInfo info) => new MetalSampler(info);
        public ITexture CreateTexture(TextureCreateInfo info) => new MetalTexture(info);

        public bool PrepareHostMapping(nint address, ulong size) => true; // UMA Shared 始终可主机映射
        public void CreateSync(ulong id, bool strict) => _syncs[id] = Array.Empty<byte>();
        public void DeleteBuffer(BufferHandle buffer)
        {
            if (_nativeBuffers.Remove(buffer, out nint nativeBuffer) && nativeBuffer != nint.Zero)
            {
                // 缓冲可能仍被未提交/执行中的命令缓冲引用，延迟到 FlushFrame 后释放。
                MetalContext.DeferBufferRelease(nativeBuffer);
            }

            _buffers.Remove(buffer);
            _hostMapped.Remove(buffer);
        }

        public unsafe PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
        {
            if (_hostMapped.TryGetValue(buffer, out var hm))
            {
                return new PinnedSpan<byte>((void*)(hm.ptr + offset), size, null);
            }

            if (_nativeBuffers.TryGetValue(buffer, out nint nativeBuffer) && nativeBuffer != nint.Zero)
            {
                nint contents = MetalNative.SendObject(nativeBuffer, MetalNative.Sel("contents"));
                if (contents != nint.Zero)
                {
                    byte[] result = new byte[size];
                    Marshal.Copy(contents + offset, result, 0, size);
                    return PinnedSpan<byte>.UnsafeFromSpan(result);
                }
            }

            if (_buffers.TryGetValue(buffer, out var data))
            {
                return PinnedSpan<byte>.UnsafeFromSpan(data.AsSpan(offset, size));
            }
            return new PinnedSpan<byte>();
        }

        internal nint GetNativeBuffer(BufferRange range)
        {
            return _nativeBuffers.TryGetValue(range.Handle, out nint buffer) ? buffer : nint.Zero;
        }

        internal byte[] GetBufferBytes(BufferRange range)
        {
            using PinnedSpan<byte> data = GetBufferData(range.Handle, range.Offset, range.Size);
            return data.Get().ToArray();
        }

        internal ulong GetBufferGpuAddress(BufferRange range)
        {
            nint buffer = GetNativeBuffer(range);
            return buffer == nint.Zero
                ? 0UL
                : MetalNative.SendULong(buffer, MetalNative.Sel("gpuAddress")) + (ulong)Math.Max(0, range.Offset);
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
                // Metal 的 B5G6R5/BGR5A1 分量序与 Vulkan 相反且无视图 swizzle，
                // 上报 false 让 Gpu 层在上传时转换为 RGBA8，避免颜色通道错乱。
                supports5BitComponentFormat: false,
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

        public ulong GetCurrentSync() => 0;
        public HardwareInfo GetHardwareInfo() => new("Apple", "Apple M1", "Metal 3.2");

        public IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info)
        {
            // P1-4 缓存回放：programBinary 为 metallib
            return new MetalProgram(Array.Empty<ShaderSource>(), info);
        }

        public unsafe void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
        {
            // 缓冲可能已被本帧未提交的命令引用：先提交避免 write-after-encode 危害。
            MetalContext.NotifyBufferWrite(buffer);

            if (_hostMapped.TryGetValue(buffer, out var hm))
            {
                fixed (byte* p = data) { Buffer.MemoryCopy(p, (void*)(hm.ptr + offset), hm.size - offset, data.Length); }
            }
            else if (_buffers.TryGetValue(buffer, out var dst))
            {
                data.CopyTo(dst.AsSpan(offset));
            }

            if (_nativeBuffers.TryGetValue(buffer, out nint nativeBuffer) && nativeBuffer != nint.Zero)
            {
                MetalContext.RecordBufferUpload();
                nint contents = MetalNative.SendObject(nativeBuffer, MetalNative.Sel("contents"));
                if (contents != nint.Zero)
                {
                    Marshal.Copy(data.ToArray(), 0, contents + offset, data.Length);
                }
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
            // 置位后由下一帧 present 读到真实 drawable 时回调，避免抓到纯 clear 色占位。
            MetalContext.RequestScreenshot((data, w, h) =>
            {
                ScreenCaptured?.Invoke(this, new ScreenCaptureImageInfo(w, h, true, data, false, false));
            });
        }
        public void Dispose()
        {
            _pipeline.Dispose();

            foreach (nint nativeBuffer in _nativeBuffers.Values)
            {
                if (nativeBuffer != nint.Zero)
                {
                    MetalNative.SendVoid(nativeBuffer, MetalNative.Sel("release"));
                }
            }

            _nativeBuffers.Clear();
            _buffers.Clear();
            _hostMapped.Clear();
        }

        private BufferHandle NewHandle()
        {
            ulong h = _nextHandle++;
            return Unsafe.As<ulong, BufferHandle>(ref h);
        }
    }
}
