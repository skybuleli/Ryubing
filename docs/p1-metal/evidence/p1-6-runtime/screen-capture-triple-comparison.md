# Metal screen screenshot triple comparison

The Metal present diagnostics support an opt-in physical-window capture on macOS:

```bash
RYUJINX_METAL_CAPTURE_PATH="$PWD/docs/p1-metal/evidence/p1-6-runtime/live-capture-screen" \
RYUJINX_METAL_COMPARE_DRAWABLE=1 \
RYUJINX_METAL_CAPTURE_SCREEN=1 \
RYUJINX_METAL_TRACE_PRESENT=1 \
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
dotnet run --project src/Ryujinx/Ryujinx.csproj --no-build -- \
  --graphics-backend metal \
  "/Users/liliang/games/蔚蓝1.3/Celeste [01002B30028F6000][v0] (TurboSnail).nsp"
```

The diagnostic captures the emulator's native window with `CGWindowListCreateImage` and writes:

- `screen-window.bgra8.bin` — raw CoreGraphics window image;
- `screen-comparison.json` — three-way comparison metadata.

The JSON contains:

1. source framebuffer → drawable comparison;
2. drawable → physical window screenshot comparison;
3. source framebuffer → physical window screenshot comparison.

The drawable comparison is byte-exact and is authoritative for the Metal copy path. The screen comparison is not expected to be byte-identical: CoreGraphics can apply compositor scaling, display color management, Retina resolution changes, occlusion, window decorations, and channel ordering. The matcher records the selected scale, offset, and channel order and uses nearest-neighbor sampling only to identify the likely drawable region.

A real Celeste run on 2026-08-24 produced a `2560x1576` CoreGraphics window image and a `1920x1080` drawable. The matcher selected scale `1.333333`, offset `(0,132)`, and `R,G,B,A` channel order. The source-to-drawable comparison reported zero mismatched bytes. The drawable-to-screen and source-to-screen comparisons covered `2,073,600` pixels and reported `67,625` mismatched pixels / `183,068` mismatched bytes. This is expected to be a compositor/color-management or capture-timing difference, not evidence that the Metal drawable copy failed. The earlier full-screen capture used the wrong option; the implementation now uses `kCGWindowListOptionIncludingWindow` with `kCGWindowImageBoundsIgnoreFraming` and records the selected window mapping.
