# P2 渲染补完 — 00 主蓝图

> 接 P1 `Metal` 骨架，目标真 `Present` 与纹理可读，使 `Celeste` 出图、`BlockamokRemix` 不崩。

## 目标
- `MetalTexture` 真 `MTLTexture` + `getBytes`，`Screenshot` 非黑
- `LayoutConverter` 真块线性/线性转换，`BlockamokRemix` 的 `ConvertLinearToBlockLinear` 空引用消失
- `P2-1` 门禁：`Celeste` 截图与 `Vulkan` 一致率 >95%，`BlockamokRemix` 12s 无 `Abort trap`

## 里程碑
- P2-1 真纹理（本阶段）
- P2-2 真 Present (`CAMetalLayer` Drawable)
- P2-3 性能 (`AppleHv` + `UMA Shared`)

## 验收
- 构建 0 error
- 5 款 NRO 中 `BlockamokRemix` 转 PASS
- `Celeste` 截图非黑
