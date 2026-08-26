using Ryujinx.Graphics.GAL;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal
{
    static unsafe class MetalPipelineState
    {
        // Metal Shader Converter runtime contract: vertex-fetch buffers start at index 6.
        internal const int VertexBufferBase = 6;

        // MSC 契约：DXIL TEXCOORDn 顶点输入映射到 Metal [[attribute(11+n)]]
        // （经 metal-shaderconverter 反射验证：texcoord0 → attributeIndex=11）。
        internal const int VertexAttribBase = 11;

        // 着色器使用了 GPU 状态未声明的顶点属性时（Maxwell 默认 (0,0,0,1)），
        // 指向该保留缓冲槽位的常量默认值，满足 Metal "所有输入必须在 descriptor 中" 的约束。
        // Metal 顶点缓冲索引上限为 31（合法值 0..30），因此保留槽位取 30。
        internal const int DefaultAttributeBufferIndex = 30;

        public static nint CreateLibrary(nint device, byte[] metallib)
        {
            if (device == nint.Zero || metallib == null || metallib.Length == 0)
            {
                Console.WriteLine("[Metal][PSO] MTLLibrary 输入为空");
                return nint.Zero;
            }

            fixed (byte* bytes = metallib)
            {
                nint dispatchData = MetalNative.dispatch_data_create(
                    (nint)bytes,
                    (nuint)metallib.Length,
                    nint.Zero,
                    nint.Zero);

                nint error = nint.Zero;
                nint library = MetalNative.SendObject(
                    device,
                    MetalNative.Sel("newLibraryWithData:error:"),
                    dispatchData,
                    (nint)(&error));

                if (library == nint.Zero)
                {
                    Console.WriteLine($"[Metal][PSO] MTLLibrary 创建失败: {DescribeError(error)}");
                }
                else
                {
                    Console.WriteLine($"[Metal][PSO] MTLLibrary 创建成功: {metallib.Length}B handle=0x{library:X}");
                }

                return library;
            }
        }

        public static nint CreateRenderPipeline(
            nint device,
            nint vertexLibrary,
            nint fragmentLibrary,
            nint vertexDescriptor = default,
            string vertexFunc = "main",
            string fragmentFunc = "main",
            ProgramPipelineState? pipelineState = null,
            BlendDescriptor? blendOverride = null,
            uint? colorWriteMaskOverride = null,
            MultisampleDescriptor? multisampleOverride = null,
            bool? logicOperationEnabledOverride = null,
            LogicalOp logicOperationOverride = LogicalOp.Copy)
        {
            if (device == nint.Zero || vertexLibrary == nint.Zero || fragmentLibrary == nint.Zero)
            {
                Console.WriteLine("[Metal][PSO] RenderPipeline 输入句柄为空");
                return nint.Zero;
            }

            nint descriptor = MetalNative.SendObject(MetalNative.Class("MTLRenderPipelineDescriptor"), MetalNative.Sel("new"));
            nint vertexFunction = GetFunction(vertexLibrary, vertexFunc);
            nint fragmentFunction = GetFunction(fragmentLibrary, fragmentFunc);

            if (descriptor == nint.Zero || vertexFunction == nint.Zero || fragmentFunction == nint.Zero)
            {
                Console.WriteLine($"[Metal][PSO] shader function 获取失败: vs={vertexFunction:X} fs={fragmentFunction:X}");
                return nint.Zero;
            }

            MetalNative.SendVoid(descriptor, MetalNative.Sel("setVertexFunction:"), vertexFunction);
            MetalNative.SendVoid(descriptor, MetalNative.Sel("setFragmentFunction:"), fragmentFunction);

            nint createdVertexDescriptor = nint.Zero;
            if (vertexDescriptor != nint.Zero)
            {
                // 着色器可能读取 GPU 状态未声明的顶点属性（Maxwell 默认 (0,0,0,1)），
                // 通过函数反射补齐缺失条目，否则 Metal 验证层会拒绝该管线。
                PatchMissingVertexAttributes(vertexLibrary, vertexDescriptor);
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setVertexDescriptor:"), vertexDescriptor);
            }

            nint colorAttachments = MetalNative.SendObject(descriptor, MetalNative.Sel("colorAttachments"));
            nint colorAttachment = MetalNative.SendObject(colorAttachments, MetalNative.Sel("objectAtIndexedSubscript:"), nint.Zero);
            ulong colorPixelFormat = pipelineState is { AttachmentEnable: var attachmentEnable } && attachmentEnable[0]
                ? MetalTextureDescriptor.ToPixelFormat(pipelineState.Value.AttachmentFormats[0], DepthStencilMode.Depth)
                : MetalContext.PixelFormatBgra8Unorm;
            MetalNative.SendVoid(colorAttachment, MetalNative.Sel("setPixelFormat:"), colorPixelFormat);

            if (pipelineState is { DepthStencilEnable: true })
            {
                ulong depthStencilPixelFormat = MetalTextureDescriptor.ToPixelFormat(pipelineState.Value.DepthStencilFormat, DepthStencilMode.Depth);
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setDepthAttachmentPixelFormat:"), depthStencilPixelFormat);
                if (pipelineState.Value.DepthStencilFormat.HasStencil)
                {
                    MetalNative.SendVoid(descriptor, MetalNative.Sel("setStencilAttachmentPixelFormat:"), depthStencilPixelFormat);
                }
            }

            if (pipelineState is { SamplesCount: > 1 })
            {
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setRasterSampleCount:"), (ulong)pipelineState.Value.SamplesCount);
            }

            if (pipelineState.HasValue || blendOverride.HasValue || colorWriteMaskOverride.HasValue)
            {
                BlendDescriptor blend = blendOverride ?? (pipelineState.HasValue ? pipelineState.Value.BlendDescriptors[0] : default);
                MetalNative.SendVoid(colorAttachment, MetalNative.Sel("setBlendingEnabled:"), blend.Enable ? (byte)1 : (byte)0);
                if (blend.Enable)
                {
                    MetalNative.SendVoid(colorAttachment, MetalNative.Sel("setRgbBlendOperation:"), ToMetalBlendOperation(blend.ColorOp));
                    MetalNative.SendVoid(colorAttachment, MetalNative.Sel("setAlphaBlendOperation:"), ToMetalBlendOperation(blend.AlphaOp));
                    MetalNative.SendVoid(colorAttachment, MetalNative.Sel("setSourceRGBBlendFactor:"), ToMetalBlendFactor(blend.ColorSrcFactor));
                    MetalNative.SendVoid(colorAttachment, MetalNative.Sel("setDestinationRGBBlendFactor:"), ToMetalBlendFactor(blend.ColorDstFactor));
                    MetalNative.SendVoid(colorAttachment, MetalNative.Sel("setSourceAlphaBlendFactor:"), ToMetalBlendFactor(blend.AlphaSrcFactor));
                    MetalNative.SendVoid(colorAttachment, MetalNative.Sel("setDestinationAlphaBlendFactor:"), ToMetalBlendFactor(blend.AlphaDstFactor));
                }

                uint colorWriteMask = colorWriteMaskOverride ?? (pipelineState.HasValue ? pipelineState.Value.ColorWriteMask[0] : 0xF);
                if (colorWriteMaskOverride.HasValue || colorWriteMask != 0)
                {
                    MetalNative.SendVoid(colorAttachment, MetalNative.Sel("setWriteMask:"), colorWriteMask & 0xF);
                }

                bool logicEnabled = logicOperationEnabledOverride ?? (pipelineState.HasValue && pipelineState.Value.LogicOpEnable);
                if (logicEnabled)
                {
                    MetalNative.SendVoid(descriptor, MetalNative.Sel("setLogicOperationEnabled:"), (byte)1);
                    MetalNative.SendVoid(descriptor, MetalNative.Sel("setLogicOperation:"), ToMetalLogicOperation(logicOperationEnabledOverride.HasValue ? logicOperationOverride : pipelineState.Value.LogicOp));
                }
            }

            if (multisampleOverride.HasValue)
            {
                MultisampleDescriptor multisample = multisampleOverride.Value;
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setAlphaToCoverageEnabled:"), multisample.AlphaToCoverageEnable ? (byte)1 : (byte)0);
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setAlphaToOneEnabled:"), multisample.AlphaToOneEnable ? (byte)1 : (byte)0);
                // MTLRenderPipelineDescriptor on macOS exposes alpha-to-coverage and
                // alpha-to-one, but not the GAL dither extension. Preserve it in capture
                // metadata without sending an unsupported Objective-C selector.
            }

            nint error = nint.Zero;
            nint pipeline = MetalNative.SendObject(
                device,
                MetalNative.Sel("newRenderPipelineStateWithDescriptor:error:"),
                descriptor,
                (nint)(&error));

            if (createdVertexDescriptor != nint.Zero)
            {
                MetalNative.SendVoid(createdVertexDescriptor, MetalNative.Sel("release"));
            }

            if (pipeline == nint.Zero)
            {
                Console.WriteLine($"[Metal][PSO] RenderPipeline 创建失败: {DescribeError(error)}");
            }
            else
            {
                Console.WriteLine($"[Metal][PSO] RenderPipeline 创建成功: handle=0x{pipeline:X}");
            }

            return pipeline;
        }

        /// <summary>
        /// Maxwell 允许着色器读取未在 GPU 状态中声明的顶点属性（固定默认值 (0,0,0,1)），
        /// Metal 则要求着色器每个顶点输入都在 MTLVertexDescriptor 中有条目。
        /// 通过顶点函数反射补齐缺失属性：指向保留槽位 DefaultAttributeBufferIndex 的常量默认缓冲。
        /// </summary>
        private static void PatchMissingVertexAttributes(nint vertexLibrary, nint vertexDescriptor)
        {
            nint function = GetFunction(vertexLibrary, "main");
            if (function == nint.Zero)
            {
                return;
            }

            nint attributes = MetalNative.SendObject(function, MetalNative.Sel("vertexAttributes"));
            MetalNative.SendVoid(function, MetalNative.Sel("release"));
            if (attributes == nint.Zero)
            {
                return;
            }

            ulong count = MetalNative.SendULong(attributes, MetalNative.Sel("count"));
            bool patched = false;
            Console.WriteLine($"[Metal][诊断] Patch: count={count} vd=0x{vertexDescriptor:X}");

            for (ulong index = 0; index < count; index++)
            {
                nint attribute = MetalNative.SendObject(attributes, MetalNative.Sel("objectAtIndexedSubscript:"), index);
                if (attribute == nint.Zero)
                {
                    continue;
                }

                byte active = MetalNative.SendByte(attribute, MetalNative.Sel("isActive"));
                ulong attributeIndex = MetalNative.SendULong(attribute, MetalNative.Sel("attributeIndex"));
                Console.WriteLine($"[Metal][诊断] Patch: [{index}] attr=0x{attribute:X} active={active} idx={attributeIndex}");
                if (active == 0)
                {
                    continue;
                }
                if (attributeIndex < (ulong)VertexAttribBase)
                {
                    // 11..15 之前的槽位属于 MSC 系统语义，由编译器契约处理。
                    continue;
                }

                // 已声明的属性（由调用方写入 descriptor）无需处理；这里无法直接枚举
                // descriptor 现有条目，因此对全部着色器属性统一覆写默认条目会破坏
                // 正确的声明。改为：仅当 descriptor 对应槽位 format 为 Invalid(0) 时补默认。
                nint attributeDescriptor = MetalNative.SendObject(
                    MetalNative.SendObject(vertexDescriptor, MetalNative.Sel("attributes")),
                    MetalNative.Sel("objectAtIndexedSubscript:"),
                    attributeIndex);
                if (attributeDescriptor == nint.Zero)
                {
                    continue;
                }

                ulong existingFormat = vertexDescriptor != nint.Zero
                    ? MetalNative.SendULong(attributeDescriptor, MetalNative.Sel("format"))
                    : 0;
                if (existingFormat != 0)
                {
                    continue;
                }

                MetalNative.SendVoid(attributeDescriptor, MetalNative.Sel("setFormat:"), 31UL); // Float4
                MetalNative.SendVoid(attributeDescriptor, MetalNative.Sel("setOffset:"), 0UL);
                MetalNative.SendVoid(attributeDescriptor, MetalNative.Sel("setBufferIndex:"), (ulong)DefaultAttributeBufferIndex);
                patched = true;
            }

            if (patched && vertexDescriptor != nint.Zero)
            {
                nint layout = MetalNative.SendObject(
                    MetalNative.SendObject(vertexDescriptor, MetalNative.Sel("layouts")),
                    MetalNative.Sel("objectAtIndexedSubscript:"),
                    (ulong)DefaultAttributeBufferIndex);
                if (layout != nint.Zero)
                {
                    MetalNative.SendVoid(layout, MetalNative.Sel("setStride:"), 16UL);
                    MetalNative.SendVoid(layout, MetalNative.Sel("setStepFunction:"), 1UL); // perVertex
                    MetalNative.SendVoid(layout, MetalNative.Sel("setStepRate:"), 1UL);
                }
            }
        }

        /// <summary>
        /// 创建 (0,0,0,1) 常量默认顶点属性缓冲，供未声明属性的着色器读取。
        /// </summary>
        public static nint CreateDefaultAttributeBuffer(nint device)
        {
            if (device == nint.Zero)
            {
                return nint.Zero;
            }

            Span<float> defaultValue = [0f, 0f, 0f, 1f];
            fixed (float* pointer = defaultValue)
            {
                return MetalNative.SendObject(
                    device,
                    MetalNative.Sel("newBufferWithBytes:length:options:"),
                    (nint)pointer,
                    (ulong)(sizeof(float) * defaultValue.Length),
                    0UL);
            }
        }

        public static nint CreateVertexDescriptor(VertexAttribDescriptor[] attributes, VertexBufferDescriptor[] buffers)
        {
            if (attributes == null || buffers == null || attributes.Length == 0)
            {
                return nint.Zero;
            }

            nint descriptor = MetalNative.SendObject(MetalNative.Class("MTLVertexDescriptor"), MetalNative.Sel("new"));
            nint attributeArray = MetalNative.SendObject(descriptor, MetalNative.Sel("attributes"));
            nint layoutArray = MetalNative.SendObject(descriptor, MetalNative.Sel("layouts"));

            for (int index = 0; index < attributes.Length; index++)
            {
                VertexAttribDescriptor attribute = attributes[index];
                if ((uint)index >= (uint)buffers.Length || attribute.IsZero)
                {
                    continue;
                }

                // 数组下标即 GAL 属性位置（ProgramPipelineState.VertexAttribs 按位置存放）。
                // 跳过 IsZero 条目后必须仍按原始位置写入，否则稀疏布局会错位绑定。
                int metalAttributeIndex = VertexAttribBase + index;
                nint attributeDescriptor = MetalNative.SendObject(attributeArray, MetalNative.Sel("objectAtIndexedSubscript:"), (ulong)metalAttributeIndex);
                MetalNative.SendVoid(attributeDescriptor, MetalNative.Sel("setFormat:"), ToVertexFormat(attribute.Format));
                MetalNative.SendVoid(attributeDescriptor, MetalNative.Sel("setOffset:"), (ulong)Math.Max(0, attribute.Offset));
                MetalNative.SendVoid(attributeDescriptor, MetalNative.Sel("setBufferIndex:"), (ulong)(VertexBufferBase + attribute.BufferIndex));
            }

            for (int index = 0; index < buffers.Length; index++)
            {
                VertexBufferDescriptor buffer = buffers[index];
                nint layoutDescriptor = MetalNative.SendObject(
                    layoutArray,
                    MetalNative.Sel("objectAtIndexedSubscript:"),
                    (ulong)(VertexBufferBase + index));
                MetalNative.SendVoid(layoutDescriptor, MetalNative.Sel("setStride:"), (ulong)Math.Max(0, buffer.Stride));
                MetalNative.SendVoid(layoutDescriptor, MetalNative.Sel("setStepFunction:"), buffer.Divisor > 0 ? 2UL : 1UL);
                MetalNative.SendVoid(layoutDescriptor, MetalNative.Sel("setStepRate:"), (ulong)Math.Max(1, buffer.Divisor));
            }

            return descriptor;
        }

        private static ulong ToVertexFormat(Format format) => format switch
        {
            // Float formats（对照 MTLVertexFormat.h：Float=28..Float4=31, Half=53/Half2=25/Half3=26/Half4=27）
            Format.R32Float => 28,
            Format.R32G32Float => 29,
            Format.R32G32B32Float => 30,
            Format.R32G32B32A32Float => 31,
            Format.R16Float => 53,
            Format.R16G16Float => 25,
            Format.R16G16B16Float => 26,
            Format.R16G16B16A16Float => 27,

            // 8-bit integer and normalized formats. These values are MTLVertexFormat,
            // not texture pixel-format values; using UChar4Normalized for RGBA8 is
            // essential because Celeste's vertex color is packed UNORM data.
            Format.R8Uint => 45,
            Format.R8G8Uint => 1,
            Format.R8G8B8Uint => 2,
            Format.R8G8B8A8Uint => 3,
            Format.R8Unorm => 47,
            Format.R8Snorm => 48,
            Format.R8G8Unorm => 7,
            Format.R8G8Snorm => 10,
            Format.R8G8B8Unorm => 8,
            Format.R8G8B8Snorm => 11,
            Format.R8G8B8A8Unorm => 9,
            Format.R8G8B8A8Snorm => 12,
            Format.R8Sint => 46,
            Format.R8G8Sint => 4,
            Format.R8G8B8Sint => 5,
            Format.R8G8B8A8Sint => 6,

            // 16/32-bit integer formats（UShort=49..UShort4=15, Short=50..Short4=18,
            // UInt=36..UInt4=39, Int=32..Int4=35）
            Format.R16Uint => 49,
            Format.R16G16Uint => 13,
            Format.R16G16B16Uint => 14,
            Format.R16G16B16A16Uint => 15,
            Format.R16Sint => 50,
            Format.R16G16Sint => 16,
            Format.R16G16B16Sint => 17,
            Format.R16G16B16A16Sint => 18,
            Format.R32Uint => 36,
            Format.R32G32Uint => 37,
            Format.R32G32B32Uint => 38,
            Format.R32G32B32A32Uint => 39,
            Format.R32Sint => 32,
            Format.R32G32Sint => 33,
            Format.R32G32B32Sint => 34,
            Format.R32G32B32A32Sint => 35,

            // 打包归一化格式
            Format.R10G10B10A2Unorm => 41, // UInt1010102Normalized
            Format.R10G10B10A2Snorm => 40, // Int1010102Normalized（Metal 无 Snorm 变体，近似）

            // Scaled 格式无原生顶点表示，用整型近似（能力位上报 false 后由上游转换）
            Format.R8Uscaled or Format.R8Sscaled => 45,
            Format.R16Uscaled or Format.R16Sscaled => 49,
            Format.R32Uscaled or Format.R32Sscaled => 36,
            Format.R8G8Uscaled or Format.R8G8Sscaled => 1,
            Format.R16G16Uscaled or Format.R16G16Sscaled => 13,
            Format.R32G32Uscaled or Format.R32G32Sscaled => 37,
            Format.R8G8B8Uscaled or Format.R8G8B8Sscaled => 2,
            Format.R16G16B16Uscaled or Format.R16G16B16Sscaled => 14,
            Format.R32G32B32Uscaled or Format.R32G32B32Sscaled => 38,
            Format.R8G8B8A8Uscaled or Format.R8G8B8A8Sscaled => 3,
            Format.R16G16B16A16Uscaled or Format.R16G16B16A16Sscaled => 15,
            Format.R32G32B32A32Uscaled or Format.R32G32B32A32Sscaled => 39,
            _ => 31,
        };

        public static nint CreateDepthStencilState(
            nint device,
            DepthTestDescriptor depthTest,
            StencilTestDescriptor stencilTest)
        {
            if (device == nint.Zero || (!depthTest.TestEnable && !depthTest.WriteEnable && !stencilTest.TestEnable))
            {
                return nint.Zero;
            }

            nint descriptor = MetalNative.SendObject(MetalNative.Class("MTLDepthStencilDescriptor"), MetalNative.Sel("new"));
            if (descriptor == nint.Zero)
            {
                return nint.Zero;
            }

            MetalNative.SendVoid(
                descriptor,
                MetalNative.Sel("setDepthCompareFunction:"),
                depthTest.TestEnable ? ToMetalCompareFunction(depthTest.Func) : 7UL);
            MetalNative.SendVoid(descriptor, MetalNative.Sel("setDepthWriteEnabled:"), depthTest.WriteEnable ? (byte)1 : (byte)0);

            if (stencilTest.TestEnable)
            {
                nint front = CreateStencilDescriptor(
                    stencilTest.FrontFunc,
                    stencilTest.FrontSFail,
                    stencilTest.FrontDpFail,
                    stencilTest.FrontDpPass,
                    stencilTest.FrontFuncMask,
                    stencilTest.FrontMask);
                nint back = CreateStencilDescriptor(
                    stencilTest.BackFunc,
                    stencilTest.BackSFail,
                    stencilTest.BackDpFail,
                    stencilTest.BackDpPass,
                    stencilTest.BackFuncMask,
                    stencilTest.BackMask);

                MetalNative.SendVoid(descriptor, MetalNative.Sel("setFrontFaceStencil:"), front);
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setBackFaceStencil:"), back);
                if (front != nint.Zero)
                {
                    MetalNative.SendVoid(front, MetalNative.Sel("release"));
                }
                if (back != nint.Zero)
                {
                    MetalNative.SendVoid(back, MetalNative.Sel("release"));
                }
            }

            nint state = MetalNative.SendObject(device, MetalNative.Sel("newDepthStencilStateWithDescriptor:"), descriptor);
            MetalNative.SendVoid(descriptor, MetalNative.Sel("release"));
            return state;
        }

        private static nint CreateStencilDescriptor(
            CompareOp compare,
            StencilOp stencilFail,
            StencilOp depthFail,
            StencilOp depthPass,
            int readMask,
            int writeMask)
        {
            nint descriptor = MetalNative.SendObject(MetalNative.Class("MTLStencilDescriptor"), MetalNative.Sel("new"));
            if (descriptor == nint.Zero)
            {
                return nint.Zero;
            }

            MetalNative.SendVoid(descriptor, MetalNative.Sel("setStencilCompareFunction:"), ToMetalCompareFunction(compare));
            MetalNative.SendVoid(descriptor, MetalNative.Sel("setStencilFailureOperation:"), ToMetalStencilOperation(stencilFail));
            MetalNative.SendVoid(descriptor, MetalNative.Sel("setDepthFailureOperation:"), ToMetalStencilOperation(depthFail));
            MetalNative.SendVoid(descriptor, MetalNative.Sel("setDepthStencilPassOperation:"), ToMetalStencilOperation(depthPass));
            MetalNative.SendVoid(descriptor, MetalNative.Sel("setReadMask:"), unchecked((ulong)readMask));
            MetalNative.SendVoid(descriptor, MetalNative.Sel("setWriteMask:"), unchecked((ulong)writeMask));
            return descriptor;
        }

        private static ulong ToMetalStencilOperation(StencilOp op) => op switch
        {
            StencilOp.Zero or StencilOp.ZeroGl => 1,
            StencilOp.Replace or StencilOp.ReplaceGl => 2,
            StencilOp.IncrementAndClamp or StencilOp.IncrementAndClampGl => 3,
            StencilOp.DecrementAndClamp or StencilOp.DecrementAndClampGl => 4,
            StencilOp.Invert or StencilOp.InvertGl => 5,
            StencilOp.IncrementAndWrap or StencilOp.IncrementAndWrapGl => 6,
            StencilOp.DecrementAndWrap or StencilOp.DecrementAndWrapGl => 7,
            _ => 0,
        };

        private static ulong ToMetalCompareFunction(CompareOp op) => op switch
        {
            CompareOp.Never or CompareOp.NeverGl => 0,
            CompareOp.Less or CompareOp.LessGl => 1,
            CompareOp.Equal or CompareOp.EqualGl => 2,
            CompareOp.LessOrEqual or CompareOp.LessOrEqualGl => 3,
            CompareOp.Greater or CompareOp.GreaterGl => 4,
            CompareOp.NotEqual or CompareOp.NotEqualGl => 5,
            CompareOp.GreaterOrEqual or CompareOp.GreaterOrEqualGl => 6,
            _ => 7,
        };

        private static ulong ToMetalBlendOperation(BlendOp op) => op switch
        {
            BlendOp.Subtract or BlendOp.SubtractGl => 1,
            BlendOp.ReverseSubtract or BlendOp.ReverseSubtractGl => 2,
            BlendOp.Minimum or BlendOp.MinimumGl => 3,
            BlendOp.Maximum or BlendOp.MaximumGl => 4,
            _ => 0,
        };

        private static ulong ToMetalLogicOperation(LogicalOp op) => op switch
        {
            LogicalOp.Clear => 0,
            LogicalOp.Set => 1,
            LogicalOp.Copy => 2,
            LogicalOp.CopyInverted => 3,
            LogicalOp.Noop => 4,
            LogicalOp.Invert => 5,
            LogicalOp.And => 6,
            LogicalOp.Nand => 7,
            LogicalOp.Or => 8,
            LogicalOp.Nor => 9,
            LogicalOp.Xor => 10,
            LogicalOp.Equiv => 11,
            LogicalOp.AndReverse => 12,
            LogicalOp.AndInverted => 13,
            LogicalOp.OrReverse => 14,
            LogicalOp.OrInverted => 15,
            _ => 2,
        };

        private static ulong ToMetalBlendFactor(BlendFactor factor) => factor switch
        {
            BlendFactor.Zero or BlendFactor.ZeroGl => 0,
            BlendFactor.One or BlendFactor.OneGl => 1,
            BlendFactor.SrcColor or BlendFactor.SrcColorGl => 2,
            BlendFactor.OneMinusSrcColor or BlendFactor.OneMinusSrcColorGl => 3,
            BlendFactor.SrcAlpha or BlendFactor.SrcAlphaGl => 4,
            BlendFactor.OneMinusSrcAlpha or BlendFactor.OneMinusSrcAlphaGl => 5,
            BlendFactor.DstAlpha or BlendFactor.DstAlphaGl => 6,
            BlendFactor.OneMinusDstAlpha or BlendFactor.OneMinusDstAlphaGl => 7,
            BlendFactor.DstColor or BlendFactor.DstColorGl => 8,
            BlendFactor.OneMinusDstColor or BlendFactor.OneMinusDstColorGl => 9,
            BlendFactor.SrcAlphaSaturate or BlendFactor.SrcAlphaSaturateGl => 10,
            BlendFactor.ConstantColor => 11,
            BlendFactor.OneMinusConstantColor => 12,
            BlendFactor.ConstantAlpha => 13,
            BlendFactor.OneMinusConstantAlpha => 14,
            BlendFactor.Src1Color or BlendFactor.Src1ColorGl => 15,
            BlendFactor.OneMinusSrc1Color or BlendFactor.OneMinusSrc1ColorGl => 16,
            BlendFactor.Src1Alpha or BlendFactor.Src1AlphaGl => 17,
            BlendFactor.OneMinusSrc1Alpha or BlendFactor.OneMinusSrc1AlphaGl => 18,
            _ => 1,
        };

        public static nint CreateComputePipeline(nint device, nint library, string functionName = "main")
        {
            if (device == nint.Zero || library == nint.Zero)
            {
                return nint.Zero;
            }

            nint function = GetFunction(library, functionName);
            if (function == nint.Zero)
            {
                Console.WriteLine($"[Metal][PSO] compute function 获取失败: {functionName}");
                return nint.Zero;
            }

            nint error = nint.Zero;
            nint pipeline = MetalNative.SendObject(
                device,
                MetalNative.Sel("newComputePipelineStateWithFunction:error:"),
                function,
                (nint)(&error));

            if (pipeline == nint.Zero)
            {
                Console.WriteLine($"[Metal][PSO] ComputePipeline 创建失败: {DescribeError(error)}");
            }

            return pipeline;
        }

        private static nint GetFunction(nint library, string name)
        {
            nint nsName = CreateNSString(name);
            return MetalNative.SendObject(library, MetalNative.Sel("newFunctionWithName:"), nsName);
        }

        private static nint CreateNSString(string value)
        {
            nint utf8 = Marshal.StringToCoTaskMemUTF8(value);
            try
            {
                return MetalNative.SendObject(
                    MetalNative.Class("NSString"),
                    MetalNative.Sel("stringWithUTF8String:"),
                    utf8);
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8);
            }
        }

        private static string DescribeError(nint error)
        {
            if (error == nint.Zero)
            {
                return "未知错误（NSError 为空）";
            }

            nint description = MetalNative.SendObject(error, MetalNative.Sel("localizedDescription"));
            nint utf8 = MetalNative.SendObject(description, MetalNative.Sel("UTF8String"));
            return Marshal.PtrToStringUTF8(utf8) ?? "NSError 无描述";
        }
    }
}
