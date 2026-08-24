# 05 验证与验收

## 1. 测试矩阵

| 层级 | 命令 | 门禁 |
|---|---|---|
| 构建 | `dotnet build -c Release` | 0 error |
| 格式 | `dotnet format --verify-no-changes` | 0 diff (verify.yml) |
| 单元 | `dotnet test --no-build -c Release` | 全绿，含 Slang 语义一致 |
| 集成 | `Ryujinx --gpu-backend metal --game <title>` 启动一帧 | 无 Metal Validation 错误 |

## 2. 性能采集 (Instruments)

- **模板**: `Time Profiler + Allocations + Metal System Trace`
- **场景**: 同一存档同关卡，Vulkan+MoltenVK vs Metal 各 3 次
- **指标**: `VmRSS` (Activity Monitor)、`GPU线程CPU%` (Time Profiler)、`Slang->metallib` 冷/热耗时 (manifest)

## 3. 终验表 (与 00-MASTER §4 一致)

| 类别 | 阈值 | 证据 | 结果 |
|---|---|---|---|
| 构建 | 0 error | `evidence/**/build.log` | Pass/Fail |
| 功能 | Clear/Draw/Compute + HelperShader 全 metallib，画面一致 | `clear.png/draw.png + Validation` | Pass/Fail |
| 编译 | 冷 <200ms p95，热 <50ms | `trace.trace + manifest.json` | Pass/Fail |
| 性能 | VmRSS -15%，GPU线程CPU -25% | `Time Profiler + VmRSS` | Pass/Fail |
| 缓存 | 二次启动命中率 >95% | `cache-hit.log` | Pass/Fail |

全 Pass 方可标记 `P1 交付`，任一 Fail 回滚至 `04-PHASE-PLAN` 对应阶段。

## 4. 回归策略

- 失败用例落 `evidence/issue-*.md`，关联 Forgejo Issue `p1_metal` 标签。
- 每阶段 PR 必须附对应 `evidence/p1-*/`，否则 Checklist 未勾选不得合入。
