using Ryujinx.Common;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal
{
    class MetalTexture : ITexture
    {
        private readonly TextureCreateInfo _info;
        private readonly MetalTexture _parent;
        private readonly bool _ownsTexture;
        private readonly object _lock = new();
        private MemoryOwner<byte> _owner;
        private nint _texture;

        public MetalTexture(TextureCreateInfo info)
        {
            _info = info;
            _ownsTexture = true;

            if (MetalContext.IsAvailable)
            {
                nint pool = MetalNative.objc_autoreleasePoolPush();
                try
                {
                    nint descriptor = MetalTextureDescriptor.CreateDescriptor(info);
                    _texture = MetalTextureDescriptor.CreateTexture(MetalContext.Device, descriptor);
                }
                finally
                {
                    MetalNative.objc_autoreleasePoolPop(pool);
                }
            }

            Console.WriteLine($"[MetalTexture] 创建: {info.Width}x{info.Height} {info.Format} Levels={info.Levels} texture=0x{_texture:X}");
        }

        private MetalTexture(TextureCreateInfo info, nint texture, MetalTexture parent)
        {
            _info = info;
            _texture = texture;
            _parent = parent;
            _ownsTexture = false;
        }

        public int Width => _info.Width;
        public int Height => _info.Height;
        internal Format Format => _info.Format;
        internal int BytesPerPixel => _info.BytesPerPixel;
        internal int Levels => _info.Levels;
        internal nint NativeTexture => _texture;
        internal ulong NativeResourceId => _texture == nint.Zero ? 0UL : MetalNative.SendULong(_texture, MetalNative.Sel("gpuResourceID"));

        public void CopyTo(ITexture destination, int firstLayer, int firstLevel)
        {
            if (destination is not MetalTexture dst || dst.NativeTexture == nint.Zero)
            {
                return;
            }

            int levels = Math.Min(Levels, dst.Levels);
            int layers = Math.Min(Math.Max(1, _info.GetDepthOrLayers()), Math.Max(1, dst._info.GetDepthOrLayers()));
            for (int level = 0; level < levels; level++)
            {
                for (int layer = 0; layer < layers; layer++)
                {
                    BlitCopy(dst, layer + firstLayer, layer, level + firstLevel, level);
                }
            }
        }

        public void CopyTo(ITexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel)
        {
            if (destination is MetalTexture dst && dst.NativeTexture != nint.Zero)
            {
                BlitCopy(dst, srcLayer, dstLayer, srcLevel, dstLevel);
            }
        }

        public void CopyTo(ITexture destination, Extents2D srcRegion, Extents2D dstRegion, bool linearFilter)
        {
            if (destination is not MetalTexture dst || dst.NativeTexture == nint.Zero)
            {
                return;
            }

            // blit 编码器只能 1:1 复制；尺寸不一致时复制对齐交集并记录（缩放走 helper 管线后续补齐）。
            int srcWidth = srcRegion.X2 - srcRegion.X1;
            int srcHeight = srcRegion.Y2 - srcRegion.Y1;
            int dstWidth = dstRegion.X2 - dstRegion.X1;
            int dstHeight = dstRegion.Y2 - dstRegion.Y1;
            int width = Math.Min(srcWidth, dstWidth);
            int height = Math.Min(srcHeight, dstHeight);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (width != srcWidth || height != srcHeight)
            {
                Console.WriteLine($"[MetalTexture] 缩放 CopyTo 暂按 1:1 复制 {srcWidth}x{srcHeight}->{dstWidth}x{dstHeight}");
            }

            MetalContext.BlitRegion(
                NativeTexture,
                dst.NativeTexture,
                new MTLOrigin((ulong)Math.Max(0, srcRegion.X1), (ulong)Math.Max(0, srcRegion.Y1), 0),
                new MTLOrigin((ulong)Math.Max(0, dstRegion.X1), (ulong)Math.Max(0, dstRegion.Y1), 0),
                new MTLSize((ulong)width, (ulong)height, 1));
        }

        public void CopyTo(BufferRange range, int layer, int level, int stride)
        {
            if (_texture == nint.Zero)
            {
                return;
            }

            int width = Math.Max(1, Width >> level);
            int height = Math.Max(1, Height >> level);
            MetalContext.ReadTextureIntoBuffer(this, range, layer, level, width, height, stride);
        }

        private void BlitCopy(MetalTexture dst, int srcLayer, int dstLayer, int srcLevel, int dstLevel)
        {
            if (_texture == nint.Zero || dst.NativeTexture == nint.Zero)
            {
                return;
            }

            int width = Math.Max(1, Math.Min(Width >> srcLevel, dst.Width >> dstLevel));
            int height = Math.Max(1, Math.Min(Height >> srcLevel, dst.Height >> dstLevel));
            MetalContext.BlitRegionFull(
                _texture,
                dst.NativeTexture,
                (ulong)Math.Max(0, srcLayer),
                (ulong)Math.Max(0, dstLayer),
                (ulong)Math.Max(0, srcLevel),
                (ulong)Math.Max(0, dstLevel),
                (ulong)width,
                (ulong)height);
        }

        public ITexture CreateView(TextureCreateInfo info, int firstLayer, int firstLevel)
        {
            // 保持父纹理引用，避免 TextureView 被销毁后底层 MTLTexture 失效。
            nint view = _texture;
            if (_texture != nint.Zero && (firstLayer != 0 || firstLevel != 0 || info.Target != _info.Target))
            {
                int levelCount = Math.Max(1, Math.Min(info.Levels, _info.Levels - firstLevel));
                int layerCount = Math.Max(1, Math.Min(info.GetDepthOrLayers(), _info.GetDepthOrLayers() - firstLayer));
                view = MetalNative.SendObject(
                    _texture,
                    MetalNative.Sel("newTextureViewWithPixelFormat:textureType:levels:slices:"),
                    MetalTextureDescriptor.ToPixelFormat(info.Format, info.DepthStencilMode),
                    info.Target switch
                    {
                        Target.Texture1D => 0UL,
                        Target.Texture1DArray => 1UL,
                        Target.Texture2DArray => 3UL,
                        Target.Texture2DMultisample => 4UL,
                        Target.Texture2DMultisampleArray => 5UL,
                        Target.Cubemap => 6UL,
                        Target.CubemapArray => 7UL,
                        Target.Texture3D => 8UL,
                        _ => 2UL,
                    },
                    new MTLRange((ulong)Math.Max(0, firstLevel), (ulong)levelCount),
                    new MTLRange((ulong)Math.Max(0, firstLayer), (ulong)layerCount));
            }

            return new MetalTexture(info, view, this);
        }

        public unsafe PinnedSpan<byte> GetData()
        {
            return GetData(0, 0);
        }

        public unsafe PinnedSpan<byte> GetData(int layer, int level)
        {
            lock (_lock)
            {
                if (_texture != nint.Zero && !_info.IsCompressed && !_info.Format.HasDepth && !_info.Format.HasStencil)
                {
                    byte[] data = MetalContext.ReadTexture(this, layer, level, _info.BytesPerPixel);
                    if (data != null)
                    {
                        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
                        return new PinnedSpan<byte>(
                            handle.AddrOfPinnedObject().ToPointer(),
                            data.Length,
                            handle.Free);
                    }
                }

                if (_owner != null)
                {
                    return PinnedSpan<byte>.UnsafeFromSpan(_owner.Memory.Span);
                }

                return new PinnedSpan<byte>();
            }
        }

        public void SetData(MemoryOwner<byte> data)
        {
            SetDataInternal(data, 0, 0, 0, _info.Width, _info.Height, 0);
        }

        public void SetData(MemoryOwner<byte> data, int layer, int level)
        {
            int width = Math.Max(1, _info.Width >> level);
            int height = Math.Max(1, _info.Height >> level);
            SetDataInternal(data, layer, 0, 0, width, height, level);
        }

        public void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region)
        {
            SetDataInternal(data, layer, region.X, region.Y, region.Width, region.Height, level);
        }

        private unsafe void SetDataInternal(MemoryOwner<byte> data, int layer, int x, int y, int width, int height, int level)
        {
            lock (_lock)
            {
                // Metal 禁止 CPU 直接向 depth/stencil 纹理 replaceRegion（验证层会 abort）。
                // 深度初值依赖 ClearRenderTargetDepthStencil；跳过无效上传。
                if (_info.Format.HasDepth || _info.Format.HasStencil)
                {
                    Console.WriteLine($"[MetalTexture] 跳过 depth/stencil 上传: {Width}x{Height} {Format}");
                    _owner?.Dispose();
                    _owner = data;
                    return;
                }

                if (_texture != nint.Zero && width > 0 && height > 0 && data.Length > 0)
                {
                    // 纹理可能已被本帧命令引用：必要时先提交帧命令，再结束活动编码器。
                    MetalContext.NotifyTextureWrite(_texture);
                    int levelWidth = Math.Max(1, _info.Width >> level);
                    int levelHeight = Math.Max(1, _info.Height >> level);
                    int layerCount = Math.Max(1, _info.GetDepthOrLayers());
                    int clampedLayer = Math.Clamp(layer, 0, layerCount - 1);
                    int clampedX = Math.Clamp(x, 0, Math.Max(0, levelWidth - 1));
                    int clampedY = Math.Clamp(y, 0, Math.Max(0, levelHeight - 1));
                    int clampedWidth = Math.Min(width, levelWidth - clampedX);
                    int clampedHeight = Math.Min(height, levelHeight - clampedY);
                    if (clampedWidth <= 0 || clampedHeight <= 0)
                    {
                        _owner?.Dispose();
                        _owner = data;
                        return;
                    }

                    if (_info.IsCompressed)
                    {
                        // 压缩纹理的 region 以块为单位，bytesPerRow 以字节/块行为单位。
                        int blockWidth = Math.Max(1, _info.BlockWidth);
                        int blockHeight = Math.Max(1, _info.BlockHeight);
                        int bytesPerBlock = Math.Max(1, _info.BytesPerPixel);

                        int regionBlocksWide = BitUtils.DivRoundUp(clampedX + clampedWidth, blockWidth) - clampedX / blockWidth;
                        int regionBlocksHigh = BitUtils.DivRoundUp(clampedY + clampedHeight, blockHeight) - clampedY / blockHeight;
                        int originBlockX = clampedX / blockWidth;
                        int originBlockY = clampedY / blockHeight;
                        int compressedRowBytes = regionBlocksWide * bytesPerBlock;

                        MTLRegion compressedRegion = new(
                            new MTLOrigin((ulong)originBlockX, (ulong)originBlockY, 0),
                            new MTLSize((ulong)regionBlocksWide, (ulong)regionBlocksHigh, 1));

                        fixed (byte* ptr = data.Memory.Span)
                        {
                            MetalNative.SendVoid(
                                _texture,
                                MetalNative.Sel("replaceRegion:mipmapLevel:withBytes:bytesPerRow:"),
                                compressedRegion,
                                (ulong)level,
                                (nint)ptr,
                                (ulong)compressedRowBytes);
                            MetalContext.RecordTextureUpload();
                        }
                    }
                    else
                    {
                        int rowBytes = Math.Max(1, clampedWidth * _info.BytesPerPixel);

                        MTLRegion region = new(
                            new MTLOrigin((ulong)clampedX, (ulong)clampedY, 0),
                            new MTLSize((ulong)clampedWidth, (ulong)clampedHeight, 1));

                        fixed (byte* ptr = data.Memory.Span)
                        {
                            if (_info.Target.HasDepthOrLayers && _info.Target != Target.Texture3D)
                            {
                                MetalNative.SendVoid(
                                    _texture,
                                    MetalNative.Sel("replaceRegion:mipmapLevel:slice:withBytes:bytesPerRow:"),
                                    region,
                                    (ulong)level,
                                    (ulong)clampedLayer,
                                    (nint)ptr,
                                    (ulong)rowBytes);
                            }
                            else
                            {
                                MetalNative.SendVoid(
                                    _texture,
                                    MetalNative.Sel("replaceRegion:mipmapLevel:withBytes:bytesPerRow:"),
                                    region,
                                    (ulong)level,
                                    (nint)ptr,
                                    (ulong)rowBytes);
                            }
                            MetalContext.RecordTextureUpload();
                        }
                    }
                }

                _owner?.Dispose();
                _owner = data;
            }
        }

        public void SetStorage(BufferRange buffer) { }

        public void Release()
        {
            lock (_lock)
            {
                _owner?.Dispose();
                _owner = null;

                if (_ownsTexture && _texture != nint.Zero)
                {
                    MetalNative.SendVoid(_texture, MetalNative.Sel("release"));
                    _texture = nint.Zero;
                }
            }
        }
    }

    class MetalSampler : ISampler
    {
        private readonly SamplerCreateInfo _info;
        private nint _sampler;

        public MetalSampler(SamplerCreateInfo info)
        {
            _info = info;

            if (!MetalContext.IsAvailable)
            {
                return;
            }

            nint pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                nint descriptor = MetalNative.SendObject(MetalNative.Class("MTLSamplerDescriptor"), MetalNative.Sel("new"));
                if (descriptor == nint.Zero)
                {
                    return;
                }

                MetalNative.SendVoid(descriptor, MetalNative.Sel("setMinFilter:"), ToMinFilter(info.MinFilter));
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setMagFilter:"), info.MagFilter == MagFilter.Linear ? 1UL : 0UL);
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setMipFilter:"), ToMipFilter(info.MinFilter));
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setSAddressMode:"), ToAddressMode(info.AddressU));
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setTAddressMode:"), ToAddressMode(info.AddressV));
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setRAddressMode:"), ToAddressMode(info.AddressP));
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setMaxAnisotropy:"), (ulong)Math.Clamp((int)info.MaxAnisotropy, 1, 16));
                MetalNative.SendVoid(descriptor, MetalNative.Sel("setSupportArgumentBuffers:"), (byte)1);

                _sampler = MetalNative.SendObject(MetalContext.Device, MetalNative.Sel("newSamplerStateWithDescriptor:"), descriptor);
                Console.WriteLine($"[MetalSampler] 创建: sampler=0x{_sampler:X}");
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        internal nint NativeSampler => _sampler;
        internal SamplerCreateInfo Info => _info;
        internal ulong NativeResourceId => _sampler == nint.Zero ? 0UL : MetalNative.SendULong(_sampler, MetalNative.Sel("gpuResourceID"));

        public void Dispose()
        {
            if (_sampler != nint.Zero)
            {
                MetalNative.SendVoid(_sampler, MetalNative.Sel("release"));
                _sampler = nint.Zero;
            }
        }

        private static ulong ToMinFilter(MinFilter filter) => filter switch
        {
            MinFilter.Linear or MinFilter.LinearMipmapNearest or MinFilter.LinearMipmapLinear => 1UL,
            _ => 0UL,
        };

        private static ulong ToMipFilter(MinFilter filter) => filter switch
        {
            MinFilter.NearestMipmapNearest or MinFilter.LinearMipmapNearest => 1UL,
            MinFilter.NearestMipmapLinear or MinFilter.LinearMipmapLinear => 2UL,
            _ => 0UL,
        };

        private static ulong ToAddressMode(AddressMode mode) => mode switch
        {
            // 对照 MTLSamplerAddressMode：ClampToEdge=0, MirrorClampToEdge=1,
            // Repeat=2, MirrorRepeat=3, ClampToZero=4, ClampToBorderColor=5。
            AddressMode.Repeat => 2UL,
            AddressMode.MirroredRepeat => 3UL,
            AddressMode.ClampToBorder => 5UL,
            AddressMode.Clamp => 0UL,
            AddressMode.MirrorClamp => 1UL,
            AddressMode.MirrorClampToEdge => 1UL,
            AddressMode.MirrorClampToBorder => 1UL,
            _ => 0UL,
        };
    }

    class MetalImageArray : IImageArray
    {
        private readonly MetalTexture[] _images;

        public MetalImageArray(int size)
        {
            _images = new MetalTexture[Math.Max(0, size)];
        }

        public void SetImages(int index, ITexture[] images)
        {
            if (images == null)
            {
                return;
            }

            for (int offset = 0; offset < images.Length && index + offset < _images.Length; offset++)
            {
                _images[index + offset] = images[offset] as MetalTexture;
            }
        }

        internal IReadOnlyList<MetalTexture> Images => _images;

        public void Dispose() { }
    }

    class MetalTextureArray : ITextureArray
    {
        private readonly (MetalTexture Texture, MetalSampler Sampler)[] _textures;

        public MetalTextureArray(int size)
        {
            _textures = new (MetalTexture Texture, MetalSampler Sampler)[Math.Max(0, size)];
        }

        public void SetSamplers(int index, ISampler[] samplers)
        {
            if (samplers == null)
            {
                return;
            }

            for (int offset = 0; offset < samplers.Length && index + offset < _textures.Length; offset++)
            {
                _textures[index + offset].Sampler = samplers[offset] as MetalSampler;
            }
        }

        public void SetTextures(int index, ITexture[] textures)
        {
            if (textures == null)
            {
                return;
            }

            for (int offset = 0; offset < textures.Length && index + offset < _textures.Length; offset++)
            {
                _textures[index + offset].Texture = textures[offset] as MetalTexture;
            }
        }

        internal IReadOnlyList<(MetalTexture Texture, MetalSampler Sampler)> Textures => _textures;

        public void Dispose() { }
    }
}
