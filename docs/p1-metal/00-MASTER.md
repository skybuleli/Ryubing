# P1 Metal 主路线蓝图 — Slang->DXIL->MSC->metallib

> **唯一真相源**。阶段/任务/验收以此为准，与 `docs/workflow/git-workflow.md` 同门禁。范围仅限 P1，不扩 CPU/JIT 与内存零拷。

## 0. Outcome

- **目标**: macOS 上以原生 `Metal` 替代 `Vulkan->MoltenVK`，着色器链路从 `Spirv/Glsl` 切换为 `Slang->DXIL->MSC->metallib`，消除驱动二次翻译与双份资源。
- **非目标**: `ARMeilleure/AppleHv`、 `Ryujinx.Memory/RegionHandle`、 `TexturePool` 零拷 (属 P2/P3)。

## 1. 基线与插入点

- **GAL**: `src/Ryujinx.Graphics.GAL/{IRenderer,IPipeline,IProgram,Capabilities}` 为抽象，实体仅 `Vulkan/OpenGL`，无 `Metal`。
- **着色器**: `Gpu/Shader/ShaderCache.cs:838 CreateTranslationOptions()` 硬编码 `api==Vulkan? Spirv:Glsl`，经 `Translator->StructuredProgram->CodeGen/{Spirv,Glsl}->ShaderSource(Code/BinaryCode+Language)->IRenderer.CreateProgram()`。
- **现成本**: `Spv.Generator + Shaderc + MoltenVK` 占 GPU 线程 10~15% CPU + 双份 `MTLResource`。

## 2. 技术路线

```
Maxwell bin -> Translator -> StructuredProgram -> SlangGenerator (.slang/HLSL)
  -> slangc -target dxil -profile sm_6_0 -> DXIL
  -> MSC libmetalirconverter.dylib -> metallib
  -> MTLRenderPipelineState (IProgram) -> Metal draw
```

- **主路径 A**: `Slang->DXIL->MSC`，Apple 官方，CLT 可用，已验证 `slangc -profile sm_6_0` 必加。
- **备选 B**: `DXIL->AIR (dxmt airconv)` 仅当 MSC 波形语义丢失时启用，不在 P1 默认。

## 3. 里程碑 (Trunk-Based, 分支 `feat/p1-metal-*`)

| 里程碑 | 分支 | 交付物 | 门禁 |
|---|---|---|---|
| P1-1 骨架 | `feat/p1-metal-skeleton#<id>` | `src/Ryujinx.Graphics.Metal` + `TargetApi.Metal/TargetLanguage.Slang` + `MetalRenderer` Clear | `dotnet build` + 无 MoltenVK 进程 |
| P1-2 Slang | `feat/p1-metal-slang#<id>` | `CodeGen/Slang/{SlangGenerator,Declarations,IoMap}` + 磁盘缓存三级 | 同 Maxwell 输入 Spirv vs Slang 语义一致测试 |
| P1-3 MSC | `feat/p1-metal-msc#<id>` | `P/Invoke libmetalirconverter + MTL4Compiler(3.2)` + `CreateProgram` 打通 | 端到端 `GetGraphicsShader()->Draw` 一帧 |
| P1-4 缓存 | `feat/p1-metal-cache#<id>` | `metallib` 库化 + `PipelineState` 复用 + 三级 `buffer(0/1/2)` | 二次启动 p95 达标 |
| 终验 | `docs/p1-metal/05-VERIFICATION` | 全量验收表 + evidence | 见 §4 |

## 4. 验收标尺 (终验唯一表)

| 类别 | 阈值 | 证据 |
|---|---|---|
| 构建 | `dotnet build -c Release` 0 error | `evidence/.../build.log` |
| 功能 | `Clear/DrawTexture/DispatchCompute` + `HelperShader` 全 metallib，无 Validation 错误，画面与 Vulkan 一致 | `Metal Validation Layer + 截图` |
| 编译 | 冷 `Slang->metallib` p95 <200ms，热 `metallib` 命中 <50ms | Instruments trace |
| 性能 | 同场景 vs Vulkan+MoltenVK: `VmRSS -15%`，`GPU线程CPU -25%` | Activity Monitor + Time Profiler |
| 缓存 | 二次启动命中率 >95% | `DiskCache hit log` |

任一 Fail 回滚至对应阶段文档修订。

## 5. 文档索引

- `01-TOOLCHAIN.md` 工具链契约
- `02-ARCHITECTURE.md` GAL 映射与 Bridge ABI
- `03-SHADER-PIPELINE.md` Slang 发射与缓存
- `04-PHASE-PLAN.md` 分阶段 Entry/Tasks/Exit
- `05-VERIFICATION.md` 测试矩阵与终验表

## 6. 工作流衔接

- 分支 `feat/p1-metal-*` 自动命中 `labeler p1-metal / graphics-backend:metal`，需 2 approvals (CODEOWNERS `buleli`)。
- PR 必须附 `evidence/p1-*/` 产物，否则 Checklist 不勾选不得合入。
- 文档先行：`docs/p1-metal/0x-*.md` 更新与代码同 PR。
