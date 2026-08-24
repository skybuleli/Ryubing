# Bridge

C++ 侧 `libmetal_bridge.a` 占位，P1-1 仅保证目录与头文件存在，P1-3 改为 CLI 间调。

- `MetalDevice.h/mm`：单例 `MTL4Compiler`，`metal_release(void*)` 统一析构，P1-3 当前通过 `metal-shaderconverter` CLI 间接调用 `libmetalirconverter.dylib`，下一迭代直连 `IRCompilerCreate/IRCompilerCompile` P/Invoke。
- 编译：`clang++ -std=c++20 -framework Metal -framework Foundation -c MetalDevice.mm -o MetalDevice.o`
- 归档：`ar rcs libmetal_bridge.a MetalDevice.o`
- C# 侧 `MetalDevice.cs` 管理单例句柄，P1-3 仅占位 `nint.Zero`，`langVersion=3.2` 约束在 C# 侧记录。
