// MetalDevice.mm - P1-1 存根，P1-3 补齐实现
// 关键约束：
// - NS_PRIVATE_IMPLEMENTATION / MTL_PRIVATE_IMPLEMENTATION 仅在此文件定义一次
// - MTL4Compiler 单例，langVersion = MTLLanguageVersion3_2 (规避 M1 float16 超时)
// - 所有 metal-cpp 调用在 NS::AutoreleasePool 作用域内

// #define NS_PRIVATE_IMPLEMENTATION
// #define MTL_PRIVATE_IMPLEMENTATION
// #include <Metal/Metal.hpp>

#include "MetalDevice.h"

extern "C" {

void* metal_device_create(void) {
    // TODO P1-3: return (void*)MTL::CreateSystemDefaultDevice();
    return nullptr;
}

void metal_device_destroy(void* device) {
    // TODO P1-3: ((MTL::Device*)device)->release();
    (void)device;
}

void metal_release(void* obj) {
    // TODO P1-3: ((NS::Object*)obj)->release();
    (void)obj;
}

}
