# Metal 动态渲染与输入诊断

## 新增诊断开关

```bash
RYUJINX_METAL_CAPTURE_FRAME_STATS=1
RYUJINX_METAL_CAPTURE_PATH="$PWD/docs/p1-metal/evidence/p1-6-runtime/dynamic-capture"
RYUJINX_INPUT_TRACE=1
```

启用 `RYUJINX_METAL_CAPTURE_FRAME_STATS=1` 后，每个成功完成的 present 输出一行 `frame-sequence.jsonl`，包含：

- present 序号；
- drawable framebuffer 的 FNV-1a 64-bit hash；
- 相邻 framebuffer 的 changed pixel 数；
- GPU completion 状态；
- texture upload 和 buffer upload 累计计数。

该路径仅在显式开关下同步读回 drawable，不影响默认异步 present。

## Texture 语义修复

Metal texture descriptor 现在区分：

- 2D array、cube、cube array；
- 3D texture；
- multisample texture 的 sample count；
- mipmap level count；
- texture view 的 level/slice range；
- `SetData` / `GetData` 的 layer 和 mip level。

资源上传会计入 `textureUploads`，buffer 更新会计入 `bufferUploads`。

## 输入诊断

`RYUJINX_INPUT_TRACE=1` 时记录：

```text
[InputTrace][SDL] joystick-added / gamepad-connected
[InputTrace][SDL] sample ...
[InputTrace][NpadController] ...
[InputTrace][Npad] update=...
```

这可以区分设备发现、SDL 原始状态、映射状态和 HLE Npad 更新四个边界。

## 当前验证状态

- Metal 项目构建：通过，0 errors。
- 主程序构建：通过，0 errors。
- `MetalRuntimeTests`：2/3 通过。
- 已知失败：`ImplementsBufferClearCopyAndScaledTextureBlitWithCrop` 的 helper shader 仍没有写入目标像素；这是此前已知的 SV/顶点坐标契约问题，不应被本次 texture/input 变更掩盖。
- 真实 Celeste 25 秒诊断：205 个 frame stats，`frame-sequence.jsonl` 约 16 MB；多个 hash 变化且 changed pixels 非零，说明最终 framebuffer 在持续变化。
- 同一运行的输入 trace 显示控制器实际为 `AvaloniaKeyboardDriver`，不是 SDL3 手柄：`controllers=1`、buttons/sticks 全为零。因此本次运行证明了 Npad 更新循环在执行，但没有证明物理手柄事件已到达；手柄断点位于设备/配置选择或 UI 输入驱动层，而不是 Metal。

下一次真实 ROM 诊断应同时启用 frame stats 和 input trace，并检查：

1. 连续 hash 是否变化；
2. changed pixel 是否非零；
3. upload counters 是否增长；
4. SDL sample 是否变化；
5. Npad update 中 buttons/sticks 是否变化。
