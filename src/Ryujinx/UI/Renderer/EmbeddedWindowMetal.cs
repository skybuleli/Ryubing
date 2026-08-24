using Ryujinx.Graphics.Metal;

namespace Ryujinx.Ava.UI.Renderer
{
    public class EmbeddedWindowMetal : EmbeddedWindowVulkan
    {
        public nint GetMetalLayer() => MetalLayer;

        public void PresentMetal()
        {
            // 真 Present 由 MetalContext 驱动，此处仅占位
            var (w, h) = MetalContext.GetFrameSize();
            MetalContext.PresentFrame(w, h);
        }
    }
}
