# 02 架构 — GAL 映射与 Bridge ABI

## 1. 工程布局

```
src/Ryujinx.Graphics.Metal/
  MetalRenderer.cs      // IRenderer
  MetalPipeline.cs      // IPipeline
  MetalBuffer.cs / MetalTexture.cs / MetalSampler.cs / MetalProgram.cs
  Bridge/               // C++ libmetal_bridge.a
    MetalDevice.mm      // 唯一定义 NS_PRIVATE_IMPLEMENTATION / MTL_PRIVATE_IMPLEMENTATION
    MetalBuffer.mm / MetalTexture.mm / ...
```

`Ryujinx.sln` 新增 `Ryujinx.Graphics.Metal` 项目，`GpuContext` 注入 `new MetalRenderer()` 即可切换。

## 2. C ABI

- **句柄**: `opaque void*` + `metal_release(void*)` 统一析构，C# 侧 `SafeHandle`。
- **调用**: 全量 `NS::AutoreleasePool` 包裹，`metal-cpp` 仅在 `Bridge/` 使用。
- **存储**: UMA `Shared`，Discrete `Managed/Private` (与 `Capabilities.MemoryType` 对应)。
- **编译器**: `MTL4Compiler` 单例，`langVersion=3.2`。

## 3. GAL 映射 (核心)

| GAL | Metal |
|---|---|
| `IRenderer.CreateProgram(ShaderSource[])` | `MTLLibrary newLibraryWithData(metallib)` -> `MTLRenderPipelineState` |
| `IPipeline.SetProgram(IProgram)` | `setRenderPipelineState` |
| `IPipeline.SetRenderTargets` | `MTLRenderPassDescriptor` |
| `SetVertexBuffers/SetVertexAttribs/SetIndexBuffer` | `setVertexBuffer/setVertexBytes` |
| `SetUniformBuffers/SetStorageBuffers` | `buffer(0)=rootTable, buffer(1)=sampler, buffer(2)=perDraw` |
| `SetTextureAndSampler/SetImage` | `setFragmentTexture/setTexture` |
| `DispatchCompute` | `MTLComputeCommandEncoder dispatchThreadgroups` |
| `Draw/DrawIndexed/DrawIndirect` | `drawPrimitives/drawIndexedPrimitives` |

未实现方法在 P1-1 阶段为 `NotImplemented` stub，仅保证 `dotnet build`。

## 4. Capabilities 扩展

```csharp
// TranslationOptions 新增
TargetApi.Metal
TargetLanguage.Slang   // 对应 .slang 源码, BinaryCode 为 DXIL, Code 为 Slang
```

`ShaderCache.cs:838 CreateTranslationOptions` 分支:
```csharp
lang = api == TargetApi.Metal ? TargetLanguage.Slang
     : GraphicsConfig.EnableSpirvCompilationOnVulkan && api == TargetApi.Vulkan ? TargetLanguage.Spirv
     : TargetLanguage.Glsl;
```

## 5. 证据

- `evidence/p1-1-skeleton/build.log`: `dotnet build -c Release` 0 error
- `evidence/p1-1-skeleton/clear.png`: `MetalRenderer` Clear 一帧截图，无 Validation 错误
