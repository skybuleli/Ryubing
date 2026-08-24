using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.Metal
{
    class MetalWindow : IWindow
    {
        public void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
        {
            // P1-1 存根：仅回调，不做实际 present。P1-3 接 MTLDrawable present.
            swapBuffersCallback?.Invoke();
        }

        public void SetSize(int width, int height) { }
        public void ChangeVSyncMode(VSyncMode vSyncMode) { }
        public void SetAntiAliasing(AntiAliasing antialiasing) { }
        public void SetScalingFilter(ScalingFilter type) { }
        public void SetScalingFilterLevel(float level) { }
        public void SetColorSpacePassthrough(bool colorSpacePassThroughEnabled) { }
    }

    class MetalCounterEvent : ICounterEvent
    {
        public bool Invalid { get; set; }
        public bool ReserveForHostAccess() => false;
        public void Flush() { }
        public void Dispose() { }
    }
}
