using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.Metal
{
    static class MetalTextureDescriptor
    {
        internal const ulong PixelFormatR8Unorm = 10;
        internal const ulong PixelFormatR8G8Unorm = 30;
        internal const ulong PixelFormatR8G8B8A8Unorm = 70;
        internal const ulong PixelFormatBgra8Unorm = 80;
        internal const ulong PixelFormatR16Float = 25;
        internal const ulong PixelFormatR32Float = 55;
        internal const ulong PixelFormatR16G16B16A16Float = 115;
        internal const ulong PixelFormatR32G32B32A32Float = 125;
        internal const ulong PixelFormatStencil8 = 253;
        internal const ulong PixelFormatDepth16Unorm = 250;
        internal const ulong PixelFormatDepth32Float = 252;
        internal const ulong PixelFormatDepth24UnormStencil8 = 255;
        internal const ulong PixelFormatDepth32FloatStencil8 = 260;

        private static byte? _d24s8Supported;

        /// <summary>
        /// Depth24UnormStencil8 仅 Intel/AMD GPU 支持；Apple Silicon (Apple GPU) 上不可渲染，
        /// 用它创建 PSO 会直接失败（表现为渲染管线创建断言/返回空）。Apple GPU 降级到
        /// Depth32FloatStencil8，与 MoltenVK 对 VK_FORMAT_D24_UNORM_S8_UINT 的重映射一致。
        /// </summary>
        internal static bool SupportsDepth24Stencil8
        {
            get
            {
                if (!_d24s8Supported.HasValue)
                {
                    nint device = MetalContext.Device;
                    _d24s8Supported = device == nint.Zero
                        ? (byte)0
                        : MetalNative.SendByte(device, MetalNative.Sel("isDepth24Stencil8PixelFormatSupported"));
                }

                return _d24s8Supported.Value != 0;
            }
        }

        private static ulong ToDepthStencilPixelFormat(Format format)
        {
            // Apple GPU 不支持 D24S8，统一降级 D32FS8；S8UintD24Unorm/X8UintD24Unorm 的
            // 深度分量语义由深度比较承担，浮点深度兼容原有用例。
            if (!SupportsDepth24Stencil8 &&
                format is Format.D24UnormS8Uint or Format.S8UintD24Unorm or Format.X8UintD24Unorm)
            {
                return PixelFormatDepth32FloatStencil8;
            }

            return format switch
            {
                Format.S8Uint => PixelFormatStencil8,
                Format.D16Unorm => PixelFormatDepth16Unorm,
                Format.D32Float => PixelFormatDepth32Float,
                Format.D24UnormS8Uint => PixelFormatDepth24UnormStencil8,
                Format.S8UintD24Unorm or Format.X8UintD24Unorm => PixelFormatDepth24UnormStencil8,
                Format.D32FloatS8Uint => PixelFormatDepth32FloatStencil8,
                _ => PixelFormatDepth32Float,
            };
        }

        public static nint CreateDescriptor(TextureCreateInfo info)
        {
            if (info.Width <= 0 || info.Height <= 0)
            {
                return nint.Zero;
            }

            ulong pixelFormat = ToPixelFormat(info.Format, info.DepthStencilMode);
            ulong mipmapped = info.Levels > 1 ? 1UL : 0UL;
            ulong usage = 1UL | 4UL; // ShaderRead | RenderTarget.

            if (!info.Format.HasDepth && !info.Format.HasStencil)
            {
                usage |= 2UL; // ShaderWrite for color images.
            }

            nint descriptor;
            if (info.Target == Target.Texture3D)
            {
                descriptor = MetalNative.SendObject(
                    MetalNative.Class("MTLTextureDescriptor"),
                    MetalNative.Sel("texture3DDescriptorWithPixelFormat:width:height:depth:mipmapped:"),
                    pixelFormat,
                    (ulong)info.Width,
                    (ulong)info.Height,
                    (ulong)info.Depth,
                    mipmapped);
            }
            else if (info.Target is Target.Texture1D or Target.Texture1DArray or Target.Texture2DArray or Target.Texture2DMultisample or Target.Texture2DMultisampleArray or Target.Cubemap or Target.CubemapArray)
            {
                descriptor = MetalNative.SendObject(MetalNative.Class("MTLTextureDescriptor"), MetalNative.Sel("new"));
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setPixelFormat:"), pixelFormat);
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setWidth:"), (ulong)info.Width);
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setHeight:"), (ulong)Math.Max(1, info.Height));
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setArrayLength:"), (ulong)Math.Max(1, info.GetLayers()));
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setMipmapLevelCount:"), (ulong)(info.Target.IsMultisample ? 1 : Math.Max(1, info.Levels)));
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setTextureType:"), info.Target switch
                {
                    Target.Texture1D => 0UL,
                    Target.Texture1DArray => 1UL,
                    Target.Texture2DArray => 3UL,
                    Target.Texture2DMultisample => 4UL,
                    Target.Texture2DMultisampleArray => 5UL,
                    Target.Cubemap => 6UL,
                    Target.CubemapArray => 7UL,
                    _ => 2UL,
                });
            }
            else
            {
                descriptor = MetalNative.SendObject(
                    MetalNative.Class("MTLTextureDescriptor"),
                    MetalNative.Sel("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
                    pixelFormat,
                    (ulong)info.Width,
                    (ulong)info.Height,
                    mipmapped);
            }

            MetalNative.SendVoid(descriptor, MetalNative.Sel("setUsage:"), usage);
            if (info.Target.IsMultisample)
            {
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setSampleCount:"), (ulong)Math.Max(1, info.Samples));
            }
            return descriptor;
        }

        public static nint CreateTexture(nint device, nint descriptor)
        {
            if (device == nint.Zero || descriptor == nint.Zero)
            {
                return nint.Zero;
            }

            return MetalNative.SendObject(device, MetalNative.Sel("newTextureWithDescriptor:"), descriptor);
        }

        public static ulong ToPixelFormat(Format format, DepthStencilMode depthStencilMode)
        {
            if (format is Format.S8Uint or Format.D16Unorm or Format.D32Float or Format.D24UnormS8Uint or Format.S8UintD24Unorm or Format.X8UintD24Unorm or Format.D32FloatS8Uint)
            {
                return ToDepthStencilPixelFormat(format);
            }

            // 完整 MTLPixelFormat 映射表（对照 MTLPixelFormat.h 枚举值）。
            // 缺省格式绝不能静默落到 BGRA8：会把任意数据按 4 字节 UNORM 解释，直接花屏。
            return format switch
            {
                // 8-bit 单通道
                Format.R8Unorm => PixelFormatR8Unorm,
                Format.R8Snorm => 12UL,
                Format.R8Uint => 13UL,
                Format.R8Sint => 14UL,

                // 16-bit 单通道
                Format.R16Unorm => 20UL,
                Format.R16Snorm => 22UL,
                Format.R16Uint => 23UL,
                Format.R16Sint => 24UL,
                Format.R16Float => PixelFormatR16Float,

                // 32-bit 单通道
                Format.R32Uint => 53UL,
                Format.R32Sint => 54UL,
                Format.R32Float => PixelFormatR32Float,

                // 双通道
                Format.R8G8Unorm => PixelFormatR8G8Unorm,
                Format.R8G8Snorm => 32UL,
                Format.R8G8Uint => 33UL,
                Format.R8G8Sint => 34UL,
                Format.R16G16Unorm => 60UL,
                Format.R16G16Snorm => 62UL,
                Format.R16G16Uint => 63UL,
                Format.R16G16Sint => 64UL,
                Format.R16G16Float => 65UL,
                Format.R32G32Uint => 103UL,
                Format.R32G32Sint => 104UL,
                Format.R32G32Float => 105UL,

                // 三通道（Metal 无原生 8/16/32-bit RGB 颜色格式，走不支持回退）
                Format.R11G11B10Float => 92UL,
                Format.R9G9B9E5Float => 93UL,

                // 四通道 8-bit
                Format.R8G8B8A8Unorm => PixelFormatR8G8B8A8Unorm,
                Format.R8G8B8A8Srgb => 71UL,
                Format.R8G8B8A8Snorm => 72UL,
                Format.R8G8B8A8Uint => 73UL,
                Format.R8G8B8A8Sint => 74UL,
                Format.B8G8R8A8Unorm => PixelFormatBgra8Unorm,
                Format.B8G8R8A8Srgb => 81UL,

                // 打包 16-bit（注意 Metal 分量序与 Vulkan 相反，采样端需要 swizzle 时另行处理）
                Format.R4G4B4A4Unorm => 42UL,   // ABGR4Unorm
                Format.R5G6B5Unorm => 40UL,     // B5G6R5Unorm
                Format.R5G5B5A1Unorm => 43UL,   // BGR5A1Unorm
                Format.R5G5B5X1Unorm => 43UL,

                // 打包 32-bit
                Format.R10G10B10A2Unorm => 90UL,
                Format.R10G10B10A2Uint => 91UL,

                // 四通道 16/32-bit
                Format.R16G16B16A16Unorm => 110UL,
                Format.R16G16B16A16Snorm => 112UL,
                Format.R16G16B16A16Uint => 113UL,
                Format.R16G16B16A16Sint => 114UL,
                Format.R16G16B16A16Float => PixelFormatR16G16B16A16Float,
                Format.R32G32B32A32Uint => 123UL,
                Format.R32G32B32A32Sint => 124UL,
                Format.R32G32B32A32Float => PixelFormatR32G32B32A32Float,

                // BC 压缩
                Format.Bc1RgbaUnorm => 130UL,
                Format.Bc1RgbaSrgb => 131UL,
                Format.Bc2Unorm => 132UL,
                Format.Bc2Srgb => 133UL,
                Format.Bc3Unorm => 134UL,
                Format.Bc3Srgb => 135UL,
                Format.Bc4Unorm => 140UL,
                Format.Bc4Snorm => 141UL,
                Format.Bc5Unorm => 142UL,
                Format.Bc5Snorm => 143UL,
                Format.Bc6HSfloat => 150UL,
                Format.Bc6HUfloat => 151UL,
                Format.Bc7Unorm => 152UL,
                Format.Bc7Srgb => 153UL,

                // ETC2/EAC（Apple GPU 原生支持）
                Format.Etc2RgbUnorm => 180UL,
                Format.Etc2RgbSrgb => 181UL,
                Format.Etc2RgbPtaUnorm => 182UL,
                Format.Etc2RgbPtaSrgb => 183UL,
                Format.Etc2RgbaUnorm => 178UL,
                Format.Etc2RgbaSrgb => 179UL,

                // ASTC LDR（Apple GPU 原生支持）
                Format.Astc4x4Unorm => 204UL,
                Format.Astc5x4Unorm => 205UL,
                Format.Astc5x5Unorm => 206UL,
                Format.Astc6x5Unorm => 207UL,
                Format.Astc6x6Unorm => 208UL,
                Format.Astc8x5Unorm => 210UL,
                Format.Astc8x6Unorm => 211UL,
                Format.Astc8x8Unorm => 212UL,
                Format.Astc10x5Unorm => 213UL,
                Format.Astc10x6Unorm => 214UL,
                Format.Astc10x8Unorm => 215UL,
                Format.Astc10x10Unorm => 216UL,
                Format.Astc12x10Unorm => 217UL,
                Format.Astc12x12Unorm => 218UL,
                Format.Astc4x4Srgb => 186UL,
                Format.Astc5x4Srgb => 187UL,
                Format.Astc5x5Srgb => 188UL,
                Format.Astc6x5Srgb => 189UL,
                Format.Astc6x6Srgb => 190UL,
                Format.Astc8x5Srgb => 192UL,
                Format.Astc8x6Srgb => 193UL,
                Format.Astc8x8Srgb => 194UL,
                Format.Astc10x5Srgb => 195UL,
                Format.Astc10x6Srgb => 196UL,
                Format.Astc10x8Srgb => 197UL,
                Format.Astc10x10Srgb => 198UL,
                Format.Astc12x10Srgb => 199UL,
                Format.Astc12x12Srgb => 200UL,

                // Scaled 格式在 Metal 上无原生表示，用整型近似（能力位后续应上报 false 触发上游转换）
                Format.R8Uscaled or Format.R8Sscaled => 13UL,
                Format.R16Uscaled or Format.R16Sscaled => 23UL,
                Format.R32Uscaled or Format.R32Sscaled => 53UL,
                Format.R8G8Uscaled or Format.R8G8Sscaled => 33UL,
                Format.R16G16Uscaled or Format.R16G16Sscaled => 63UL,
                Format.R32G32Uscaled or Format.R32G32Sscaled => 103UL,
                Format.R8G8B8Uscaled or Format.R8G8B8Sscaled => 73UL,
                Format.R16G16B16Uscaled or Format.R16G16B16Sscaled => 113UL,
                Format.R32G32B32Uscaled or Format.R32G32B32Sscaled => 123UL,
                Format.R8G8B8A8Uscaled or Format.R8G8B8A8Sscaled => 73UL,
                Format.R16G16B16A16Uscaled or Format.R16G16B16A16Sscaled => 113UL,
                Format.R32G32B32A32Uscaled or Format.R32G32B32A32Sscaled => 123UL,

                _ => LogUnsupportedPixelFormat(format),
            };
        }

        private static ulong LogUnsupportedPixelFormat(Format format)
        {
            if (_loggedUnsupported.Add(format))
            {
                Console.WriteLine($"[MetalTexture] 未支持的格式 {format}，回退 BGRA8Unorm（可能显示异常）");
            }

            return PixelFormatBgra8Unorm;
        }

        private static readonly HashSet<Format> _loggedUnsupported = new();
    }
}
