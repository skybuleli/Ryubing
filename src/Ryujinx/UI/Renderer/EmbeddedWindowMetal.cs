using Ryujinx.Graphics.Metal;

namespace Ryujinx.Ava.UI.Renderer
{
    public class EmbeddedWindowMetal : EmbeddedWindowVulkan
    {
        public nint GetMetalLayer() => MetalLayer;

        public override void OnWindowCreated()
        {
            base.OnWindowCreated();
            MetalContext.Initialize(MetalLayer, NsView);
        }

        protected override void OnWindowDestroying()
        {
            MetalContext.Initialize(nint.Zero);
            base.OnWindowDestroying();
        }
    }
}
