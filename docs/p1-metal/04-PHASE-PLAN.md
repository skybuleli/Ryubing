# 04 分阶段计划 — Entry / Tasks / Exit / Evidence

> 分支前缀 `feat/p1-metal-*`，Issue 用 `p1_metal.yml` 标题 `[P1-P1.x]`，PR 关联 `evidence/p1-*/`。

## P1-1 骨架 (MetalRenderer Clear)

- **Entry**: `00-MASTER` + `01-TOOLCHAIN` 已评审
- **Tasks**:
  1. 新建 `Ryujinx.Graphics.Metal` 项目，`MetalRenderer:IRenderer` + `MetalPipeline:IPipeline` stub
  2. `Bridge/MetalDevice.mm` 单例 `MTL4Compiler(3.2)`，`opaque handle + metal_release`
  3. `TranslationOptions` 扩展 `Metal/Slang`，`ShaderCache:838` 分支
  4. `dotnet sln add` + `Distribution/macos` 打包脚本适配
- **Exit**: `dotnet build -c Release` 0 error，`MetalRenderer` 可 Clear 一帧，无 Validation 错误
- **Evidence**: `evidence/p1-1-skeleton/{build.log, toolchain.log, clear.png}`

## P1-2 Slang 发射

- **Entry**: P1-1 Exit 绿
- **Tasks**:
  1. 新增 `CodeGen/Slang/{SlangGenerator, Declarations, IoMap, Instructions}`
  2. `HelperShader` 预编译 `*.metallib`
  3. 单元测试：同 Maxwell 输入 Spirv vs Slang 语义一致
- **Exit**: 抽样 `.slang` 可 `slangc -target dxil -profile sm_6_0` 产 `DXIL`
- **Evidence**: `evidence/p1-2-slang/{slang-dump/, semantic-test.log}`

## P1-3 MSC 打通

- **Entry**: P1-2 Exit 绿
- **Tasks**:
  1. `P/Invoke libmetalirconverter.dylib` 封装 `DXIL->metallib`
  2. `MetalRenderer.CreateProgram` 接 `slangc + MSC + newLibraryWithData`
  3. 端到端 `GetGraphicsShader()->Draw` 一帧
- **Exit**: 一帧 `DrawTexture/DispatchCompute` 成功，`metallib` 字节数 >0
- **Evidence**: `evidence/p1-3-msc/{msc.log, draw.png, metallib/*.metallib}`

## P1-4 缓存与库化

- **Entry**: P1-3 Exit 绿
- **Tasks**:
  1. `DiskCacheHostStorage` 三级缓存 + `manifest.json`
  2. `metallib` 按 `PipelineUid` 聚合 `MTLLibrary`，`PipelineState` 复用
  3. 三级 `buffer(0/1/2)` 绑定定版
- **Exit**: 二次启动命中率 >95%，`p95 <50ms`
- **Evidence**: `evidence/p1-4-cache/{cache-hit.log, trace.trace}`

## 终验

- 执行 `05-VERIFICATION` 全量，更新 `00-MASTER §4` 验收表，归档 `evidence/`。
