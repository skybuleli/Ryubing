# 01 工具链契约 — slangc / DXIL / MSC / metallib

> P1 唯一编译链路。与 `00-MASTER §2` 对齐，已在 M1+CLT 验证。

## 1. 版本锁

| 工具 | 路径 | 版本 | 用途 |
|---|---|---|---|
| slangc | `slangc` (PATH) | 最新 (内嵌 DXC v1.10.2605.24+) | `.slang -> DXIL` |
| metal-shaderconverter | `/usr/local/bin/metal-shaderconverter` | 4.0 | `DXIL -> metallib` (CLI) |
| libmetalirconverter | `/usr/local/lib/libmetalirconverter.dylib` | 4.0 | 同上运行时库 (P/Invoke) |
| metal (CLT) | `/usr/bin/metal` | CLT SDK | 仅 Path B/C，P1 不用 |
| spirv-* | `/opt/homebrew/bin/spirv-{val,opt,cross}` | - | 仅对照校验，不在主路径 |

## 2. 命令

```bash
# Slang -> DXIL (必须 -profile sm_6_0，否则零输出)
slangc input.slang -target dxil -entry main -stage vertex -profile sm_6_0 -o out.dxil

# DXIL -> metallib
metal-shaderconverter out.dxil -o out.metallib
# 运行时等价: libmetalirconverter.dylib P/Invoke
```

## 3. 陷阱与规避

| 陷阱 | 现象 | 规避 |
|---|---|---|
| 无 `sm_6_0` | 无输出文件 | 固定 `-profile sm_6_0` |
| `xcrun metal` 缺失 | `xcrun: error` | P1 不用 Path B/C，仅用 MSC |
| `MTL4Compiler` 多实例 | 并发崩溃 | 单例 |
| `MSL 4.0 + float16` | M1/M2 超时 | `MTLLanguageVersion3_2` |
| Wave->SIMD | 语义丢失待验证 | 先禁 `SupportsShaderBallot`，标量降级 |

## 4. 证据

- `evidence/p1-1-skeleton/toolchain.log`: `slangc --version` + `metal-shaderconverter --help` + `out.dxil`/`out.metallib` 产物 `ls -lh`
- `evidence/p1-3-msc/msc.log`: `libmetalirconverter` 调用返回 `metallib` 字节数

## 5. 变更策略

- 升级 `slangc` 即升级内嵌 DXC，无需单独装 `dxc`。
- `SLANG_USE_SYSTEM_DXC` 仅在验证 Slang 内嵌 DXC 回归时启用。
