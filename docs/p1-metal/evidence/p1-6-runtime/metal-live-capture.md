# Metal live first-draw capture

The Metal backend has an opt-in capture path for the first submitted draw. It is disabled unless `RYUJINX_METAL_CAPTURE_PATH` is set, so normal rendering does not perform synchronous readback.

## Real Celeste run

Run on macOS with the Metal backend selected:

```bash
rm -rf docs/p1-metal/evidence/p1-6-runtime/live-capture
# Optional comparison input: export a raw BGRA8 reference before launching.
# export RYUJINX_METAL_CAPTURE_REFERENCE_FRAMEBUFFER="$PWD/reference-framebuffer.bgra8.bin"
RYUJINX_METAL_CAPTURE_PATH="$PWD/docs/p1-metal/evidence/p1-6-runtime/live-capture" \
RYUJINX_METAL_CAPTURE_ALL_DRAWS=0 \
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
dotnet run --project src/Ryujinx/Ryujinx.csproj --no-restore -- \
  --graphics-backend metal \
  /absolute/path/to/Celeste.nsp
```

The same environment variables can be applied when launching a packaged UI build. Allow the game to reach the first rendered frame, then close the emulator. `RYUJINX_METAL_CAPTURE_ALL_DRAWS=1` captures every draw, but intentionally stalls after each draw and should only be used for a short diagnostic window. For a lightweight full sequence that keeps draw state/resources but skips per-draw shader, buffer, texture, depth and framebuffer byte dumps, also set `RYUJINX_METAL_CAPTURE_METADATA_ONLY=1`.

Example lightweight full-draw command:

```bash
RYUJINX_METAL_CAPTURE_PATH="$PWD/docs/p1-metal/evidence/p1-6-runtime/live-capture" \
RYUJINX_METAL_CAPTURE_ALL_DRAWS=1 \
RYUJINX_METAL_CAPTURE_METADATA_ONLY=1 \
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
dotnet run --project src/Ryujinx/Ryujinx.csproj --no-restore -- \
  --graphics-backend metal \
  /absolute/path/to/Celeste.nsp
```

The capture path is synchronous: after the draw is committed it waits for the submitted command buffer, checks both the fence and draw command buffer, reads back the color target, then writes resource snapshots. If `RYUJINX_METAL_CAPTURE_REFERENCE_FRAMEBUFFER` points to a raw BGRA8 file, the manifest also records compared and mismatched byte counts. Set `RYUJINX_METAL_COMPARE_DRAWABLE=1` to additionally wait for the CAMetalDrawable present command, read the drawable texture back through a blit staging buffer, and compare its pixels against the copied source rectangle. Set `RYUJINX_METAL_CAPTURE_SCREEN=1` to also capture the actual emulator window with macOS `CGWindowListCreateImage`; this writes `screen-window.bgra8.bin` and `screen-comparison.json`, comparing drawable and window pixels after nearest-neighbor size normalization. Screen capture is diagnostic only because CoreGraphics can include compositor scaling, display color management, occlusion, and window decorations. The drawable comparison remains the authoritative byte-exact GPU/present check. A capture failure is logged as `[MetalCapture]` and does not terminate emulation.

## Output files

For draw `0001` the directory contains:

- `draw-0001.json`: live capture manifest, command completion status, shader source/reflection references, resource IDs, draw parameters, and state. Metadata-only captures set `captureMode` to `metadata-only` and omit raw byte files.
- `draw-0001-*.slang`: the exact Slang source passed to the Metal compiler for each stage.
- `draw-0001-*.reflection.json`: MSC top-level argument-buffer reflection for each compiled stage.
- `draw-0001-VertexBuffer-b*.bin`, `UniformBuffer-b*.bin`, `StorageBuffer-b*.bin`, and `IndexBuffer.bin`: raw byte snapshots at the bound ranges.
- `draw-0001-*-Texture-b*.bin` and `draw-0001-*-Image-b*.bin`: raw texture/image readbacks.
- `framebuffer-0001.bgra8.bin`: the color target after GPU completion.
- `depth-stencil-0001.bin` when a depth/stencil attachment is bound.
- `sequence.json`: aggregate list of captured draw manifests and framebuffer completion status when `RYUJINX_METAL_CAPTURE_ALL_DRAWS=1`.
- `drawable-comparison.json`: optional actual CAMetalDrawable readback, source-rectangle mapping, compared pixel/byte counts, and mismatch counts.
- `screen-window.bgra8.bin` and `screen-comparison.json`: optional macOS compositor/window capture and normalized drawable-to-screen comparison.
- `replay-manifest.json`: canonical replay-oriented manifest generated from the same draw, including sequence metadata, depth, stencil, blend, color-mask, multisample, polygon, logic-op, and indirect-draw state. It uses relative file names and includes shader pair, render target, vertex input, resources, draw state, and observed framebuffer information.

Texture entries retain sampler parameters and Metal resource IDs. Repeated bindings of the same texture refer to the same captured byte file instead of silently dropping the later binding. Array entries additionally retain `arrayBinding` and `arrayIndex`; extra descriptor-set arrays use the same binding-range representation as the Metal argument-buffer path.

## Acceptance checks

A valid first-draw capture must have:

1. `draw-0001.json` contains `"gpuCompleted": true`;
2. a non-null `renderTarget.framebufferFile` whose size is `width * height * 4` for BGRA8;
3. both vertex and fragment source entries, plus reflection when MSC produced it;
4. at least the resources actually bound by the draw, including array elements when present;
5. `replay-manifest.json` with the same framebuffer reference;
6. when `RYUJINX_METAL_COMPARE_DRAWABLE=1` is enabled, `drawable-comparison.json` reports `identical: true` for an exact source/drawable match.

The repository test `RendersCapturedCelesteShaderPairToFramebuffer` enables this mode against the captured Celeste pair, verifies the replay manifest and sequence are readable, and verifies the completed framebuffer file size. It is a deterministic capture-path regression test; it is not a substitute for a live ROM capture.
