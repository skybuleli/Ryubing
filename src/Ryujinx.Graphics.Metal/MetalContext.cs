using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Ryujinx.Graphics.Metal
{
    public static class MetalContext
    {
        private static nint _device;
        private static nint _commandQueue;
        private static nint _layer;
        private static bool _layerConfigured;
        private static readonly object _lock = new();
        private static byte[] _lastFrameData;
        private static Action<byte[], int, int> _screenshotCallback;
        private static int _width = 1280, _height = 720;
        private static uint _windowId;
        private static nint _nativeView;
        private static bool _screenCaptureCompleted;
        private static ulong _presentSequence;
        private static byte[] _previousPresentedFrame;
        private static int _textureUploadCount;
        private static int _bufferUploadCount;

        // MTLEnum 常量 (Metal.framework 头文件)
        internal const ulong PixelFormatBgra8Unorm = 80;
        internal const ulong LoadActionLoad = 1;
        internal const ulong LoadActionClear = 2;
        internal const ulong StoreActionStore = 1;
        internal const int ArgumentBufferBindPoint = 2;

        // ---- 帧级命令批处理状态 ----
        // 一帧内的所有绘制/拷贝/清除编码进同一个 MTLCommandBuffer，在呈现/同步/回读点
        // 统一提交，避免每绘制一次 commit 造成数千次提交与 GPU 空泡。
        private static nint _frameCommandBuffer;
        private static nint _renderEncoder;
        private static nint _encoderColorTarget;
        private static nint _encoderDepthTarget;
        private static List<nint> _inflightBuffersToRelease = new();
        private static List<nint> _pendingBufferReleases = new();

        // 帧内已使用的资源跟踪：命令缓冲在帧末才提交，若 CPU 在此期间重写已被
        // 编码命令引用的缓冲/纹理，先绘制的命令会读到新数据（经典 write-after-encode 危害）。
        // 检测到此类写入时先提交帧命令缓冲，保证写入发生在命令执行之后。
        private static readonly HashSet<ulong> _frameUsedBufferHandles = new();
        private static readonly HashSet<nint> _frameUsedTextures = new();

        /// <summary>
        /// 标记缓冲在本帧命令缓冲中已被引用。
        /// </summary>
        private static void TrackBufferUse(BufferRange range)
        {
            if (range.Handle != BufferHandle.Null)
            {
                BufferHandle handle = range.Handle;
                _frameUsedBufferHandles.Add(Unsafe.As<BufferHandle, ulong>(ref handle));
            }
        }

        /// <summary>
        /// CPU 将要写入缓冲：若该缓冲已被本帧命令引用，先提交帧命令缓冲。
        /// </summary>
        internal static void NotifyBufferWrite(BufferHandle handle)
        {
            if (handle != BufferHandle.Null)
            {
                ulong key = Unsafe.As<BufferHandle, ulong>(ref handle);
                if (_frameUsedBufferHandles.Contains(key))
                {
                    FlushFrame();
                }
            }
        }

        /// <summary>
        /// CPU 将要写入纹理：若该纹理已被本帧命令引用，先提交帧命令缓冲；
        /// 否则仅需结束活动编码器（replaceRegion 不能针对被活动编码器引用的纹理）。
        /// </summary>
        internal static void NotifyTextureWrite(nint nativeTexture)
        {
            if (nativeTexture != nint.Zero && _frameUsedTextures.Contains(nativeTexture))
            {
                FlushFrame();
            }
            else
            {
                EndRenderEncoder();
            }
        }

        public static nint Device
        {
            get
            {
                if (!OperatingSystem.IsMacOS())
                {
                    return nint.Zero;
                }

                if (_device == nint.Zero)
                {
                    lock (_lock)
                    {
                        if (_device == nint.Zero)
                            _device = MetalNative.MTLCreateSystemDefaultDevice();
                    }
                }
                return _device;
            }
        }

        public static nint CommandQueue
        {
            get
            {
                nint device = Device;
                if (_commandQueue == nint.Zero && device != nint.Zero)
                {
                    lock (_lock)
                    {
                        if (_commandQueue == nint.Zero)
                        {
                            _commandQueue = MetalNative.SendObject(device, MetalNative.Sel("newCommandQueue"));
                        }
                    }
                }
                return _commandQueue;
            }
        }

        public static bool IsAvailable => OperatingSystem.IsMacOS() && Device != nint.Zero;

        /// <summary>
        /// 绑定 UI 层创建的真实 CAMetalLayer（EmbeddedWindowMetal.GetMetalLayer）。
        /// 无窗口（headless）时传 nint.Zero，仅保留 CPU 侧帧数据供截图。
        /// </summary>
        public static void Initialize(nint metalLayer, nint nativeView = default)
        {
            lock (_lock)
            {
                if (metalLayer != nint.Zero)
                {
                    _layer = metalLayer;
                }
                if (nativeView != nint.Zero)
                {
                    _nativeView = nativeView;
                    _windowId = GetWindowId(nativeView);
                }
                else if (metalLayer == nint.Zero)
                {
                    _layer = nint.Zero;
                    _nativeView = nint.Zero;
                    _windowId = 0;
                }
                _screenCaptureCompleted = false;
                _layerConfigured = false;
            }
        }

        private static uint GetWindowId(nint nativeView)
        {
            if (nativeView == nint.Zero || !OperatingSystem.IsMacOS())
            {
                return 0;
            }

            nint window = MetalNative.SendObject(nativeView, MetalNative.Sel("window"));
            return window == nint.Zero
                ? 0u
                : unchecked((uint)MetalNative.SendNInt(window, MetalNative.Sel("windowNumber")));
        }

        private static nint _defaultAttributeBuffer;

        /// <summary>
        /// (0,0,0,1) 常量缓冲：供着色器读取 GPU 状态未声明的顶点属性（Maxwell 默认语义）。
        /// </summary>
        private static nint DefaultAttributeBuffer
        {
            get
            {
                if (_defaultAttributeBuffer == nint.Zero && Device != nint.Zero)
                {
                    lock (_lock)
                    {
                        if (_defaultAttributeBuffer == nint.Zero)
                        {
                            _defaultAttributeBuffer = MetalPipelineState.CreateDefaultAttributeBuffer(Device);
                        }
                    }
                }

                return _defaultAttributeBuffer;
            }
        }

        /// <summary>
        /// 获取或创建当前帧命令缓冲。注意 objc_msgSend 返回的是 autoreleased 对象，
        /// 跨 autorelease pool 存储必须显式 retain。
        /// </summary>
        private static nint EnsureFrameCommandBuffer()
        {
            if (_frameCommandBuffer == nint.Zero && CommandQueue != nint.Zero)
            {
                _frameCommandBuffer = MetalNative.SendObject(CommandQueue, MetalNative.Sel("commandBuffer"));
                if (_frameCommandBuffer != nint.Zero)
                {
                    MetalNative.SendVoid(_frameCommandBuffer, MetalNative.Sel("retain"));
                }
            }

            return _frameCommandBuffer;
        }

        /// <summary>
        /// 结束当前打开的渲染编码器（目标切换或非渲染操作前调用）。
        /// </summary>
        private static void EndRenderEncoder()
        {
            if (_renderEncoder != nint.Zero)
            {
                MetalNative.SendVoid(_renderEncoder, MetalNative.Sel("endEncoding"));
                MetalNative.SendVoid(_renderEncoder, MetalNative.Sel("release"));
                _renderEncoder = nint.Zero;
                _encoderColorTarget = nint.Zero;
                _encoderDepthTarget = nint.Zero;
            }
        }

        /// <summary>
        /// 提交当前帧命令缓冲并等待完成；随后释放上一帧遗留的延迟释放资源。
        /// 等待完成使 CPU→GPU 资源生命周期能安全串行化（正确性优先于流水线深度）。
        /// </summary>
        private static void FlushFrame()
        {
            EndRenderEncoder();

            if (_frameCommandBuffer == nint.Zero)
            {
                ReleasePendingBuffers();
                return;
            }

            MetalNative.SendVoid(_frameCommandBuffer, MetalNative.Sel("commit"));
            MetalNative.SendVoid(_frameCommandBuffer, MetalNative.Sel("waitUntilCompleted"));

            ulong status = MetalNative.SendULong(_frameCommandBuffer, MetalNative.Sel("status"));
            if (status != 4)
            {
                nint error = MetalNative.SendObject(_frameCommandBuffer, MetalNative.Sel("error"));
                Console.WriteLine($"[Metal][Frame] 命令缓冲提交失败 status={status}: {DescribeError(error)}");
            }

            MetalNative.SendVoid(_frameCommandBuffer, MetalNative.Sel("release"));
            _frameCommandBuffer = nint.Zero;

            // 帧命令已执行完毕，资源引用跟踪随之失效。
            _frameUsedBufferHandles.Clear();
            _frameUsedTextures.Clear();

            ReleasePendingBuffers();
        }

        /// <summary>
        /// 释放延迟删除的原生缓冲（须在 waitUntilCompleted 之后调用）。
        /// </summary>
        private static void ReleasePendingBuffers()
        {
            foreach (nint buffer in _inflightBuffersToRelease)
            {
                MetalNative.SendVoid(buffer, MetalNative.Sel("release"));
            }

            _inflightBuffersToRelease.Clear();

            // 本帧累积的删除请求转入在途列表：待下一次 FlushFrame（即本帧命令执行完毕）后释放。
            (_inflightBuffersToRelease, _pendingBufferReleases) = (_pendingBufferReleases, _inflightBuffersToRelease);
        }

        /// <summary>
        /// 缓冲的原生句柄删除请求进入延迟释放队列（可能仍被未提交/执行中的命令引用）。
        /// </summary>
        internal static void DeferBufferRelease(nint nativeBuffer)
        {
            if (nativeBuffer != nint.Zero)
            {
                _pendingBufferReleases.Add(nativeBuffer);
            }
        }

        /// <summary>
        /// 开始（或复用）针对给定目标的渲染编码器；目标变化时结束旧编码器。
        /// </summary>
        private static nint BeginOrReuseRenderEncoder(MetalTexture target, MetalTexture depthTarget)
        {
            nint depthNative = depthTarget is not null && depthTarget.NativeTexture != nint.Zero
                ? depthTarget.NativeTexture
                : nint.Zero;

            if (_renderEncoder != nint.Zero && _encoderColorTarget == target.NativeTexture && _encoderDepthTarget == depthNative)
            {
                return _renderEncoder;
            }

            EndRenderEncoder();
            nint commandBuffer = EnsureFrameCommandBuffer();
            if (commandBuffer == nint.Zero)
            {
                return nint.Zero;
            }

            nint descriptor = MetalNative.SendObject(MetalNative.Class("MTLRenderPassDescriptor"), MetalNative.Sel("renderPassDescriptor"));
            nint attachments = MetalNative.SendObject(descriptor, MetalNative.Sel("colorAttachments"));
            nint attachment = MetalNative.SendObject(attachments, MetalNative.Sel("objectAtIndexedSubscript:"), nint.Zero);

            MetalNative.SendVoid(attachment, MetalNative.Sel("setTexture:"), target.NativeTexture);
            MetalNative.SendVoid(attachment, MetalNative.Sel("setLoadAction:"), LoadActionLoad);
            MetalNative.SendVoid(attachment, MetalNative.Sel("setStoreAction:"), StoreActionStore);

            if (depthNative != nint.Zero)
            {
                nint depthAttachment = MetalNative.SendObject(descriptor, MetalNative.Sel("depthAttachment"));
                MetalNative.SendVoid(depthAttachment, MetalNative.Sel("setTexture:"), depthNative);
                MetalNative.SendVoid(depthAttachment, MetalNative.Sel("setLoadAction:"), LoadActionLoad);
                MetalNative.SendVoid(depthAttachment, MetalNative.Sel("setStoreAction:"), StoreActionStore);

                // 打包 depth/stencil 格式（D24S8/D32FS8）要求 stencil attachment 也指向同一纹理，
                // 否则 PSO 声明了 stencil pixel format 而 pass 未设置纹理，验证层直接 abort。
                if (depthTarget.Format.HasStencil)
                {
                    nint stencilAttachment = MetalNative.SendObject(descriptor, MetalNative.Sel("stencilAttachment"));
                    MetalNative.SendVoid(stencilAttachment, MetalNative.Sel("setTexture:"), depthNative);
                    MetalNative.SendVoid(stencilAttachment, MetalNative.Sel("setLoadAction:"), LoadActionLoad);
                    MetalNative.SendVoid(stencilAttachment, MetalNative.Sel("setStoreAction:"), StoreActionStore);
                }
            }

            _renderEncoder = MetalNative.SendObject(commandBuffer, MetalNative.Sel("renderCommandEncoderWithDescriptor:"), descriptor);
            if (_renderEncoder != nint.Zero)
            {
                // 跨 autorelease pool 持有，显式 retain（EndRenderEncoder 中配对 release）。
                MetalNative.SendVoid(_renderEncoder, MetalNative.Sel("retain"));
                _encoderColorTarget = target.NativeTexture;
                _encoderDepthTarget = depthNative;
            }
            else
            {
                _encoderColorTarget = nint.Zero;
                _encoderDepthTarget = nint.Zero;
            }
            return _renderEncoder;
        }

        internal static void EncodeDraw(
            MetalRenderer renderer,
            MetalProgram program,
            MetalTexture target,
            MetalTexture depthTarget,
            DepthTestDescriptor depthTest,
            StencilTestDescriptor stencilTest,
            BlendDescriptor blendDescriptor,
            uint colorWriteMask,
            bool cullEnable,
            Face cullMode,
            FrontFace frontFace,
            bool depthClamp,
            bool rasterizerDiscard,
            PolygonModeMask depthBiasEnables,
            float depthBiasFactor,
            float depthBiasUnits,
            float depthBiasClamp,
            float lineWidth,
            float pointSize,
            PolygonMode polygonFront,
            PolygonMode polygonBack,
            MultisampleDescriptor multisample,
            bool multisampleSet,
            bool logicOpEnable,
            LogicalOp logicOp,
            bool logicOpSet,
            VertexBufferDescriptor[] vertexBuffers,
            VertexAttribDescriptor[] vertexAttribs,
            Viewport[] viewports,
            Rectangle<int>[] scissors,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), (MetalTexture Texture, MetalSampler Sampler)> textures,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), MetalTexture> images,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), MetalTextureArray> textureArrays,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), MetalImageArray> imageArrays,
            IReadOnlyDictionary<int, BufferRange> uniformBuffers,
            IReadOnlyDictionary<int, BufferRange> storageBuffers,
            BufferRange indexBuffer,
            IndexType indexType,
            PrimitiveTopology topology,
            int vertexCount,
            int instanceCount,
            int firstVertex,
            int firstInstance,
            int firstIndex,
            bool indexed)
        {
            if (renderer == null || program?.Pipeline == nint.Zero || target?.NativeTexture == nint.Zero || CommandQueue == nint.Zero || rasterizerDiscard)
            {
                return;
            }

            nint pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                nint encoder = BeginOrReuseRenderEncoder(target, depthTarget);
                if (encoder == nint.Zero)
                {
                    return;
                }

                MetalNative.SendVoid(encoder, MetalNative.Sel("setRenderPipelineState:"), program.Pipeline);
                if (cullEnable)
                {
                    MetalNative.SendVoid(encoder, MetalNative.Sel("setCullMode:"), ToMetalCullMode(cullMode));
                }
                MetalNative.SendVoid(encoder, MetalNative.Sel("setFrontFacingWinding:"), ToMetalWinding(frontFace));
                // MTLDepthClipMode: Clip=0, Clamp=1.
                MetalNative.SendVoid(encoder, MetalNative.Sel("setDepthClipMode:"), depthClamp ? 1UL : 0UL);
                if ((depthBiasEnables & (PolygonModeMask.Fill | PolygonModeMask.Line | PolygonModeMask.Point)) != 0)
                {
                    MetalNative.SendFloat3(
                        encoder,
                        MetalNative.Sel("setDepthBias:slopeScale:clamp:"),
                        depthBiasFactor,
                        depthBiasUnits,
                        depthBiasClamp);
                }
                // Metal 要求线宽/点大小在合法区间且非 NaN；Maxwell 状态可能出现 0/NaN。
                // 现代 Metal 线宽固定为 1（setLineWidth 已废弃），不再发送；仅设置点大小。
                double safePointSize = double.IsNaN(pointSize) ? 1.0 : Math.Clamp((double)pointSize, 1.0, 64.0);
                MetalNative.SendVoid(encoder, MetalNative.Sel("setPointSize:"), safePointSize);
                MetalNative.SendVoid(encoder, MetalNative.Sel("setTriangleFillMode:"), ToMetalTriangleFillMode(polygonFront, polygonBack));
                if (blendDescriptor.Enable)
                {
                    MetalNative.SendVoid(
                        encoder,
                        MetalNative.Sel("setBlendColorRed:green:blue:alpha:"),
                        blendDescriptor.BlendConstant.Red,
                        blendDescriptor.BlendConstant.Green,
                        blendDescriptor.BlendConstant.Blue,
                        blendDescriptor.BlendConstant.Alpha);
                }
                nint depthStencilState = MetalPipelineState.CreateDepthStencilState(Device, depthTest, stencilTest);
                if (depthStencilState != nint.Zero)
                {
                    MetalNative.SendVoid(encoder, MetalNative.Sel("setDepthStencilState:"), depthStencilState);
                    if (stencilTest.TestEnable)
                    {
                        MetalNative.SendVoid(
                            encoder,
                            MetalNative.Sel("setStencilReferenceValue:frontBack:"),
                            (ulong)Math.Max(0, stencilTest.FrontFuncRef),
                            (ulong)Math.Max(0, stencilTest.BackFuncRef));
                    }
                }
                if (Environment.GetEnvironmentVariable("RYUJINX_METAL_TRACE_STATE") == "1" && viewports is { Length: > 0 })
                {
                    Console.WriteLine($"[Metal][State] viewport region=({viewports[0].Region.X},{viewports[0].Region.Y},{viewports[0].Region.Width}x{viewports[0].Region.Height}) target={target.Width}x{target.Height}");
                }
                SetViewportAndScissor(encoder, target, viewports, scissors);

                for (int index = 0; index < vertexBuffers?.Length; index++)
                {
                    nint buffer = renderer.GetNativeBuffer(vertexBuffers[index].Buffer);
                    if (buffer != nint.Zero)
                    {
                        MetalNative.SendVoid(
                            encoder,
                            MetalNative.Sel("setVertexBuffer:offset:atIndex:"),
                            buffer,
                            (ulong)Math.Max(0, vertexBuffers[index].Buffer.Offset),
                            (ulong)(MetalPipelineState.VertexBufferBase + index));
                    }
                }

                nint defaultAttributeBuffer = DefaultAttributeBuffer;
                if (defaultAttributeBuffer != nint.Zero)
                {
                    MetalNative.SendVoid(
                        encoder,
                        MetalNative.Sel("setVertexBuffer:offset:atIndex:"),
                        defaultAttributeBuffer,
                        0UL,
                        (ulong)MetalPipelineState.DefaultAttributeBufferIndex);
                }

                nint vertexArgumentBuffer = nint.Zero;
                nint fragmentArgumentBuffer = nint.Zero;
                bool usesArgumentBuffers = program.GetArgumentBufferSize(ShaderStage.Vertex) > 0 ||
                                            program.GetArgumentBufferSize(ShaderStage.Fragment) > 0;
                if (usesArgumentBuffers)
                {
                    vertexArgumentBuffer = CreateArgumentBuffer(
                        renderer,
                        program,
                        ShaderStage.Vertex,
                        textures,
                        images,
                        uniformBuffers,
                        storageBuffers,
                        textureArrays,
                        imageArrays);
                    fragmentArgumentBuffer = CreateArgumentBuffer(
                        renderer,
                        program,
                        ShaderStage.Fragment,
                        textures,
                        images,
                        uniformBuffers,
                        storageBuffers,
                        textureArrays,
                        imageArrays);

                    if (vertexArgumentBuffer != nint.Zero)
                    {
                        MetalNative.SendVoid(
                            encoder,
                            MetalNative.Sel("setVertexBuffer:offset:atIndex:"),
                            vertexArgumentBuffer,
                            0UL,
                            ArgumentBufferBindPoint);
                    }
                    if (fragmentArgumentBuffer != nint.Zero)
                    {
                        MetalNative.SendVoid(
                            encoder,
                            MetalNative.Sel("setFragmentBuffer:offset:atIndex:"),
                            fragmentArgumentBuffer,
                            0UL,
                            ArgumentBufferBindPoint);
                    }
                    MarkResourcesResident(encoder, renderer, vertexBuffers, textures, images, uniformBuffers, storageBuffers);
                }
                else
                {
                    BindRenderResources(encoder, renderer, program, textures, images, uniformBuffers, storageBuffers);
                }

                ulong primitive = ToMetalPrimitive(topology);
                BufferHandle convertedIndexBuffer = BufferHandle.Null;
                if (indexed)
                {
                    nint buffer = renderer.GetNativeBuffer(indexBuffer);
                    if (buffer != nint.Zero && indexType == IndexType.UByte)
                    {
                        byte[] source = renderer.GetBufferBytes(indexBuffer);
                        ushort[] converted = new ushort[source.Length];
                        for (int index = 0; index < source.Length; index++)
                        {
                            converted[index] = source[index];
                        }

                        convertedIndexBuffer = renderer.CreateBuffer(converted.Length * sizeof(ushort));
                        try
                        {
                            renderer.SetBufferData(convertedIndexBuffer, 0, MemoryMarshal.AsBytes(converted.AsSpan()));
                            buffer = renderer.GetNativeBuffer(new BufferRange(convertedIndexBuffer, 0, converted.Length * sizeof(ushort)));
                            MetalNative.SendVoid(
                                encoder,
                                MetalNative.Sel("drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:baseVertex:baseInstance:"),
                                primitive,
                                (ulong)Math.Max(0, vertexCount),
                                0UL,
                                buffer,
                                checked((ulong)Math.Max(0, firstIndex) * sizeof(ushort)),
                                (ulong)Math.Max(1, instanceCount),
                                (nint)firstVertex,
                                (ulong)Math.Max(0, firstInstance));
                        }
                        catch
                        {
                            renderer.DeleteBuffer(convertedIndexBuffer);
                            convertedIndexBuffer = BufferHandle.Null;
                            throw;
                        }
                    }
                    else if (buffer != nint.Zero)
                    {
                        MetalNative.SendVoid(
                            encoder,
                            MetalNative.Sel("drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:baseVertex:baseInstance:"),
                            primitive,
                            (ulong)Math.Max(0, vertexCount),
                            indexType == IndexType.UInt ? 1UL : 0UL,
                            buffer,
                            (ulong)Math.Max(0, indexBuffer.Offset + firstIndex * (indexType == IndexType.UInt ? 4 : 2)),
                            (ulong)Math.Max(1, instanceCount),
                            (nint)firstVertex,
                            (ulong)Math.Max(0, firstInstance));
                    }
                }
                else
                {
                    MetalNative.SendVoid(
                        encoder,
                        MetalNative.Sel("drawPrimitives:vertexStart:vertexCount:instanceCount:baseInstance:"),
                        primitive,
                        (ulong)Math.Max(0, firstVertex),
                        (ulong)Math.Max(0, vertexCount),
                        (ulong)Math.Max(1, instanceCount),
                        (ulong)Math.Max(0, firstInstance));
                }

                // 绘制编码完毕：保持编码器打开供后续绘制复用，由 FlushFrame 统一提交。
                if (depthStencilState != nint.Zero)
                {
                    MetalNative.SendVoid(depthStencilState, MetalNative.Sel("release"));
                }

                // 记录本帧已引用的资源，供 CPU 写入路径检测 write-after-encode 危害。
                TrackDrawResources(renderer, target, depthTarget, vertexBuffers, indexBuffer, uniformBuffers, storageBuffers, textures, images);

                if (vertexArgumentBuffer != nint.Zero)
                {
                    MetalNative.SendVoid(vertexArgumentBuffer, MetalNative.Sel("release"));
                }
                if (fragmentArgumentBuffer != nint.Zero)
                {
                    MetalNative.SendVoid(fragmentArgumentBuffer, MetalNative.Sel("release"));
                }
                if (convertedIndexBuffer != BufferHandle.Null)
                {
                    renderer.DeleteBuffer(convertedIndexBuffer);
                }
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        private static void TrackDrawResources(
            MetalRenderer renderer,
            MetalTexture target,
            MetalTexture depthTarget,
            VertexBufferDescriptor[] vertexBuffers,
            BufferRange indexBuffer,
            IReadOnlyDictionary<int, BufferRange> uniformBuffers,
            IReadOnlyDictionary<int, BufferRange> storageBuffers,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), (MetalTexture Texture, MetalSampler Sampler)> textures,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), MetalTexture> images)
        {
            if (target is not null && target.NativeTexture != nint.Zero)
            {
                _frameUsedTextures.Add(target.NativeTexture);
            }

            if (depthTarget is not null && depthTarget.NativeTexture != nint.Zero)
            {
                _frameUsedTextures.Add(depthTarget.NativeTexture);
            }

            for (int index = 0; index < vertexBuffers?.Length; index++)
            {
                TrackBufferUse(vertexBuffers[index].Buffer);
            }

            TrackBufferUse(indexBuffer);

            foreach (BufferRange range in (uniformBuffers ?? new Dictionary<int, BufferRange>()).Values)
            {
                TrackBufferUse(range);
            }

            foreach (BufferRange range in (storageBuffers ?? new Dictionary<int, BufferRange>()).Values)
            {
                TrackBufferUse(range);
            }

            foreach (KeyValuePair<(ShaderStage Stage, int Binding), (MetalTexture Texture, MetalSampler Sampler)> pair in textures ?? new Dictionary<(ShaderStage Stage, int Binding), (MetalTexture Texture, MetalSampler Sampler)>())
            {
                MetalTexture texture = pair.Value.Texture;
                if (texture is not null && texture.NativeTexture != nint.Zero)
                {
                    _frameUsedTextures.Add(texture.NativeTexture);
                }
            }

            foreach (KeyValuePair<(ShaderStage Stage, int Binding), MetalTexture> pair in images ?? new Dictionary<(ShaderStage Stage, int Binding), MetalTexture>())
            {
                MetalTexture texture = pair.Value;
                if (texture is not null && texture.NativeTexture != nint.Zero)
                {
                    _frameUsedTextures.Add(texture.NativeTexture);
                }
            }
        }

        private static nint CreateArgumentBuffer(
            MetalRenderer renderer,
            MetalProgram program,
            ShaderStage stage,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), (MetalTexture Texture, MetalSampler Sampler)> textures,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), MetalTexture> images,
            IReadOnlyDictionary<int, BufferRange> uniformBuffers,
            IReadOnlyDictionary<int, BufferRange> storageBuffers,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), MetalTextureArray> textureArrays,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), MetalImageArray> imageArrays)
        {
            int size = program.GetArgumentBufferSize(stage);
            if (size <= 0)
            {
                return nint.Zero;
            }

            size = (size + 7) & ~7;
            nint argumentBuffer = MetalNative.SendObject(
                Device,
                MetalNative.Sel("newBufferWithLength:options:"),
                (ulong)size,
                0UL);
            nint contents = argumentBuffer == nint.Zero
                ? nint.Zero
                : MetalNative.SendObject(argumentBuffer, MetalNative.Sel("contents"));
            if (contents == nint.Zero)
            {
                if (argumentBuffer != nint.Zero)
                {
                    MetalNative.SendVoid(argumentBuffer, MetalNative.Sel("release"));
                }
                return nint.Zero;
            }

            foreach ((int binding, BufferRange range) in uniformBuffers)
            {
                if (program.TryGetArgumentLocation(stage, ResourceType.UniformBuffer, binding, out int offset))
                {
                    ulong gpuAddress = renderer.GetBufferGpuAddress(range);
                    Console.WriteLine($"[Metal][AB] stage={stage} CBV binding={binding} offset={offset} address=0x{gpuAddress:X} size={range.Size}");
                    WriteArgumentEntry(contents, offset, gpuAddress, 0UL, (ulong)Math.Max(0, range.Size));
                }
            }

            foreach ((int binding, BufferRange range) in storageBuffers)
            {
                if (program.TryGetArgumentLocation(stage, ResourceType.StorageBuffer, binding, out int offset))
                {
                    WriteArgumentEntry(contents, offset, renderer.GetBufferGpuAddress(range), 0UL, (ulong)Math.Max(0, range.Size));
                }
            }

            foreach (((ShaderStage stageKey, int binding) key, (MetalTexture texture, MetalSampler sampler) resource) in textures)
            {
                if (key.stageKey != stage)
                {
                    continue;
                }

                if (resource.texture?.NativeTexture != nint.Zero &&
                    program.TryGetArgumentLocation(stage, ResourceType.Texture, key.binding, out int textureOffset))
                {
                    ulong textureResourceId = resource.texture.NativeResourceId;
                    Console.WriteLine($"[Metal][AB] stage={stage} SRV binding={key.binding} offset={textureOffset} resourceId=0x{textureResourceId:X}");
                    WriteArgumentEntry(contents, textureOffset, 0UL, textureResourceId, 0UL);
                }

                if (resource.sampler?.NativeSampler != nint.Zero &&
                    program.TryGetArgumentLocation(stage, ResourceType.Sampler, key.binding, out int samplerOffset))
                {
                    ulong samplerResourceId = resource.sampler.NativeResourceId;
                    Console.WriteLine($"[Metal][AB] stage={stage} SMP binding={key.binding} offset={samplerOffset} resourceId=0x{samplerResourceId:X}");
                    WriteSamplerEntry(contents, samplerOffset, samplerResourceId);
                }
            }

            foreach (((ShaderStage stageKey, int binding) key, MetalTextureArray array) in textureArrays)
            {
                if (key.stageKey != stage || array == null)
                {
                    continue;
                }

                for (int index = 0; index < array.Textures.Count; index++)
                {
                    MetalTexture texture = array.Textures[index].Texture;
                    int binding = key.binding + index;
                    if (texture?.NativeTexture == nint.Zero ||
                        !program.TryGetArgumentLocation(stage, ResourceType.Texture, binding, out int textureOffset))
                    {
                        continue;
                    }

                    WriteArgumentEntry(contents, textureOffset, 0UL, texture.NativeResourceId, 0UL);
                }
            }

            foreach (((ShaderStage stageKey, int binding) key, MetalImageArray array) in imageArrays)
            {
                if (key.stageKey != stage || array == null)
                {
                    continue;
                }

                for (int index = 0; index < array.Images.Count; index++)
                {
                    MetalTexture texture = array.Images[index];
                    int binding = key.binding + index;
                    if (texture?.NativeTexture == nint.Zero ||
                        !program.TryGetArgumentLocation(stage, ResourceType.Image, binding, out int imageOffset))
                    {
                        continue;
                    }

                    WriteArgumentEntry(contents, imageOffset, 0UL, texture.NativeResourceId, 0UL);
                }
            }

            foreach (((ShaderStage stageKey, int binding) key, MetalTexture texture) in images)
            {
                if (key.stageKey == stage && texture?.NativeTexture != nint.Zero &&
                    program.TryGetArgumentLocation(stage, ResourceType.Image, key.binding, out int imageOffset))
                {
                    WriteArgumentEntry(contents, imageOffset, 0UL, texture.NativeResourceId, 0UL);
                }
            }

            Console.WriteLine($"[Metal][AB] stage={stage} size={size} buffer=0x{argumentBuffer:X}");
            return argumentBuffer;
        }

        private static void MarkResourcesResident(
            nint encoder,
            MetalRenderer renderer,
            VertexBufferDescriptor[] vertexBuffers,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), (MetalTexture Texture, MetalSampler Sampler)> textures,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), MetalTexture> images,
            IReadOnlyDictionary<int, BufferRange> uniformBuffers,
            IReadOnlyDictionary<int, BufferRange> storageBuffers)
        {
            foreach (VertexBufferDescriptor vertexBuffer in vertexBuffers ?? Array.Empty<VertexBufferDescriptor>())
            {
                nint buffer = renderer.GetNativeBuffer(vertexBuffer.Buffer);
                if (buffer != nint.Zero)
                {
                    MetalNative.SendVoid(encoder, MetalNative.Sel("useResource:usage:"), buffer, 1UL);
                }
            }

            foreach ((int _, BufferRange range) in uniformBuffers)
            {
                nint buffer = renderer.GetNativeBuffer(range);
                if (buffer != nint.Zero)
                {
                    MetalNative.SendVoid(encoder, MetalNative.Sel("useResource:usage:"), buffer, 1UL);
                }
            }

            foreach ((int _, BufferRange range) in storageBuffers)
            {
                nint buffer = renderer.GetNativeBuffer(range);
                if (buffer != nint.Zero)
                {
                    MetalNative.SendVoid(encoder, MetalNative.Sel("useResource:usage:"), buffer, 3UL);
                }
            }

            foreach (KeyValuePair<(ShaderStage Stage, int Binding), (MetalTexture Texture, MetalSampler Sampler)> pair in textures)
            {
                MetalTexture texture = pair.Value.Texture;
                if (texture?.NativeTexture != nint.Zero)
                {
                    MetalNative.SendVoid(encoder, MetalNative.Sel("useResource:usage:"), texture.NativeTexture, 1UL);
                }
            }

            foreach (KeyValuePair<(ShaderStage Stage, int Binding), MetalTexture> pair in images)
            {
                MetalTexture texture = pair.Value;
                if (texture?.NativeTexture != nint.Zero)
                {
                    MetalNative.SendVoid(encoder, MetalNative.Sel("useResource:usage:"), texture.NativeTexture, 3UL);
                }
            }
        }

        private static void WriteArgumentEntry(nint contents, int offset, ulong gpuAddress, ulong resourceId, ulong metadata)
        {
            if (offset < 0)
            {
                return;
            }

            Marshal.WriteInt64(contents, offset, unchecked((long)gpuAddress));
            Marshal.WriteInt64(contents, checked(offset + 8), unchecked((long)resourceId));
            Marshal.WriteInt64(contents, checked(offset + 16), unchecked((long)metadata));
        }

        private static void WriteSamplerEntry(nint contents, int offset, ulong resourceId)
        {
            // IRDescriptorTableSetSampler stores the sampler resource ID in gpuVA,
            // unlike textures which store it in textureViewID.
            WriteArgumentEntry(contents, offset, resourceId, 0UL, 0UL);
        }

        private static void SetViewportAndScissor(
            nint encoder,
            MetalTexture target,
            Viewport[] viewports,
            Rectangle<int>[] scissors)
        {
            if (viewports is { Length: > 0 })
            {
                Viewport viewport = viewports[0];
                // Maxwell/GL 语义允许负高度视口（GL/Vulkan 原生支持，配合着色器端 Y 翻转）。
                // Metal 要求正高度：这里规范化矩形区间。Metal 的 NDC 约定（Y 轴向上）与
                // Maxwell 裁剪空间一致，无需着色器端翻转（实验已验证）。
                double x = viewport.Region.X;
                double y = viewport.Region.Y;
                double width = viewport.Region.Width;
                double height = viewport.Region.Height;
                if (height < 0)
                {
                    y += height;
                    height = -height;
                }
                if (width < 0)
                {
                    x += width;
                    width = -width;
                }

                MetalNative.SendVoid(
                    encoder,
                    MetalNative.Sel("setViewport:"),
                    new MTLViewport(x, y, width, height, viewport.DepthNear, viewport.DepthFar));
            }
            else
            {
                MetalNative.SendVoid(encoder, MetalNative.Sel("setViewport:"), new MTLViewport(0, 0, target.Width, target.Height, 0, 1));
            }

            if (scissors is { Length: > 0 })
            {
                Rectangle<int> scissor = scissors[0];
                MetalNative.SendVoid(
                    encoder,
                    MetalNative.Sel("setScissorRect:"),
                    new MTLScissorRect(
                        (ulong)Math.Max(0, scissor.X),
                        (ulong)Math.Max(0, scissor.Y),
                        (ulong)Math.Max(0, scissor.Width),
                        (ulong)Math.Max(0, scissor.Height)));
            }
        }

        private static void BindRenderResources(
            nint encoder,
            MetalRenderer renderer,
            MetalProgram program,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), (MetalTexture Texture, MetalSampler Sampler)> textures,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), MetalTexture> images,
            IReadOnlyDictionary<int, BufferRange> uniformBuffers,
            IReadOnlyDictionary<int, BufferRange> storageBuffers)
        {
            foreach ((int binding, BufferRange range) in uniformBuffers)
            {
                if (!program.UsesResource(ResourceType.UniformBuffer, ShaderStage.Vertex, binding) &&
                    !program.UsesResource(ResourceType.UniformBuffer, ShaderStage.Fragment, binding))
                {
                    continue;
                }

                nint buffer = renderer.GetNativeBuffer(range);
                if (buffer == nint.Zero)
                {
                    continue;
                }

                MetalNative.SendVoid(encoder, MetalNative.Sel("setVertexBuffer:offset:atIndex:"), buffer, (ulong)Math.Max(0, range.Offset), (ulong)binding);
                MetalNative.SendVoid(encoder, MetalNative.Sel("setFragmentBuffer:offset:atIndex:"), buffer, (ulong)Math.Max(0, range.Offset), (ulong)binding);
            }

            foreach ((int binding, BufferRange range) in storageBuffers)
            {
                if (!program.UsesResource(ResourceType.StorageBuffer, ShaderStage.Vertex, binding) &&
                    !program.UsesResource(ResourceType.StorageBuffer, ShaderStage.Fragment, binding))
                {
                    continue;
                }

                nint buffer = renderer.GetNativeBuffer(range);
                if (buffer == nint.Zero)
                {
                    continue;
                }

                MetalNative.SendVoid(encoder, MetalNative.Sel("setVertexBuffer:offset:atIndex:"), buffer, (ulong)Math.Max(0, range.Offset), (ulong)binding);
                MetalNative.SendVoid(encoder, MetalNative.Sel("setFragmentBuffer:offset:atIndex:"), buffer, (ulong)Math.Max(0, range.Offset), (ulong)binding);
            }

            foreach (((ShaderStage stage, int binding) key, (MetalTexture texture, MetalSampler sampler) resource) in textures)
            {
                if (!program.UsesResource(ResourceType.TextureAndSampler, key.stage, key.binding) &&
                    !program.UsesResource(ResourceType.Texture, key.stage, key.binding) &&
                    !program.UsesResource(ResourceType.BufferTexture, key.stage, key.binding))
                {
                    continue;
                }

                if (resource.texture?.NativeTexture == nint.Zero)
                {
                    continue;
                }

                string textureSelector = key.stage == ShaderStage.Vertex ? "setVertexTexture:atIndex:" : "setFragmentTexture:atIndex:";
                string samplerSelector = key.stage == ShaderStage.Vertex ? "setVertexSamplerState:atIndex:" : "setFragmentSamplerState:atIndex:";
                MetalNative.SendVoid(encoder, MetalNative.Sel(textureSelector), resource.texture.NativeTexture, (ulong)key.binding);

                if (resource.sampler?.NativeSampler != nint.Zero)
                {
                    MetalNative.SendVoid(encoder, MetalNative.Sel(samplerSelector), resource.sampler.NativeSampler, (ulong)key.binding);
                }
            }

            foreach (((ShaderStage stage, int binding) key, MetalTexture texture) in images)
            {
                if (!program.UsesResource(ResourceType.Image, key.stage, key.binding) &&
                    !program.UsesResource(ResourceType.BufferImage, key.stage, key.binding))
                {
                    continue;
                }

                if (texture?.NativeTexture == nint.Zero)
                {
                    continue;
                }

                string selector = key.stage == ShaderStage.Vertex ? "setVertexTexture:atIndex:" : "setFragmentTexture:atIndex:";
                MetalNative.SendVoid(encoder, MetalNative.Sel(selector), texture.NativeTexture, (ulong)key.binding);
            }
        }

        internal static void EncodeTextureBlit(
            MetalRenderer renderer,
            MetalProgram program,
            MetalTexture target,
            MetalTexture source,
            MetalSampler sampler,
            BufferRange parameters,
            Extents2DF destination)
        {
            Dictionary<(ShaderStage Stage, int Binding), (MetalTexture Texture, MetalSampler Sampler)> textures = new()
            {
                [(ShaderStage.Fragment, 0)] = (source, sampler),
            };
            Dictionary<int, BufferRange> uniformBuffers = new()
            {
                [0] = parameters,
            };
            Viewport[] viewports =
            [
                new Viewport(
                    new Rectangle<float>(0, 0, target.Width, target.Height),
                    ViewportSwizzle.PositiveX,
                    ViewportSwizzle.PositiveY,
                    ViewportSwizzle.PositiveZ,
                    ViewportSwizzle.PositiveW,
                    0,
                    1),
            ];

            EncodeDraw(
                renderer,
                program,
                target,
                null,
                default,
                default,
                default,
                0xF,
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
                new[]
                {
                    new Viewport(
                        new Rectangle<float>(
                            destination.X1,
                            destination.Y1,
                            destination.X2 - destination.X1,
                            destination.Y2 - destination.Y1),
                        ViewportSwizzle.PositiveX,
                        ViewportSwizzle.PositiveY,
                        ViewportSwizzle.PositiveZ,
                        ViewportSwizzle.PositiveW,
                        0,
                        1),
                },
                Array.Empty<Rectangle<int>>(),
                textures,
                new Dictionary<(ShaderStage Stage, int Binding), MetalTexture>(),
                new Dictionary<(ShaderStage Stage, int Binding), MetalTextureArray>(),
                new Dictionary<(ShaderStage Stage, int Binding), MetalImageArray>(),
                uniformBuffers,
                new Dictionary<int, BufferRange>(),
                BufferRange.Empty,
                IndexType.UShort,
                PrimitiveTopology.Triangles,
                6,
                1,
                0,
                0,
                0,
                false);
        }

        internal static void EncodeCompute(
            MetalRenderer renderer,
            MetalProgram program,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), (MetalTexture Texture, MetalSampler Sampler)> textures,
            IReadOnlyDictionary<(ShaderStage Stage, int Binding), MetalTexture> images,
            IReadOnlyDictionary<int, BufferRange> uniformBuffers,
            IReadOnlyDictionary<int, BufferRange> storageBuffers,
            int groupsX,
            int groupsY,
            int groupsZ)
        {
            if (renderer == null || program?.Pipeline == nint.Zero || !program.IsCompute || CommandQueue == nint.Zero)
            {
                return;
            }

            nint pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                // 计算派发编码进帧命令缓冲：渲染与计算交替时按编码顺序执行。
                EndRenderEncoder();
                nint commandBuffer = EnsureFrameCommandBuffer();
                nint encoder = commandBuffer == nint.Zero
                    ? nint.Zero
                    : MetalNative.SendObject(commandBuffer, MetalNative.Sel("computeCommandEncoder"));
                if (commandBuffer == nint.Zero || encoder == nint.Zero)
                {
                    return;
                }

                MetalNative.SendVoid(encoder, MetalNative.Sel("setComputePipelineState:"), program.Pipeline);

                foreach ((int binding, BufferRange range) in uniformBuffers)
                {
                    if (!program.UsesResource(ResourceType.UniformBuffer, ShaderStage.Compute, binding))
                    {
                        continue;
                    }

                    nint buffer = renderer.GetNativeBuffer(range);
                    if (buffer != nint.Zero)
                    {
                        MetalNative.SendVoid(encoder, MetalNative.Sel("setBuffer:offset:atIndex:"), buffer, (ulong)Math.Max(0, range.Offset), (ulong)binding);
                    }
                }

                foreach ((int binding, BufferRange range) in storageBuffers)
                {
                    if (!program.UsesResource(ResourceType.StorageBuffer, ShaderStage.Compute, binding))
                    {
                        continue;
                    }

                    nint buffer = renderer.GetNativeBuffer(range);
                    if (buffer != nint.Zero)
                    {
                        MetalNative.SendVoid(encoder, MetalNative.Sel("setBuffer:offset:atIndex:"), buffer, (ulong)Math.Max(0, range.Offset), (ulong)binding);
                    }
                }

                foreach (((ShaderStage stage, int binding) key, (MetalTexture texture, MetalSampler sampler) resource) in textures)
                {
                    if (key.stage != ShaderStage.Compute ||
                        (!program.UsesResource(ResourceType.TextureAndSampler, key.stage, key.binding) &&
                         !program.UsesResource(ResourceType.Texture, key.stage, key.binding) &&
                         !program.UsesResource(ResourceType.BufferTexture, key.stage, key.binding)) ||
                        resource.texture?.NativeTexture == nint.Zero)
                    {
                        continue;
                    }

                    MetalNative.SendVoid(encoder, MetalNative.Sel("setTexture:atIndex:"), resource.texture.NativeTexture, (ulong)key.binding);
                    if (resource.sampler?.NativeSampler != nint.Zero)
                    {
                        MetalNative.SendVoid(encoder, MetalNative.Sel("setSamplerState:atIndex:"), resource.sampler.NativeSampler, (ulong)key.binding);
                    }
                }

                foreach (((ShaderStage stage, int binding) key, MetalTexture texture) in images)
                {
                    if (key.stage == ShaderStage.Compute &&
                        (program.UsesResource(ResourceType.Image, key.stage, key.binding) ||
                         program.UsesResource(ResourceType.BufferImage, key.stage, key.binding)) &&
                        texture?.NativeTexture != nint.Zero)
                    {
                        MetalNative.SendVoid(encoder, MetalNative.Sel("setTexture:atIndex:"), texture.NativeTexture, (ulong)key.binding);
                    }
                }

                MTLSize groups = new((ulong)Math.Max(0, groupsX), (ulong)Math.Max(0, groupsY), (ulong)Math.Max(0, groupsZ));
                MTLSize threads = program.ThreadsPerThreadgroup;
                MetalNative.SendVoid(encoder, MetalNative.Sel("dispatchThreadgroups:threadsPerThreadgroup:"), groups, threads);
                MetalNative.SendVoid(encoder, MetalNative.Sel("endEncoding"));
                // 不在此处 commit，由 FlushFrame 统一提交。
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        private static ulong ToMetalCullMode(Face face) => face switch
        {
            Face.Front => 1,
            Face.Back => 2,
            _ => 0,
        };

        private static ulong ToMetalWinding(FrontFace frontFace) => frontFace == FrontFace.Clockwise ? 0UL : 1UL;

        private static ulong ToMetalTriangleFillMode(PolygonMode front, PolygonMode back)
        {
            if (front == PolygonMode.Line || back == PolygonMode.Line)
            {
                return 1UL;
            }

            // Metal has no point polygon fill mode. Point primitives remain supported;
            // polygon-point requests are conservatively rendered as filled triangles.
            return 0UL;
        }

        private static ulong ToMetalPrimitive(PrimitiveTopology topology) => topology switch
        {
            PrimitiveTopology.Points => 0,
            PrimitiveTopology.Lines => 1,
            PrimitiveTopology.LineStrip => 2,
            PrimitiveTopology.Triangles => 3,
            PrimitiveTopology.TriangleStrip => 4,
            PrimitiveTopology.TriangleFan => 5,
            _ => 3,
        };

        internal static void BlitTexture(MetalTexture source, MetalTexture target, MetalSampler sampler, Extents2DF srcRegion, Extents2DF dstRegion)
        {
            _ = sampler;
            if (source?.NativeTexture == nint.Zero || target?.NativeTexture == nint.Zero ||
                source.Format != target.Format || CommandQueue == nint.Zero)
            {
                return;
            }

            int sourceLeft = (int)MathF.Round(MathF.Min(srcRegion.X1, srcRegion.X2));
            int sourceTop = (int)MathF.Round(MathF.Min(srcRegion.Y1, srcRegion.Y2));
            int sourceWidth = (int)MathF.Round(MathF.Abs(srcRegion.X2 - srcRegion.X1));
            int sourceHeight = (int)MathF.Round(MathF.Abs(srcRegion.Y2 - srcRegion.Y1));
            int destinationLeft = (int)MathF.Round(MathF.Min(dstRegion.X1, dstRegion.X2));
            int destinationTop = (int)MathF.Round(MathF.Min(dstRegion.Y1, dstRegion.Y2));
            int destinationWidth = (int)MathF.Round(MathF.Abs(dstRegion.X2 - dstRegion.X1));
            int destinationHeight = (int)MathF.Round(MathF.Abs(dstRegion.Y2 - dstRegion.Y1));

            // A blit encoder copies texels but cannot scale or filter. Reject those
            // requests instead of silently copying the wrong region; the helper shader
            // remains a separate follow-up for scaled DrawTexture calls.
            if (sourceWidth <= 0 || sourceHeight <= 0 || sourceWidth != destinationWidth ||
                sourceHeight != destinationHeight || sourceLeft < 0 || sourceTop < 0 ||
                destinationLeft < 0 || destinationTop < 0 || sourceLeft + sourceWidth > source.Width ||
                sourceTop + sourceHeight > source.Height || destinationLeft + destinationWidth > target.Width ||
                destinationTop + destinationHeight > target.Height)
            {
                return;
            }

            nint pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                EndRenderEncoder();
                nint commandBuffer = EnsureFrameCommandBuffer();
                nint encoder = commandBuffer == nint.Zero
                    ? nint.Zero
                    : MetalNative.SendObject(commandBuffer, MetalNative.Sel("blitCommandEncoder"));
                if (encoder == nint.Zero)
                {
                    return;
                }

                MetalNative.SendVoid(
                    encoder,
                    MetalNative.Sel("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:"),
                    source.NativeTexture,
                    0,
                    0,
                    new MTLOrigin((ulong)sourceLeft, (ulong)sourceTop, 0),
                    new MTLSize((ulong)sourceWidth, (ulong)sourceHeight, 1),
                    target.NativeTexture,
                    0,
                    0,
                    new MTLOrigin((ulong)destinationLeft, (ulong)destinationTop, 0));
                MetalNative.SendVoid(encoder, MetalNative.Sel("endEncoding"));
                _frameUsedTextures.Add(source.NativeTexture);
                _frameUsedTextures.Add(target.NativeTexture);
                // 不在此处 commit，由 FlushFrame 统一提交。
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        internal static void BlitRegionFull(
            nint source,
            nint destination,
            ulong srcLayer,
            ulong dstLayer,
            ulong srcLevel,
            ulong dstLevel,
            ulong width,
            ulong height)
        {
            if (source == nint.Zero || destination == nint.Zero || CommandQueue == nint.Zero)
            {
                return;
            }

            nint pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                EndRenderEncoder();
                nint commandBuffer = EnsureFrameCommandBuffer();
                nint encoder = commandBuffer == nint.Zero
                    ? nint.Zero
                    : MetalNative.SendObject(commandBuffer, MetalNative.Sel("blitCommandEncoder"));
                if (encoder == nint.Zero)
                {
                    return;
                }

                MetalNative.SendVoid(
                    encoder,
                    MetalNative.Sel("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:"),
                    source,
                    srcLayer,
                    srcLevel,
                    new MTLOrigin(0, 0, 0),
                    new MTLSize(width, height, 1),
                    destination,
                    dstLayer,
                    dstLevel,
                    new MTLOrigin(0, 0, 0));
                MetalNative.SendVoid(encoder, MetalNative.Sel("endEncoding"));
                _frameUsedTextures.Add(source);
                _frameUsedTextures.Add(destination);
                // 不在此处 commit，由 FlushFrame 统一提交。
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        internal static void BlitRegion(nint source, nint destination, MTLOrigin srcOrigin, MTLOrigin dstOrigin, MTLSize size)
        {
            if (source == nint.Zero || destination == nint.Zero || CommandQueue == nint.Zero)
            {
                return;
            }

            nint pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                EndRenderEncoder();
                nint commandBuffer = EnsureFrameCommandBuffer();
                nint encoder = commandBuffer == nint.Zero
                    ? nint.Zero
                    : MetalNative.SendObject(commandBuffer, MetalNative.Sel("blitCommandEncoder"));
                if (encoder == nint.Zero)
                {
                    return;
                }

                MetalNative.SendVoid(
                    encoder,
                    MetalNative.Sel("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:"),
                    source,
                    0UL,
                    0UL,
                    srcOrigin,
                    size,
                    destination,
                    0UL,
                    0UL,
                    dstOrigin);
                MetalNative.SendVoid(encoder, MetalNative.Sel("endEncoding"));
                _frameUsedTextures.Add(source);
                _frameUsedTextures.Add(destination);
                // 不在此处 commit，由 FlushFrame 统一提交。
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        internal static void ClearDepthStencil(MetalTexture target, float depthValue, int stencilValue)
        {
            if (target == null || target.NativeTexture == nint.Zero || CommandQueue == nint.Zero)
            {
                return;
            }

            nint pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                // 清除是独立 render pass：先结束已打开的渲染编码器。
                EndRenderEncoder();
                nint commandBuffer = EnsureFrameCommandBuffer();
                nint descriptor = MetalNative.SendObject(MetalNative.Class("MTLRenderPassDescriptor"), MetalNative.Sel("renderPassDescriptor"));
                nint depthAttachment = MetalNative.SendObject(descriptor, MetalNative.Sel("depthAttachment"));
                MetalNative.SendVoid(depthAttachment, MetalNative.Sel("setTexture:"), target.NativeTexture);
                MetalNative.SendVoid(depthAttachment, MetalNative.Sel("setLoadAction:"), LoadActionClear);
                MetalNative.SendVoid(depthAttachment, MetalNative.Sel("setStoreAction:"), StoreActionStore);
                MetalNative.SendVoid(depthAttachment, MetalNative.Sel("setClearDepth:"), (double)Math.Clamp(depthValue, 0f, 1f));

                if (target.Format.HasStencil)
                {
                    nint stencilAttachment = MetalNative.SendObject(descriptor, MetalNative.Sel("stencilAttachment"));
                    MetalNative.SendVoid(stencilAttachment, MetalNative.Sel("setTexture:"), target.NativeTexture);
                    MetalNative.SendVoid(stencilAttachment, MetalNative.Sel("setLoadAction:"), LoadActionClear);
                    MetalNative.SendVoid(stencilAttachment, MetalNative.Sel("setStoreAction:"), StoreActionStore);
                    MetalNative.SendVoid(stencilAttachment, MetalNative.Sel("setClearStencil:"), (ulong)Math.Max(0, stencilValue));
                }

                nint encoder = MetalNative.SendObject(commandBuffer, MetalNative.Sel("renderCommandEncoderWithDescriptor:"), descriptor);
                if (encoder != nint.Zero)
                {
                    MetalNative.SendVoid(encoder, MetalNative.Sel("endEncoding"));
                    _frameUsedTextures.Add(target.NativeTexture);
                    // 不在此处 commit，由 FlushFrame 统一提交。
                }
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        internal static void ClearTexture(MetalTexture target, uint clearColor)
        {
            if (target == null || target.NativeTexture == nint.Zero || CommandQueue == nint.Zero)
            {
                return;
            }

            nint pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                // 清除是独立 render pass：先结束已打开的渲染编码器。
                EndRenderEncoder();
                nint commandBuffer = EnsureFrameCommandBuffer();
                nint descriptor = MetalNative.SendObject(MetalNative.Class("MTLRenderPassDescriptor"), MetalNative.Sel("renderPassDescriptor"));
                nint attachments = MetalNative.SendObject(descriptor, MetalNative.Sel("colorAttachments"));
                nint attachment = MetalNative.SendObject(attachments, MetalNative.Sel("objectAtIndexedSubscript:"), nint.Zero);

                MetalNative.SendVoid(attachment, MetalNative.Sel("setTexture:"), target.NativeTexture);
                MetalNative.SendVoid(attachment, MetalNative.Sel("setLoadAction:"), LoadActionClear);
                MetalNative.SendVoid(attachment, MetalNative.Sel("setStoreAction:"), StoreActionStore);
                MetalNative.SendVoid(attachment, MetalNative.Sel("setClearColor:"), ToMtClearColor(clearColor));

                nint encoder = MetalNative.SendObject(commandBuffer, MetalNative.Sel("renderCommandEncoderWithDescriptor:"), descriptor);
                MetalNative.SendVoid(encoder, MetalNative.Sel("endEncoding"));
                _frameUsedTextures.Add(target.NativeTexture);
                // 不在此处 commit，由 FlushFrame 统一提交。
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        /// <summary>
        /// 由 MetalRenderer 注入的缓冲区句柄解析器（CopyTo(buffer) 路径使用）。
        /// </summary>
        internal static Func<BufferRange, nint> NativeBufferResolver { get; set; }

        private static nint GetRendererNativeBuffer(BufferRange range) => NativeBufferResolver?.Invoke(range) ?? nint.Zero;

        /// <summary>
        /// 将纹理指定层/级复制到宿主缓冲区（GPU→CPU flush 路径）。
        /// stride 为 0 时按紧凑行距写入。
        /// </summary>
        internal static void ReadTextureIntoBuffer(
            MetalTexture source,
            BufferRange range,
            int layer,
            int level,
            int width,
            int height,
            int stride)
        {
            nint buffer = source == null ? nint.Zero : GetRendererNativeBuffer(range);
            if (source is not null && source.NativeTexture != nint.Zero && buffer != nint.Zero && CommandQueue != nint.Zero)
            {
                int bytesPerPixel = Math.Max(1, source.BytesPerPixel);
                int bytesPerRow = stride > 0 ? stride : (width * bytesPerPixel + 3) & ~3;
                nint pool = MetalNative.objc_autoreleasePoolPush();
                try
                {
                    EndRenderEncoder();
                    nint commandBuffer = EnsureFrameCommandBuffer();
                    nint encoder = commandBuffer == nint.Zero
                        ? nint.Zero
                        : MetalNative.SendObject(commandBuffer, MetalNative.Sel("blitCommandEncoder"));
                    if (encoder == nint.Zero)
                    {
                        return;
                    }

                    MetalNative.SendVoid(
                        encoder,
                        MetalNative.Sel("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:"),
                        source.NativeTexture,
                        (ulong)Math.Max(0, layer),
                        (ulong)Math.Max(0, level),
                        new MTLOrigin(0, 0, 0),
                        new MTLSize((ulong)width, (ulong)height, 1),
                        buffer,
                        0UL,
                        (ulong)bytesPerRow,
                        (ulong)(bytesPerRow * height));
                    MetalNative.SendVoid(encoder, MetalNative.Sel("endEncoding"));
                    // GPU→CPU 路径：立即提交并等待，保证 CPU 随后读到完整数据。
                    FlushFrame();
                }
                finally
                {
                    MetalNative.objc_autoreleasePoolPop(pool);
                }
            }
        }

        /// <summary>
        /// Waits for a GPU texture copy and returns tightly packed level data.
        /// This is intentionally a synchronous diagnostic/readback path; the frame path remains asynchronous.
        /// </summary>
        internal static byte[] ReadTexture(MetalTexture source, int layer, int level, int bytesPerPixel)
        {
            if (source == null)
            {
                return null;
            }

            int width = Math.Max(1, source.Width >> level);
            int height = Math.Max(1, source.Height >> level);
            return ReadNativeTexture(source.NativeTexture, width, height, level, bytesPerPixel, layer);
        }

        private static byte[] ReadNativeTexture(nint texture, int width, int height, int level, int bytesPerPixel, int slice = 0)
        {
            if (texture == nint.Zero || CommandQueue == nint.Zero || width <= 0 || height <= 0 || bytesPerPixel <= 0)
            {
                return null;
            }

            int bytesPerRow = checked((width * bytesPerPixel + 3) & ~3);
            int dataSize = checked(bytesPerRow * height);

            nint pool = MetalNative.objc_autoreleasePoolPush();
            nint stagingBuffer = nint.Zero;
            try
            {
                // 回读前提交帧命令缓冲：确保此前的绘制/拷贝对纹理的写入已完成。
                FlushFrame();

                stagingBuffer = MetalNative.SendObject(
                    MetalContext.Device,
                    MetalNative.Sel("newBufferWithLength:options:"),
                    (ulong)dataSize,
                    0UL);
                nint commandBuffer = MetalNative.SendObject(CommandQueue, MetalNative.Sel("commandBuffer"));
                nint encoder = commandBuffer == nint.Zero
                    ? nint.Zero
                    : MetalNative.SendObject(commandBuffer, MetalNative.Sel("blitCommandEncoder"));

                if (stagingBuffer == nint.Zero || commandBuffer == nint.Zero || encoder == nint.Zero)
                {
                    return null;
                }

                MetalNative.SendVoid(
                    encoder,
                    MetalNative.Sel("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:"),
                    texture,                            (ulong)Math.Max(0, slice),
                    (ulong)level,
                    new MTLOrigin(0, 0, 0),
                    new MTLSize((ulong)width, (ulong)height, 1),
                    stagingBuffer,
                    0,
                    (ulong)bytesPerRow,
                    (ulong)dataSize);
                MetalNative.SendVoid(encoder, MetalNative.Sel("endEncoding"));
                MetalNative.SendVoid(commandBuffer, MetalNative.Sel("commit"));
                MetalNative.SendVoid(commandBuffer, MetalNative.Sel("waitUntilCompleted"));

                ulong status = MetalNative.SendULong(commandBuffer, MetalNative.Sel("status"));
                if (status != 4)
                {
                    nint error = MetalNative.SendObject(commandBuffer, MetalNative.Sel("error"));
                    Console.WriteLine($"[Metal][Readback] command buffer status={status}: {DescribeError(error)}");
                    return null;
                }

                nint contents = MetalNative.SendObject(stagingBuffer, MetalNative.Sel("contents"));
                if (contents == nint.Zero)
                {
                    return null;
                }

                byte[] result = new byte[dataSize];
                Marshal.Copy(contents, result, 0, dataSize);
                return result;
            }
            finally
            {
                if (stagingBuffer != nint.Zero)
                {
                    MetalNative.SendVoid(stagingBuffer, MetalNative.Sel("release"));
                }
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        /// <summary>
        /// Submits a fence command on the same queue and waits for all previously submitted work.
        /// This is intended for diagnostics/readback, not for the normal asynchronous frame path.
        /// </summary>
        public static bool WaitForIdle()
        {
            if (CommandQueue == nint.Zero)
            {
                return false;
            }

            nint pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                FlushFrame();
                return true;
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        private static string DescribeError(nint error)
        {
            if (error == nint.Zero)
            {
                return "NSError 为空";
            }

            nint description = MetalNative.SendObject(error, MetalNative.Sel("localizedDescription"));
            nint utf8 = MetalNative.SendObject(description, MetalNative.Sel("UTF8String"));
            return Marshal.PtrToStringUTF8(utf8) ?? "NSError 无描述";
        }

        /// <summary>
        /// 将 GPU 最终纹理复制到 CAMetalLayer drawable。
        /// 当前只走同尺寸 blit；缩放、裁剪和格式转换由后续 render pass 补齐。
        /// </summary>
        internal static bool PresentTexture(MetalTexture source, ImageCrop crop = default)
        {
            // 无头模式帧转储：无 CAMetalLayer 时直接回读呈现纹理，供离屏比对。
            string dumpDir = Environment.GetEnvironmentVariable("RYUJINX_METAL_DUMP_FRAME_DIR");
            if (_layer == nint.Zero && !string.IsNullOrWhiteSpace(dumpDir))
            {
                DumpPresentedFrame(source, dumpDir);
                return false;
            }

            if (source == null || source.NativeTexture == nint.Zero || _layer == nint.Zero || CommandQueue == nint.Zero)
            {
                if (Environment.GetEnvironmentVariable("RYUJINX_METAL_TRACE_PRESENT") == "1")
                {
                    Console.WriteLine($"[Metal][Present] unavailable source={(source != null)} texture={(source?.NativeTexture != nint.Zero)} layer={_layer != nint.Zero} queue={CommandQueue != nint.Zero}");
                }
                return false;
            }

            _width = source.Width;
            _height = source.Height;

            // 呈现前提交本帧全部绘制命令，保证 drawable blit 读到完整帧内容。
            FlushFrame();

            nint pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                ConfigureLayerOnce(
                    source.Width,
                    source.Height,
                    MetalTextureDescriptor.ToPixelFormat(source.Format, DepthStencilMode.Depth));

                nint drawable = MetalNative.SendObject(_layer, MetalNative.Sel("nextDrawable"));
                nint commandBuffer = drawable == nint.Zero
                    ? nint.Zero
                    : MetalNative.SendObject(CommandQueue, MetalNative.Sel("commandBuffer"));
                nint drawableTexture = drawable == nint.Zero
                    ? nint.Zero
                    : MetalNative.SendObject(drawable, MetalNative.Sel("texture"));

                if (drawable == nint.Zero || commandBuffer == nint.Zero || drawableTexture == nint.Zero)
                {
                    if (Environment.GetEnvironmentVariable("RYUJINX_METAL_TRACE_PRESENT") == "1")
                    {
                        Console.WriteLine($"[Metal][Present] unavailable drawable={drawable != nint.Zero} commandBuffer={commandBuffer != nint.Zero} texture={drawableTexture != nint.Zero}");
                    }
                    return false;
                }

                nint encoder = MetalNative.SendObject(commandBuffer, MetalNative.Sel("blitCommandEncoder"));
                if (encoder == nint.Zero)
                {
                    if (Environment.GetEnvironmentVariable("RYUJINX_METAL_TRACE_PRESENT") == "1")
                    {
                        Console.WriteLine("[Metal][Present] blit encoder unavailable");
                    }
                    return false;
                }

                // ImageCrop.Right/Bottom are ending coordinates. A zero pair means
                // the complete source texture, matching the GAL/Vulkan contract.
                bool hasCropX = crop.Left != 0 || crop.Right != 0;
                bool hasCropY = crop.Top != 0 || crop.Bottom != 0;
                int left = Math.Clamp(crop.Left, 0, source.Width);
                int right = hasCropX ? Math.Clamp(crop.Right, left, source.Width) : source.Width;
                int top = Math.Clamp(crop.Top, 0, source.Height);
                int bottom = hasCropY ? Math.Clamp(crop.Bottom, top, source.Height) : source.Height;
                int width = right - left;
                int height = bottom - top;
                if (width <= 0 || height <= 0)
                {
                    left = 0;
                    top = 0;
                    width = source.Width;
                    height = source.Height;
                }

                MTLOrigin origin = new((ulong)left, (ulong)top, 0);
                MTLSize size = new((ulong)width, (ulong)height, 1);
                MetalNative.SendVoid(
                    encoder,
                    MetalNative.Sel("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:"),
                    source.NativeTexture,
                    0,
                    0,
                    origin,
                    size,
                    drawableTexture,
                    0,
                    0,
                    new MTLOrigin(0, 0, 0));
                MetalNative.SendVoid(encoder, MetalNative.Sel("endEncoding"));
                MetalNative.SendVoid(commandBuffer, MetalNative.Sel("presentDrawable:"), drawable);
                MetalNative.SendVoid(commandBuffer, MetalNative.Sel("commit"));
                Action<byte[], int, int> callback = _screenshotCallback;
                if (callback != null)
                {
                    // 保存截图：从真实 drawable 读回像素，替代纯 clear 色占位。
                    _screenshotCallback = null;
                    MetalNative.SendVoid(commandBuffer, MetalNative.Sel("waitUntilCompleted"));
                    byte[] shot = ReadNativeTexture(drawableTexture, source.Width, source.Height, 0, source.BytesPerPixel);
                    if (shot != null)
                    {
                        lock (_lock) { _lastFrameData = shot; }
                        callback(shot, source.Width, source.Height);
                    }
                }

                bool trace = Environment.GetEnvironmentVariable("RYUJINX_METAL_TRACE_PRESENT") == "1";
                bool compareDrawable = Environment.GetEnvironmentVariable("RYUJINX_METAL_COMPARE_DRAWABLE") == "1";
                bool captureScreen = Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_SCREEN") == "1";
                bool frameStats = Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_FRAME_STATS") == "1";
                if (trace || compareDrawable || captureScreen || frameStats)
                {
                    // Present is asynchronous. Wait only in explicit diagnostics so normal
                    // frames keep the existing non-blocking path.
                    MetalNative.SendVoid(commandBuffer, MetalNative.Sel("waitUntilCompleted"));
                    ulong status = MetalNative.SendULong(commandBuffer, MetalNative.Sel("status"));
                    nint error = status == 4 ? nint.Zero : MetalNative.SendObject(commandBuffer, MetalNative.Sel("error"));
                    RecordPresentedFrame(source, drawableTexture, left, top, width, height, status == 4);
                    if (trace)
                    {
                        Console.WriteLine($"[Metal][Present] source={source.Width}x{source.Height} format={source.Format} crop={left},{top},{width}x{height} submitted=true status={status} error={DescribeError(error)}");
                    }

                    if (compareDrawable && status == 4)
                    {
                        ComparePresentedDrawable(source, drawableTexture, drawable, left, top, width, height);
                    }

                    if (captureScreen && status == 4 && !_screenCaptureCompleted)
                    {
                        _screenCaptureCompleted = true;
                        int drawableWidth = checked((int)MetalNative.SendULong(drawableTexture, MetalNative.Sel("width")));
                        int drawableHeight = checked((int)MetalNative.SendULong(drawableTexture, MetalNative.Sel("height")));
                        byte[] sourcePixels = ReadNativeTexture(source.NativeTexture, source.Width, source.Height, 0, source.BytesPerPixel);
                        byte[] drawablePixels = ReadNativeTexture(drawableTexture, drawableWidth, drawableHeight, 0, source.BytesPerPixel);
                        CaptureAndCompareScreen(source, sourcePixels, drawablePixels, drawableWidth, drawableHeight, left, top, width, height);
                    }
                }
                return true;
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        private static int _dumpedFrameCount;
        private static int _presentedFrameCount;

        /// <summary>
        /// 无头模式：回读当前呈现纹理并写出 BMP（每 RYUJINX_METAL_DUMP_FRAME_INTERVAL 帧一次，
        /// 最多 RYUJINX_METAL_DUMP_FRAME_MAX 张），用于离屏画面正确性比对。
        /// </summary>
        private static void DumpPresentedFrame(MetalTexture source, string dumpDir)
        {
            _presentedFrameCount++;
            int interval = int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_DUMP_FRAME_INTERVAL"), out int parsed) && parsed > 0 ? parsed : 30;
            if (_presentedFrameCount % interval != 0)
            {
                return;
            }

            byte[] pixels = ReadNativeTexture(source.NativeTexture, source.Width, source.Height, 0, source.BytesPerPixel);
            if (pixels == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(dumpDir);
                string path = Path.Combine(dumpDir, $"frame-{_dumpedFrameCount:D4}-{source.Width}x{source.Height}.bmp");
                WriteBgraBmp(path, pixels, source.Width, source.Height);
                _dumpedFrameCount++;
                Console.WriteLine($"[Metal][Dump] 已写出 {path}");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[Metal][Dump] 写帧失败: {exception.Message}");
            }

            int maxFrames = int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_DUMP_FRAME_MAX"), out int max) && max > 0 ? max : 5;
            if (_dumpedFrameCount >= maxFrames)
            {
                Environment.Exit(0);
            }
        }

        /// <summary>
        /// 将 BGRA8 像素数据写成 24 位 BMP（无 alpha 通道，行序翻转满足 BMP 自底向上存储）。
        /// </summary>
        private static void WriteBgraBmp(string path, byte[] bgra, int width, int height)
        {
            int rowBytes = (width * 3 + 3) & ~3;
            int pixelBytes = rowBytes * height;
            int fileSize = 54 + pixelBytes;

            using FileStream stream = new(path, FileMode.Create);
            using BinaryWriter writer = new(stream);
            writer.Write((byte)'B'); writer.Write((byte)'M');
            writer.Write(fileSize);
            writer.Write(0);
            writer.Write(54);
            writer.Write(40);
            writer.Write(width);
            writer.Write(height);
            writer.Write((short)1);
            writer.Write((short)24);
            writer.Write(0);
            writer.Write(pixelBytes);
            writer.Write(2835);
            writer.Write(2835);
            writer.Write(0);
            writer.Write(0);

            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * width + x) * 4;
                    writer.Write(bgra[offset]);     // B
                    writer.Write(bgra[offset + 1]); // G
                    writer.Write(bgra[offset + 2]); // R
                }

                for (int pad = width * 3; pad < rowBytes; pad++)
                {
                    writer.Write((byte)0);
                }
            }
        }

        private static void ComparePresentedDrawable(
            MetalTexture source,
            nint drawableTexture,
            nint drawable,
            int sourceLeft,
            int sourceTop,
            int width,
            int height)
        {
            int drawableWidth = checked((int)MetalNative.SendULong(drawableTexture, MetalNative.Sel("width")));
            int drawableHeight = checked((int)MetalNative.SendULong(drawableTexture, MetalNative.Sel("height")));
            byte[] sourcePixels = ReadNativeTexture(source.NativeTexture, source.Width, source.Height, 0, source.BytesPerPixel);
            byte[] drawablePixels = ReadNativeTexture(drawableTexture, drawableWidth, drawableHeight, 0, source.BytesPerPixel);
            DrawableComparison comparison = ComparePixels(
                sourcePixels,
                drawablePixels,
                source.Width,
                source.Height,
                drawableWidth,
                drawableHeight,
                sourceLeft,
                sourceTop,
                width,
                height,
                source.BytesPerPixel);

            object result = new
            {
                capturedAtUtc = DateTime.UtcNow,
                drawable = $"0x{drawable:X}",
                source = new { width = source.Width, height = source.Height, format = source.Format.ToString() },
                drawableTexture = new { width = drawableWidth, height = drawableHeight },
                copiedSourceRect = new { x = sourceLeft, y = sourceTop, width, height },
                comparison = new
                {
                    comparedPixels = comparison.ComparedPixels,
                    comparedBytes = comparison.ComparedBytes,
                    mismatchedPixels = comparison.MismatchedPixels,
                    mismatchedBytes = comparison.MismatchedBytes,
                    identical = comparison.MismatchedBytes == 0 && comparison.ComparedPixels > 0,
                },
            };
            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            string capturePath = Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                Directory.CreateDirectory(capturePath);
                File.WriteAllText(Path.Combine(capturePath, "drawable-comparison.json"), json);
            }
            Console.WriteLine($"[Metal][PresentCompare] pixels={comparison.ComparedPixels} mismatchedPixels={comparison.MismatchedPixels} mismatchedBytes={comparison.MismatchedBytes} identical={comparison.MismatchedBytes == 0 && comparison.ComparedPixels > 0}");
        }

        private readonly record struct DrawableComparison(int ComparedPixels, int ComparedBytes, int MismatchedPixels, int MismatchedBytes);

        private static void CaptureAndCompareScreen(
            MetalTexture source,
            byte[] sourcePixels,
            byte[] drawablePixels,
            int drawableWidth,
            int drawableHeight,
            int sourceLeft,
            int sourceTop,
            int width,
            int height)
        {
            if (_windowId == 0)
            {
                Console.WriteLine("[Metal][ScreenCompare] skipped: no native window id");
                return;
            }

            const uint windowListOptionIncludingWindow = 8u;
            const uint imageOptionBoundsIgnoreFraming = 1u;
            const uint imageOptionNominalResolution = 8u;
            nint image = MetalNative.CGWindowListCreateImage(
                new CGRect(0, 0, 0, 0),
                windowListOptionIncludingWindow,
                _windowId,
                imageOptionBoundsIgnoreFraming | imageOptionNominalResolution);
            if (image == nint.Zero)
            {
                Console.WriteLine("[Metal][ScreenCompare] skipped: CGWindowListCreateImage returned null");
                return;
            }

            try
            {
                int screenWidth = checked((int)MetalNative.CGImageGetWidth(image));
                int screenHeight = checked((int)MetalNative.CGImageGetHeight(image));
                nint provider = MetalNative.CGImageGetDataProvider(image);
                nint data = provider == nint.Zero ? nint.Zero : MetalNative.CGDataProviderCopyData(provider);
                if (data == nint.Zero)
                {
                    Console.WriteLine("[Metal][ScreenCompare] skipped: image data unavailable");
                    return;
                }

                try
                {
                    nuint length = MetalNative.CFDataGetLength(data);
                    nint pointer = MetalNative.CFDataGetBytePtr(data);
                    int packedRowBytes = checked(screenWidth * 4);
                    int providerRowBytes = checked((int)MetalNative.CGImageGetBytesPerRow(image));
                    int expectedProviderBytes = checked(providerRowBytes * screenHeight);
                    if (pointer == nint.Zero || providerRowBytes < packedRowBytes || length < (nuint)expectedProviderBytes)
                    {
                        Console.WriteLine($"[Metal][ScreenCompare] skipped: invalid image data length={length} rowBytes={providerRowBytes} expected={expectedProviderBytes}");
                        return;
                    }

                    // CGImage providers may pad each row. Normalize to tightly packed
                    // 4-byte pixels before matching against the Metal readback.
                    int expected = checked(packedRowBytes * screenHeight);
                    byte[] screenPixels = new byte[expected];
                    for (int row = 0; row < screenHeight; row++)
                    {
                        Marshal.Copy(pointer + row * providerRowBytes, screenPixels, row * packedRowBytes, packedRowBytes);
                    }
                    DrawableComparison sourceToDrawable = ComparePixels(
                        sourcePixels,
                        drawablePixels,
                        source.Width,
                        source.Height,
                        drawableWidth,
                        drawableHeight,
                        sourceLeft,
                        sourceTop,
                        width,
                        height,
                        source.BytesPerPixel);
                    ScreenMatch match = FindBestScreenMatch(
                        drawablePixels,
                        drawableWidth,
                        drawableHeight,
                        screenPixels,
                        screenWidth,
                        screenHeight);
                    DrawableComparison sourceToScreen = CompareScreenRegion(
                        sourcePixels,
                        source.Width,
                        source.Height,
                        screenPixels,
                        screenWidth,
                        screenHeight,
                        match);
                    DrawableComparison drawableToScreen = CompareScreenRegion(
                        drawablePixels,
                        drawableWidth,
                        drawableHeight,
                        screenPixels,
                        screenWidth,
                        screenHeight,
                        match);

                    object result = new
                    {
                        capturedAtUtc = DateTime.UtcNow,
                        windowId = _windowId,
                        screen = new { width = screenWidth, height = screenHeight, format = "CGImage provider bytes (bitmap order reported by CoreGraphics provider)" },
                        drawable = new { width = drawableWidth, height = drawableHeight },
                        source = new { width = source.Width, height = source.Height, format = source.Format.ToString() },
                        copiedSourceRect = new { x = sourceLeft, y = sourceTop, width, height },
                        match = new { scale = match.Scale, offsetX = match.OffsetX, offsetY = match.OffsetY, channelOrder = match.ChannelOrder },
                        comparison = new
                        {
                            sourceToDrawable = new
                            {
                                comparedPixels = sourceToDrawable.ComparedPixels,
                                comparedBytes = sourceToDrawable.ComparedBytes,
                                mismatchedPixels = sourceToDrawable.MismatchedPixels,
                                mismatchedBytes = sourceToDrawable.MismatchedBytes,
                                identical = sourceToDrawable.MismatchedBytes == 0 && sourceToDrawable.ComparedPixels > 0,
                            },
                            drawableToScreen = new
                            {
                                comparedPixels = drawableToScreen.ComparedPixels,
                                comparedBytes = drawableToScreen.ComparedBytes,
                                mismatchedPixels = drawableToScreen.MismatchedPixels,
                                mismatchedBytes = drawableToScreen.MismatchedBytes,
                                identical = drawableToScreen.MismatchedBytes == 0 && drawableToScreen.ComparedPixels > 0,
                            },
                            sourceToScreen = new
                            {
                                comparedPixels = sourceToScreen.ComparedPixels,
                                comparedBytes = sourceToScreen.ComparedBytes,
                                mismatchedPixels = sourceToScreen.MismatchedPixels,
                                mismatchedBytes = sourceToScreen.MismatchedBytes,
                                identical = sourceToScreen.MismatchedBytes == 0 && sourceToScreen.ComparedPixels > 0,
                            },
                        },
                    };
                    string capturePath = Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_PATH");
                    if (!string.IsNullOrWhiteSpace(capturePath))
                    {
                        Directory.CreateDirectory(capturePath);
                        File.WriteAllBytes(Path.Combine(capturePath, "screen-window.bgra8.bin"), screenPixels);
                        File.WriteAllText(Path.Combine(capturePath, "screen-comparison.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    Console.WriteLine($"[Metal][ScreenCompare] screen={screenWidth}x{screenHeight} drawable={drawableWidth}x{drawableHeight} match={match.Scale:F4}@{match.OffsetX},{match.OffsetY} {match.ChannelOrder} drawableMismatchedPixels={drawableToScreen.MismatchedPixels} sourceMismatchedPixels={sourceToScreen.MismatchedPixels}");
                }
                finally
                {
                    MetalNative.CFRelease(data);
                }
            }
            finally
            {
                MetalNative.CGImageRelease(image);
            }
        }

        private readonly record struct ScreenMatch(double Scale, int OffsetX, int OffsetY, string ChannelOrder);

        private static ScreenMatch FindBestScreenMatch(byte[] drawable, int drawableWidth, int drawableHeight, byte[] screen, int screenWidth, int screenHeight)
        {
            double scale = Math.Min((double)screenWidth / drawableWidth, (double)screenHeight / drawableHeight);
            int width = Math.Max(1, (int)Math.Round(drawableWidth * scale));
            int height = Math.Max(1, (int)Math.Round(drawableHeight * scale));
            int bestX = Math.Max(0, (screenWidth - width) / 2);
            int bestY = Math.Max(0, (screenHeight - height) / 2);
            double bestScore = double.MaxValue;
            string bestOrder = "B,G,R,A";
            int step = Math.Max(1, Math.Min(drawableWidth, drawableHeight) / 32);
            for (int y = 0; y <= screenHeight - height; y += Math.Max(1, (screenHeight - height) / 12))
            {
                for (int x = 0; x <= screenWidth - width; x += Math.Max(1, (screenWidth - width) / 12))
                {
                    for (int order = 0; order < 2; order++)
                    {
                        double score = 0;
                        int samples = 0;
                        for (int dy = 0; dy < drawableHeight; dy += step)
                        {
                            int sy = y + Math.Min(height - 1, (int)(dy * scale));
                            for (int dx = 0; dx < drawableWidth; dx += step)
                            {
                                int sx = x + Math.Min(width - 1, (int)(dx * scale));
                                int a = (dy * drawableWidth + dx) * 4;
                                int b = (sy * screenWidth + sx) * 4;
                                int[] channels = order == 0 ? [0, 1, 2, 3] : [2, 1, 0, 3];
                                for (int c = 0; c < 4; c++) score += Math.Abs(drawable[a + c] - screen[b + channels[c]]);
                                samples++;
                            }
                        }
                        score /= Math.Max(1, samples * 4);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestX = x;
                            bestY = y;
                            bestOrder = order == 0 ? "B,G,R,A" : "R,G,B,A";
                        }
                    }
                }
            }
            return new ScreenMatch(scale, bestX, bestY, bestOrder);
        }

        private static DrawableComparison CompareScreenRegion(byte[] first, int firstWidth, int firstHeight, byte[] screen, int screenWidth, int screenHeight, ScreenMatch match)
        {
            int width = Math.Min(firstWidth, Math.Max(0, (int)Math.Round(screenWidth / match.Scale)));
            int height = Math.Min(firstHeight, Math.Max(0, (int)Math.Round(screenHeight / match.Scale)));
            bool swap = match.ChannelOrder == "R,G,B,A";
            int[] channels = swap ? [2, 1, 0, 3] : [0, 1, 2, 3];
            int comparedPixels = 0, comparedBytes = 0, mismatchedPixels = 0, mismatchedBytes = 0;
            for (int y = 0; y < height; y++)
            {
                int sy = match.OffsetY + Math.Min(screenHeight - match.OffsetY - 1, (int)(y * match.Scale));
                for (int x = 0; x < width; x++)
                {
                    int sx = match.OffsetX + Math.Min(screenWidth - match.OffsetX - 1, (int)(x * match.Scale));
                    int a = (y * firstWidth + x) * 4, b = (sy * screenWidth + sx) * 4;
                    bool mismatch = false;
                    for (int c = 0; c < 4; c++)
                    {
                        comparedBytes++;
                        if (first[a + c] != screen[b + channels[c]]) { mismatchedBytes++; mismatch = true; }
                    }
                    comparedPixels++;
                    if (mismatch) mismatchedPixels++;
                }
            }
            return new DrawableComparison(comparedPixels, comparedBytes, mismatchedPixels, mismatchedBytes);
        }

        private static DrawableComparison ComparePixels(
            byte[] source,
            byte[] drawable,
            int sourceWidth,
            int sourceHeight,
            int drawableWidth,
            int drawableHeight,
            int sourceLeft,
            int sourceTop,
            int width,
            int height,
            int bytesPerPixel)
        {
            if (source == null || drawable == null || bytesPerPixel <= 0)
            {
                return default;
            }

            int comparedPixels = 0;
            int comparedBytes = 0;
            int mismatchedPixels = 0;
            int mismatchedBytes = 0;
            int right = Math.Min(width, Math.Min(sourceWidth - sourceLeft, drawableWidth));
            int bottom = Math.Min(height, Math.Min(sourceHeight - sourceTop, drawableHeight));
            for (int y = 0; y < Math.Max(0, bottom); y++)
            {
                for (int x = 0; x < Math.Max(0, right); x++)
                {
                    int sourceOffset = ((sourceTop + y) * sourceWidth + sourceLeft + x) * bytesPerPixel;
                    int drawableOffset = (y * drawableWidth + x) * bytesPerPixel;
                    bool pixelMismatch = false;
                    for (int channel = 0; channel < bytesPerPixel; channel++)
                    {
                        comparedBytes++;
                        if (sourceOffset + channel >= source.Length || drawableOffset + channel >= drawable.Length || source[sourceOffset + channel] != drawable[drawableOffset + channel])
                        {
                            mismatchedBytes++;
                            pixelMismatch = true;
                        }
                    }
                    comparedPixels++;
                    if (pixelMismatch)
                    {
                        mismatchedPixels++;
                    }
                }
            }

            return new DrawableComparison(comparedPixels, comparedBytes, mismatchedPixels, mismatchedBytes);
        }

        public static void PresentFrame(int width, int height, uint clearColor = 0xFF3366CC)
        {
            _width = width; _height = height;
            StoreLastFrame(width, height, clearColor);

            if (_layer == nint.Zero || CommandQueue == nint.Zero)
            {
                return;
            }

            nint pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                ConfigureLayerOnce(width, height, PixelFormatBgra8Unorm);

                nint drawable = MetalNative.SendObject(_layer, MetalNative.Sel("nextDrawable"));
                if (drawable == nint.Zero)
                {
                    return; // 本帧无可用 drawable，跳过
                }

                nint texture = MetalNative.SendObject(drawable, MetalNative.Sel("texture"));
                nint commandBuffer = MetalNative.SendObject(CommandQueue, MetalNative.Sel("commandBuffer"));
                if (texture == nint.Zero || commandBuffer == nint.Zero)
                {
                    return;
                }

                // MTLRenderPassDescriptor renderPassDescriptor
                nint rpdClass = MetalNative.Class("MTLRenderPassDescriptor");
                nint passDescriptor = MetalNative.SendObject(rpdClass, MetalNative.Sel("renderPassDescriptor"));

                nint attachments = MetalNative.SendObject(passDescriptor, MetalNative.Sel("colorAttachments"));
                nint attachment = MetalNative.SendObject(attachments, MetalNative.Sel("objectAtIndexedSubscript:"), nint.Zero);

                MetalNative.SendVoid(attachment, MetalNative.Sel("setTexture:"), texture);
                MetalNative.SendVoid(attachment, MetalNative.Sel("setLoadAction:"), LoadActionClear);
                MetalNative.SendVoid(attachment, MetalNative.Sel("setStoreAction:"), StoreActionStore);
                MetalNative.SendVoid(attachment, MetalNative.Sel("setClearColor:"), ToMtClearColor(clearColor));

                nint encoder = MetalNative.SendObject(commandBuffer, MetalNative.Sel("renderCommandEncoderWithDescriptor:"), passDescriptor);
                MetalNative.SendVoid(encoder, MetalNative.Sel("endEncoding"));

                MetalNative.SendVoid(commandBuffer, MetalNative.Sel("presentDrawable:"), drawable);
                MetalNative.SendVoid(commandBuffer, MetalNative.Sel("commit"));
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        private static int _layerWidth, _layerHeight;
        private static ulong _layerPixelFormat;

        private static void ConfigureLayerOnce(int width, int height, ulong pixelFormat)
        {
            if (Device == nint.Zero || _layer == nint.Zero)
            {
                return;
            }

            // 分辨率或颜色格式切换时必须更新 drawable，否则 blit 可能写入旧尺寸/格式的纹理。
            if (_layerConfigured && _layerWidth == width && _layerHeight == height && _layerPixelFormat == pixelFormat)
            {
                return;
            }

            MetalNative.SendVoid(_layer, MetalNative.Sel("setDevice:"), Device);
            MetalNative.SendVoid(_layer, MetalNative.Sel("setPixelFormat:"), pixelFormat);
            // PresentTexture 使用 blit encoder 写 drawable。CAMetalLayer 默认为
            // framebufferOnly=YES，此时 drawable texture 不能作为 blit 目标。
            MetalNative.SendVoid(_layer, MetalNative.Sel("setFramebufferOnly:"), (byte)0);
            MetalNative.SendVoid(_layer, MetalNative.Sel("setDrawableSize:"), new CGSize(width, height));
            _layerConfigured = true;
            _layerWidth = width;
            _layerHeight = height;
            _layerPixelFormat = pixelFormat;
        }

        private static MTLClearColor ToMtClearColor(uint rgba) =>
            new(
                ((rgba >> 16) & 0xFF) / 255.0,
                ((rgba >> 8) & 0xFF) / 255.0,
                (rgba & 0xFF) / 255.0,
                ((rgba >> 24) & 0xFF) / 255.0);

        private static void StoreLastFrame(int width, int height, uint clearColor)
        {
            int size = width * height * 4;
            var data = new byte[size];
            byte b = (byte)(clearColor & 0xFF);
            byte g = (byte)((clearColor >> 8) & 0xFF);
            byte r = (byte)((clearColor >> 16) & 0xFF);
            byte a = (byte)((clearColor >> 24) & 0xFF);
            for (int i = 0; i < size; i += 4)
            {
                data[i] = b; data[i + 1] = g; data[i + 2] = r; data[i + 3] = a;
            }
            lock (_lock) { _lastFrameData = data; }
        }

        private static void RecordPresentedFrame(MetalTexture source, nint drawableTexture, int left, int top, int width, int height, bool completed)
        {
            if (Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_FRAME_STATS") != "1" || !completed)
            {
                return;
            }

            byte[] pixels = ReadNativeTexture(drawableTexture, source.Width, source.Height, 0, source.BytesPerPixel);
            if (pixels == null)
            {
                return;
            }

            ulong hash = 1469598103934665603UL;
            int changedPixels = 0;
            lock (_lock)
            {
                for (int index = 0; index < pixels.Length; index++)
                {
                    hash ^= pixels[index];
                    hash *= 1099511628211UL;
                }

                if (_previousPresentedFrame != null)
                {
                    int count = Math.Min(_previousPresentedFrame.Length, pixels.Length);
                    for (int index = 0; index + 3 < count; index += 4)
                    {
                        if (_previousPresentedFrame[index] != pixels[index] ||
                            _previousPresentedFrame[index + 1] != pixels[index + 1] ||
                            _previousPresentedFrame[index + 2] != pixels[index + 2] ||
                            _previousPresentedFrame[index + 3] != pixels[index + 3])
                        {
                            changedPixels++;
                        }
                    }
                }

                _previousPresentedFrame = pixels;
                _presentSequence++;
            }

            string path = Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(path))
            {
                Directory.CreateDirectory(path);
                File.AppendAllText(Path.Combine(path, "frame-sequence.jsonl"), JsonSerializer.Serialize(new
                {
                    frame = _presentSequence,
                    width = source.Width,
                    height = source.Height,
                    copiedSourceRect = new { x = left, y = top, width, height },
                    hash = $"{hash:X16}",
                    changedPixels,
                    gpuCompleted = completed,
                    textureUploads = _textureUploadCount,
                    bufferUploads = _bufferUploadCount,
                }) + Environment.NewLine);
            }

            Console.WriteLine($"[Metal][FrameStats] frame={_presentSequence} hash={hash:X16} changedPixels={changedPixels} textureUploads={_textureUploadCount} bufferUploads={_bufferUploadCount}");
        }

        internal static void RecordTextureUpload() => Interlocked.Increment(ref _textureUploadCount);
        internal static void RecordBufferUpload() => Interlocked.Increment(ref _bufferUploadCount);

        public static void RequestScreenshot(Action<byte[], int, int> callback)
        {
            _screenshotCallback = callback;
        }

        public static byte[] GetLastFrameData()
        {
            lock (_lock) { return _lastFrameData != null ? (byte[])_lastFrameData.Clone() : new byte[_width * _height * 4]; }
        }

        public static (int w, int h) GetFrameSize() => (_width, _height);
    }
}
