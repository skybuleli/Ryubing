# 03 着色器管线 — Slang 发射与缓存

## 1. 发射器

```
StructuredProgram -> SlangGenerator -> .slang (HLSL)
```

- 新增 `src/Ryujinx.Graphics.Shader/CodeGen/Slang/{SlangGenerator.cs, Declarations.cs, IoMap.cs, Instructions.cs}`
- 复用 `OperandManager / AttributeUsage / SamplerDeclaration / HelperFunctions/SwizzleAdd.glsl` 逻辑，仅输出语法改为 HLSL `[[buffer(n)]] / [[texture(n)]]`。
- `ShaderProgram`: `Code=.slang`, `BinaryCode=DXIL`, `Language=Slang`。`Prepend` 用于注入 `cbuffer` 头。

## 2. 编译时序

```
ShaderCache.GetGraphicsShader/GetComputeShader
  -> Translator -> StructuredProgram
  -> SlangGenerator.Generate(info, params) -> slang string
  -> slangc -target dxil -profile sm_6_0 -> DXIL (BinaryCode)
  -> MSC libmetalirconverter -> metallib
  -> MetalRenderer.CreateProgram([ShaderSource(Code=slang, BinaryCode=DXIL)] + metallib) -> IProgram
```

`HelperShader` 预编译: `src/Ryujinx.Graphics.Vulkan/Shaders/SpirvBinaries/*.spv` 平行新增 `src/Ryujinx.Graphics.Metal/Shaders/*.metallib`，启动时 `ReadMetallib()`。

## 3. 磁盘缓存

沿用 `DiskCacheHostStorage / BackgroundDiskCacheWriter`:

```
cache/shader/<titleId>/metal/
  <hash>.slang
  <hash>.dxil
  <hash>.metallib
  manifest.json  // {hash, stage, slang->dxil->metallib 耗时}
```

- 首次 `ProcessShaderCacheQueue()` 存三级，二次直接 `newLibraryWithData(metallib)`。
- `manifest` 用于命中率统计，P1-4 定 `>95%` 阈值。

## 4. 对照校验 (P1-2 门禁)

- 同一 Maxwell 输入，`Spirv` vs `Slang` 生成的 `StructuredProgram` 语义一致 (IoMap/资源绑定) 单元测试。
- `spirv-val` 仅作历史对照，主路径用 `slangc -warnings-as-errors`。

## 5. 证据

- `evidence/p1-2-slang/slang-dump/`: 抽样 `.slang` + 对应 `.dxil` 字节数
- `evidence/p1-2-slang/semantic-test.log`: 语义一致测试通过
