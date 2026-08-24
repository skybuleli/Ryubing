# 06 游戏级验证 — 真机可玩判定

> 接 `00-MASTER §4` 与 `05-VERIFICATION`，从“编译通过”提升到“可模拟游戏”。仅用自转储 ROM 或开源 Homebrew，M1 8GB + CLT 环境。

## 1. 分级

```
L1 单元 (已做 P1-2/3/4) → L2 集成 (无 ROM) → L3 游戏 (真 ROM)
```

| 级别 | 目标 | 输入 | 判定 |
|---|---|---|---|
| L1 | Slang/DXIL/metallib 链路 | `StructuredProgram` | `slangc -profile sm_6_0` 零错误 |
| L2 | GpuContext + MetalRenderer 不崩 | 模拟 Maxwell `0x50` 头 | `ShaderCache` 往返命中 |
| L3 | 真游戏进 Title 60s | `2048.nro` / `pong.nro` / `compatibility.csv` Playable 3 款 | 见 §3 |

## 2. 前置

- `MetalDevice` 真 `MTLCreateSystemDefaultDevice`，`MTL4Compiler langVersion=3.2` 单例，`CAMetalLayer` 窗口
- 启动参数 `--graphics-backend metal|vulkan` + UI 下拉 (`GraphicsConfig`)
- `Metal Validation Layer` 开启 (`MTLDebugDevice`)

## 3. 游戏用例

| ROM | 来源 | 帧数 | 判定 |
|---|---|---|---|
| `2048.nro` | switch-examples 开源编译 | 600 | 无崩，`metallib` 命中 |
| `pong.nro` | sdl2-pong Homebrew | 600 | 无崩 |
| `Celeste/Stardew` 序章 | 自转储，按 `compatibility.csv` | 1000 | 进 Title，`VmRSS -15%` vs Vulkan |

命令：
```bash
./build/Ryujinx --game ~/Games/homebrew/2048.nro --graphics-backend metal --headless --frames 600 --screenshot /tmp/metal.png 2>&1 | tee evidence/p1-5-game/2048.log
./build/Ryujinx --game ~/Games/homebrew/2048.nro --graphics-backend vulkan --headless --frames 600 --screenshot /tmp/vulkan.png 2>&1 | tee evidence/p1-5-game/2048-vk.log
diff /tmp/metal.png /tmp/vulkan.png # 画面一致率 >95%
```

## 4. 脚本

`distribution/macos/validate-metal.sh` 批量跑 `~/Games/homebrew/*.nro`，输出 `evidence/p1-5-game/{*.log,*.png,manifest.json}`。

## 5. 验收

| 项 | 阈值 | 证据 |
|---|---|---|
| 拉起 | 60s 无崩，无 `E30027` | `*.log` |
| 画面 | 与 Vulkan 一致率 >95% | `*.png` diff |
| 性能 | `VmRSS -15%`，帧时间不劣化 | `Instruments Metal System Trace` |
| 缓存 | 二次启动 `HitCount>10` | `manifest.json` |

L3 全 Pass 方标记 `P1 可玩`。
