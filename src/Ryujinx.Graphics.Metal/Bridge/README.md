# Bridge

C++ 侧 `libmetal_bridge.a` 占位，P1-1 仅保证目录与头文件存在，不参与 `dotnet build`。

- `MetalDevice.h/mm`：单例 `MTL4Compiler`，`metal_release(void*)` 统一析构，P1-3 实现。
- 编译：`clang++ -std=c++20 -framework Metal -framework Foundation -c MetalDevice.mm -o MetalDevice.o`
- 归档：`ar rcs libmetal_bridge.a MetalDevice.o`
- C# 侧 P/Invoke 仅在 `MetalRenderer` P1-3 阶段启用。
