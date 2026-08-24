using Ryujinx.Common.Configuration;
using Ryujinx.Input.HLE;
using Ryujinx.Graphics.Metal;
using SDL;
using static SDL.SDL3;

namespace Ryujinx.Headless
{
    class MetalWindow : WindowBase
    {
        public MetalWindow(
            InputManager inputManager,
            GraphicsDebugLevel glLogLevel,
            AspectRatio aspectRatio,
            bool enableMouse,
            HideCursorMode hideCursorMode,
            bool ignoreControllerApplet)
            : base(inputManager, glLogLevel, aspectRatio, enableMouse, hideCursorMode, ignoreControllerApplet)
        {
        }

        public override SDL_WindowFlags WindowFlags => SDL_WindowFlags.SDL_WINDOW_HIDDEN | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY;

        protected override void InitializeWindowRenderer() { }

        protected override void InitializeRenderer()
        {
            Renderer?.Window.SetSize(DefaultWidth, DefaultHeight);
            MouseDriver.SetClientSize(DefaultWidth, DefaultHeight);
        }

        protected override void FinalizeWindowRenderer()
        {
            Device.DisposeGpu();
        }

        protected override void SwapBuffers() { var (w, h) = MetalContext.GetFrameSize(); MetalContext.PresentFrame(w, h); }
    }
}
