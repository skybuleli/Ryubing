using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.Metal
{
    class MetalWindow : IWindow
    {
        public void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
        {
            bool trace = Environment.GetEnvironmentVariable("RYUJINX_METAL_TRACE_PRESENT") == "1";
            if (trace)
            {
                Console.WriteLine($"[Metal][Present] called texture={texture?.GetType().Name ?? "null"}");
            }

            if (texture is MetalTexture metalTexture && MetalContext.PresentTexture(metalTexture, crop))
            {
                swapBuffersCallback?.Invoke();
                return;
            }

            if (trace)
            {
                Console.WriteLine("[Metal][Present] fallback callback");
            }

            // 无可用 Metal 纹理时保留 callback，供 headless/降级路径继续工作。
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
