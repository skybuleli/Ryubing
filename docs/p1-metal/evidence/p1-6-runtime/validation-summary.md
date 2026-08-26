# P1-6 real shader/runtime validation

Date: 2026-08-24

## Environment

- Host: macOS arm64, Apple M1, Metal 4.
- `slangc`: `/usr/local/bin/slangc`.
- `metal-shaderconverter`: `/usr/local/bin/metal-shaderconverter`, version 4.0.0.
- `xcrun metal` is not installed; MSC is used directly for DXIL conversion.
- Real Celeste NSP used for live validation: `/Users/liliang/games/蔚蓝1.3/Celeste [01002B30028F6000][v0] (TurboSnail).nsp`.
- The repository contains four captured Maxwell shader binaries in `celeste-dump/Dumps01/Full`.

## Captured Celeste shader pair

`CapturedCelesteShaderTranslationTests.TranslatesCapturedCelesteVertexAndFragmentBinaries` now decodes the four captured guest binaries through the real Structured IR translator and writes the generated Slang sources to:

```text
docs/p1-metal/evidence/p1-6-runtime/celeste-slang-source-captured/
```

The captured stage mapping is verified from each binary header:

```text
Shader0001.bin -> Vertex
Shader0002.bin -> Fragment
Shader0003.bin -> Vertex
Shader0004.bin -> Fragment
```

The first pair is used for framebuffer replay:

```text
Shader0001.bin / Shader0001-Vertex.slang
Shader0002.bin / Shader0002-Fragment.slang
```

This is the first validation in this workstream that uses a real captured Celeste fragment shader. The fragment reads `TEXCOORD0`/`TEXCOORD1`, samples `fp_t_tcb_8`, and writes the sampled color to `SV_Target0`; it is not the diagnostic adapter.

## Replay input format

The deterministic replay manifest is:

```text
docs/p1-metal/evidence/p1-6-runtime/captured-first-frame/replay-manifest.json
```

It records:

- original captured vertex and fragment binaries;
- generated Slang source paths;
- render-target dimensions and format;
- interleaved vertex data, stride, and attribute offsets;
- vertex `b0` support buffer and captured vertex `b3` matrix buffer bindings;
- fragment texture/sampler binding `t0/s0`;
- framebuffer readback acceptance criteria.

The manifest is consumed by `CelesteFirstFramebufferTests.RendersCapturedCelesteShaderPairToFramebuffer`, so the test does not reconstruct the resource layout from hard-coded shader strings.

The resource values in this manifest are a deterministic replay fixture: identity transform, a 2x2 white texture, and explicit vertex attributes. They are not claimed to be the original live Celeste resource contents; a live game capture is still required for final visual equivalence.

## First framebuffer replay result

The test executes the production Metal path:

```text
captured Maxwell binaries
 -> Structured IR
 -> generated Slang vertex + real Slang fragment
 -> slangc DXIL
 -> MSC reflection + metallib
 -> MTLLibrary
 -> MTLRenderPipelineState
 -> MSC top-level argument buffers
 -> real texture/sampler and uniform buffers
 -> GPU draw and completion wait
 -> Metal blit readback
```

Observed result:

```text
Captured texture readback bytes=255,255,255,255.
Captured Celeste pair readback coloredPixels=1152 centerBGRA=255,255,255,255.
[MetalProgram][AB] stage=Vertex binding=3 type=CBV offset=0
[MetalProgram][AB] stage=Fragment binding=0 type=SRV offset=0
[MetalProgram][AB] stage=Fragment binding=0 type=Sampler offset=24
[Metal][AB] stage=Fragment SMP binding=0 offset=24 resourceId=...
Test Run Successful.
Total tests: 1
Passed: 1
```

The full focused log is in `celeste-first-frame-runtime.log`; the aggregate Shader test log is in `celeste-first-frame-tests.log`.

## Fixes made by this validation

1. Slang lowering now accepts GLSL buffer declarations whose layout contains qualifiers before `binding` (for example `layout(std140, binding = 3)`).
2. Slang lowering removes the temporary `static` qualifier that was reintroduced for arrays inside HLSL constant buffers. Without this, MSC optimized the vertex constant buffer away and reflection returned no vertex CBV.
3. MSC reflection accepts both `SMP` and MSC 4.0's `Sampler` spelling. Without the alias, the real fragment sampler descriptor was not encoded and the framebuffer remained black.
4. The runtime records resource IDs and binds the real fragment SRV and sampler descriptors at the reflection-provided offsets.

## Verification

```text
dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~Shader
Test Run Successful.
Total tests: 7
Passed: 7
```

```text
dotnet build src/Ryujinx.Graphics.Metal/Ryujinx.Graphics.Metal.csproj --no-restore -c Release
Build succeeded.
0 warnings, 0 errors.
```

```text
dotnet build src/Ryujinx/Ryujinx.csproj --no-restore -c Release
Build succeeded.
0 errors.
```

The main build still reports the pre-existing `System.Private.Uri` package vulnerability warnings.

## Live first-draw capture

The backend now exposes an opt-in capture path in `MetalCapture`.

```text
RYUJINX_METAL_CAPTURE_PATH=/absolute/output/directory
RYUJINX_METAL_CAPTURE_ALL_DRAWS=0
```

After a draw is committed, the capture path waits for the GPU, checks the fence and submitted draw command buffer, and writes:

- exact Slang source and MSC reflection for every stage;
- raw vertex, index, uniform, and storage buffer ranges;
- raw texture/image readbacks plus sampler parameters and resource IDs;
- vertex attributes, vertex buffers, viewport/scissor, topology, index parameters, draw counts, depth/stencil and blend state;
- direct and CPU-decoded indexed/non-indexed indirect draw commands;
- `framebuffer-0001.bgra8.bin` after GPU completion;
- `replay-manifest.json`, a relative-path replay view of the same capture.

The capture path is disabled by default and failures are logged without aborting emulation. The launch procedure and output contract are documented in `metal-live-capture.md`. The Celeste framebuffer regression test enables the same path in a temporary directory and verifies that the completed framebuffer and replay manifest are readable. The Metal runtime regression additionally exercises a D16 depth attachment, depth clear/test state, cull/front-face/depth-clip state, dynamic blend/color-mask/multisample/logic-op PSO updates, UByte indexed drawing with a non-zero first index, and CPU-decoded non-indexed indirect draw. Stencil descriptors and references are encoded when enabled and included in captures. Texture/image arrays are flattened into binding-addressable elements, and extra descriptor-set arrays now use the same representation rather than being silently ignored.

## Current conclusion

The first captured Celeste vertex/fragment pair now produces non-clear pixels through real shader compilation, MSC reflection, argument-buffer resource binding, GPU completion, framebuffer readback, depth attachment setup, indirect draw decoding, and the new capture/replay path. This proves the first captured pair's shader/resource contract is executable on the Apple M1 and that the runtime can now collect the evidence needed from a real ROM run.

It is not yet a complete Celeste first-frame acceptance. The missing evidence is the original live Celeste resource snapshot and framebuffer from a ROM run, including the actual texture contents, support/constant buffer bytes, depth/stencil target, blend/depth state, index/indirect draw arguments, and every shader pair used before the first present. True bindless indexing, multisample attachments/sample-count validation, and polygon-point semantics still need dedicated coverage; Metal's unsupported alpha-to-coverage dither extension is intentionally capture-only. A real ROM run has now been completed. The compact live capture is retained under `live-capture/`: the first draw completed on the GPU, produced a `1920x1080` framebuffer of `8294400` bytes, and included non-black pixels. A short full-draw diagnostic run completed 145 draws with `gpuCompleted=true` for every draw; 140 were indexed, 5 non-indexed, all used `UShort` triangle lists, and every draw produced non-zero colored-pixel observations. The large full capture was removed after extracting statistics; see `live-capture-sequence-summary.md`.

The UI present path was also verified with `RYUJINX_METAL_TRACE_PRESENT=1`: `MetalWindow.Present` received a `MetalTexture`, found a real CAMetalLayer, copied the `R8G8B8A8Unorm` source to the drawable, and submitted `presentDrawable`. A follow-up diagnostic build waits for the present command buffer when either trace or drawable comparison is enabled; the live run reported `status=4` and `error=NSError 为空` repeatedly. With `RYUJINX_METAL_COMPARE_DRAWABLE=1`, the actual `1920x1080` drawable was read back through a staging buffer and compared against the same source rectangle: `comparedPixels=2073600`, `comparedBytes=8294400`, `mismatchedPixels=0`, `mismatchedBytes=0`, `identical=true`. With `RYUJINX_METAL_CAPTURE_SCREEN=1`, macOS `CGWindowListCreateImage` additionally captures the actual emulator window and writes `screen-window.bgra8.bin` plus `screen-comparison.json`. A fresh run selected scale `1.333333`, offset `(0,132)`, and `R,G,B,A` channel order; source-to-drawable was exact, while drawable-to-screen reported `67625/2073600` mismatched pixels and `183068/8294400` mismatched bytes. This third comparison is diagnostic only because compositor scaling, color management, capture timing, and window composition affect physical-screen pixels. See `screen-capture-triple-comparison.md`; longer interactive gameplay remains to be verified.
