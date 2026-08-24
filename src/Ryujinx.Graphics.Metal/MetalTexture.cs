using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.Metal
{
    class MetalTexture : ITexture
    {
        private readonly TextureCreateInfo _info;
        private byte[] _data;
        private readonly object _lock = new();

        public MetalTexture(TextureCreateInfo info)
        {
            _info = info;
            // 预分配占位，SetData 时按实际大小重分配
            int size = Math.Max(1, _info.Width) * Math.Max(1, _info.Height) * Math.Max(1, _info.Depth) * Math.Max(1, _info.BytesPerPixel);
            _data = new byte[size];
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
            lock (_lock) { return PinnedSpan<byte>.UnsafeFromSpan(_data.AsSpan()); }
        }
        public PinnedSpan<byte> GetData(int layer, int level)
        {
            lock (_lock) { return PinnedSpan<byte>.UnsafeFromSpan(_data.AsSpan()); }
        }
        public void SetData(MemoryOwner<byte> data)
        {
            lock (_lock) { _data = data.Memory.ToArray(); }
            data.Dispose();
        }
        public void SetData(MemoryOwner<byte> data, int layer, int level)
        {
            lock (_lock) { _data = data.Memory.ToArray(); }
            data.Dispose();
        }
        public void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region)
        {
            lock (_lock) { _data = data.Memory.ToArray(); }
            data.Dispose();
        }
        public void SetStorage(BufferRange buffer) { }
        public void Release() { }
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
