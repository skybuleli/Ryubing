using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.Metal
{
    class MetalTexture : ITexture
    {
        private readonly TextureCreateInfo _info;
        private MemoryOwner<byte> _owner;
        private readonly object _lock = new();

        public MetalTexture(TextureCreateInfo info)
        {
            _info = info;
            Console.WriteLine($"[MetalTexture] 创建: {info.Width}x{info.Height} {info.Format} Levels={info.Levels}");
            if (info.Width == 1280 && info.Height == 720)
            {
                Console.WriteLine($"[MetalTexture] 真 MTLTexture 创建试点: {info.Width}x{info.Height} {info.Format} (simplegfx)");
                nint desc = MetalTextureDescriptor.CreateDescriptor(info.Width, info.Height);
                nint tex = MetalTextureDescriptor.CreateTexture(MetalContext.Device, desc);
                Console.WriteLine($"[MetalTexture] MTLTexture 句柄: 0x{tex:X}");
            }
        }

        public int Width => _info.Width;
        public int Height => _info.Height;

        public void CopyTo(ITexture destination, int firstLayer, int firstLevel) { }
        public void CopyTo(ITexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel) { }
        public void CopyTo(ITexture destination, Extents2D srcRegion, Extents2D dstRegion, bool linearFilter) { }
        public void CopyTo(BufferRange range, int layer, int level, int stride) { }
        public ITexture CreateView(TextureCreateInfo info, int firstLayer, int firstLevel) => new MetalTexture(info);
        public PinnedSpan<byte> GetData()
        {
            lock (_lock)
            {
                if (_owner.Memory.Length > 0) return PinnedSpan<byte>.UnsafeFromSpan(_owner.Memory.Span);
                return new PinnedSpan<byte>();
            }
        }
        public PinnedSpan<byte> GetData(int layer, int level)
        {
            lock (_lock)
            {
                if (_owner.Memory.Length > 0) return PinnedSpan<byte>.UnsafeFromSpan(_owner.Memory.Span);
                return new PinnedSpan<byte>();
            }
        }
        public void SetData(MemoryOwner<byte> data)
        {
            lock (_lock) { _owner?.Dispose(); _owner = data; }
        }
        public void SetData(MemoryOwner<byte> data, int layer, int level)
        {
            lock (_lock) { _owner?.Dispose(); _owner = data; }
        }
        public void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region)
        {
            lock (_lock) { _owner?.Dispose(); _owner = data; }
        }
        public void SetStorage(BufferRange buffer) { }
        public void Release()
        {
            lock (_lock) { _owner?.Dispose(); _owner = null; }
        }
    }

    class MetalSampler : ISampler
    {
        public void Dispose() { }
    }

    class MetalImageArray : IImageArray
    {
        public void Dispose() { }
        public void SetImages(int index, ITexture[] images) { }
    }

    class MetalTextureArray : ITextureArray
    {
        public void Dispose() { }
        public void SetSamplers(int index, ISampler[] samplers) { }
        public void SetTextures(int index, ITexture[] textures) { }
    }
}
