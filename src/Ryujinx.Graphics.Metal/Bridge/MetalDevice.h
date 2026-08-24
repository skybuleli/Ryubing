#pragma once
// Bridge/MetalDevice.h - P1-1 占位
// 单例 MTL4Compiler，langVersion=3.2，NS_PRIVATE_IMPLEMENTATION 仅在此定义
// P1-3 实现：NS::AutoreleasePool 包裹，metal_release(void*) 统一析构

#ifdef __OBJC__
#import <Metal/Metal.h>
#endif

#ifdef __cplusplus
extern "C" {
#endif

// Opaque handle C ABI
void* metal_device_create(void);
void metal_device_destroy(void* device);
void metal_release(void* obj); // 统一析构

// P1-3: metallib 编译
// void* metal_compile_metallib(void* device, const void* dxilData, size_t dxilSize, size_t* outSize);

#ifdef __cplusplus
}
#endif
