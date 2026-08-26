# Real Celeste Metal lightweight capture summary

ROM:

```text
/Users/liliang/games/蔚蓝1.3/Celeste [01002B30028F6000][v0] (TurboSnail).nsp
```

Capture mode:

```text
RYUJINX_METAL_CAPTURE_ALL_DRAWS=1
RYUJINX_METAL_CAPTURE_METADATA_ONLY=1
```

The 45-second diagnostic run produced 764 metadata manifests. Every draw reported `gpuCompleted=true`. Metadata-only mode intentionally omitted shader source, buffer bytes, texture bytes, depth bytes, and framebuffer readback files; the complete directory was approximately 16 MB.

## Draw sequence

- Total draws: 764
- GPU completed: 764/764
- Indexed/non-indexed: 764/0 in this interval
- Primitive topology: `Triangles` for all 764 draws
- Render target format: `R8G8B8A8Unorm` for all 764 draws
- Texture snapshot count: 1 per draw
- Buffer snapshot count: 4 per draw

## Depth and stencil

- Depth attachment: present on 764/764 draws
- Depth format: `D24UnormS8Uint` on 764/764 draws
- Depth test enabled: 279 draws
- Depth test disabled: 485 draws
- Depth functions: `LessOrEqual` 703, `Never` 61
- Stencil test enabled: 0 draws

The D24S8 value is the GAL format presented to the Metal backend; the device-specific Metal texture format may be downgraded by `MetalTextureDescriptor` when native D24S8 support is unavailable.

## Blend and raster state

- Blend enabled: 764/764 draws
- Color operation: `Add` for all draws
- Source/destination factors:
  - `SrcAlpha` + `One`: 458 draws
  - `One` + `OneMinusSrcAlpha`: 306 draws
- Color write mask: `0xF` for all draws
- Polygon mode: Fill/Fill for all draws
- Multisample alpha-to-coverage/alpha-to-one: disabled for all draws
- Logic operation: disabled for all draws

## Texture/image arrays

No texture-array or image-array bindings occurred in this 764-draw interval:

```text
textureArrayBindingDraws = 0
imageArrayBindingDraws = 0
```

This is evidence that the tested Celeste scene did not exercise those GAL calls during this interval; it is not evidence that the array implementation is complete. Dedicated array fixtures are still required.

## Files

The detailed metadata manifests are under:

```text
live-capture/draw-0001.json
live-capture/draw-0002.json
...
live-capture/draw-0764.json
```

`live-capture/sequence.json` contains the ordered draw list. The capture is intentionally kept as a diagnostic artifact and may be regenerated with the command documented in `metal-live-capture.md`.
