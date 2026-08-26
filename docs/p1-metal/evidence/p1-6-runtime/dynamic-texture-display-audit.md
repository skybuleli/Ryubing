# Celeste 动态纹理与显示效果审计

输入证据：`docs/p1-metal/evidence/p1-6-runtime/live-capture/` 中的 764 个真实 draw manifest。

## 统计结果

- draw 数：764
- texture binding：764
- texture format：`R8G8B8A8Unorm`，764/764
- mip level：`1`，764/764
- texture 尺寸：`4096x4096` 301 次、`1922x1082` 234 次、`134x126` 229 次
- sampler：
  - Linear/Linear + Repeat：463 次
  - Linear/Linear + ClampToEdge：301 次
- texture array/image array：本区间未出现

## 结论

真实 Celeste 这段序列没有触发压缩纹理、mipmap、texture array 或多格式纹理，因此当前颜色/纹理异常不能由这份 capture 单独归因于这些状态。

需要优先验证的共因是：

1. `R8G8B8A8Unorm` 的 Metal shader 资源绑定和采样坐标；
2. 透明纹理的 blend 输入是否为正确的 RGBA 通道；
3. 每次 draw 的 texture 数据是否在上传后被后续 owner 生命周期覆盖；
4. 每帧动态 uniform/buffer 内容是否与当前 draw 同步。

本次动态 frame stats 已证明最终 framebuffer hash 在变化，但 hash 变化不代表视觉语义正确。要完成颜色/粒子验收，还需要参考渲染结果或针对纹理采样、blend 和动画 uniform 建立像素级 fixture。
