// MetalDevice.mm - 真设备实现 (P1-5)
// 单例 MTL4Compiler langVersion=3.2 在此管理，当前 C# 侧已直调 Metal.framework，
// 此文件保留供 libmetal_bridge.a 编译时使用
#import <Metal/Metal.h>
#include "MetalDevice.h"

extern "C" {

void* metal_device_create(void) {
    id<MTLDevice> dev = MTLCreateSystemDefaultDevice();
    return (__bridge_retained void*)dev;
}

void metal_device_destroy(void* device) {
    if (device) CFRelease(device);
}

void metal_release(void* obj) {
    if (obj) CFRelease(obj);
}

}
