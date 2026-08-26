using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MTLClearColor(double r, double g, double b, double a)
    {
        public readonly double R = r;
        public readonly double G = g;
        public readonly double B = b;
        public readonly double A = a;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct CGSize(double width, double height)
    {
        public readonly double Width = width;
        public readonly double Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct CGRect(double x, double y, double width, double height)
    {
        public readonly double X = x;
        public readonly double Y = y;
        public readonly double Width = width;
        public readonly double Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MTLOrigin(ulong x, ulong y, ulong z)
    {
        public readonly ulong X = x;
        public readonly ulong Y = y;
        public readonly ulong Z = z;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MTLSize(ulong width, ulong height, ulong depth)
    {
        public readonly ulong Width = width;
        public readonly ulong Height = height;
        public readonly ulong Depth = depth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MTLViewport(double originX, double originY, double width, double height, double znear, double zfar)
    {
        public readonly double OriginX = originX;
        public readonly double OriginY = originY;
        public readonly double Width = width;
        public readonly double Height = height;
        public readonly double ZNear = znear;
        public readonly double ZFar = zfar;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MTLScissorRect(ulong x, ulong y, ulong width, ulong height)
    {
        // MTLScissorRect uses NSUInteger on macOS, which is 64-bit on arm64.
        public readonly ulong X = x;
        public readonly ulong Y = y;
        public readonly ulong Width = width;
        public readonly ulong Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MTLRange(ulong location, ulong length)
    {
        public readonly ulong Location = location;
        public readonly ulong Length = length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MTLRegion(MTLOrigin origin, MTLSize size)
    {
        public readonly MTLOrigin Origin = origin;
        public readonly MTLSize Size = size;
    }

    /// <summary>
    /// Metal/Objective-C 运行时互操作。所有复杂对象仍由 MetalContext/MetalTexture 持有句柄。
    /// </summary>
    internal static unsafe partial class MetalNative
    {
        private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";
        private const string LibSystem = "/usr/lib/libSystem.B.dylib";
        private const string MetalFramework = "/System/Library/Frameworks/Metal.framework/Metal";
        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [LibraryImport(ObjCRuntime, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint objc_getClass(string name);

        [LibraryImport(ObjCRuntime, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint sel_getUid(string name);

        [LibraryImport(ObjCRuntime)]
        internal static partial nint objc_autoreleasePoolPush();

        [LibraryImport(ObjCRuntime)]
        internal static partial void objc_autoreleasePoolPop(nint pool);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, nint arg);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, nint arg1, ulong arg2);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, nint arg1, ulong arg2, ulong arg3);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, ulong arg1, ulong arg2);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, ulong arg1, ulong arg2, ulong arg3);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, ulong arg1, ulong arg2, ulong arg3, nint arg4, ulong arg5);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, nint arg1, ulong arg2, ulong arg3, MTLSize arg4, MTLSize arg5);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, MTLSize arg1, MTLSize arg2);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, nint arg1, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, ulong arg1, ulong arg2, ulong arg3, nint arg4, ulong arg5, ulong arg6, ulong arg7, ulong arg8);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, ulong arg1, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(
            nint receiver,
            nint selector,
            ulong primitive,
            ulong indexCount,
            ulong indexType,
            nint indexBuffer,
            ulong indexBufferOffset,
            ulong instanceCount,
            nint baseVertex,
            ulong baseInstance);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, ulong arg);


        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, byte arg);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, double arg);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, double arg1, double arg2, double arg3, double arg4);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, double arg1, double arg2, double arg3);

        // setBlendColorRed:green:blue:alpha: 与 setDepthBias:slopeScale:clamp: 的 ObjC
        // 签名是 float（arm64 上走 s0..s3 寄存器）。传 double 会读到乱码，必须用 float 重载。
        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, float arg1, float arg2, float arg3, float arg4);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendFloat3(nint receiver, nint selector, float arg1, float arg2, float arg3);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, MTLClearColor clearColor);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, CGSize size);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, MTLViewport viewport);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, MTLScissorRect scissorRect);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, MTLRegion region, ulong mipmapLevel, nint bytes, ulong bytesPerRow);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(nint receiver, nint selector, MTLRegion region, ulong mipmapLevel, ulong slice, nint bytes, ulong bytesPerRow);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(
            nint receiver,
            nint selector,
            nint sourceTexture,
            ulong sourceSlice,
            ulong sourceLevel,
            MTLOrigin sourceOrigin,
            MTLSize sourceSize,
            nint destinationTexture,
            ulong destinationSlice,
            ulong destinationLevel,
            MTLOrigin destinationOrigin);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial void SendVoid(
            nint receiver,
            nint selector,
            nint sourceTexture,
            ulong sourceSlice,
            ulong sourceLevel,
            MTLOrigin sourceOrigin,
            MTLSize sourceSize,
            nint destinationBuffer,
            ulong destinationOffset,
            ulong destinationBytesPerRow,
            ulong destinationBytesPerImage);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial nint SendObject(nint receiver, nint selector);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial nint SendObject(nint receiver, nint selector, nint arg);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial nint SendObject(nint receiver, nint selector, nint arg1, nint arg2);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial nint SendObject(nint receiver, nint selector, ulong arg);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial nint SendObject(nint receiver, nint selector, ulong arg1, ulong arg2);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial nint SendObject(nint receiver, nint selector, ulong arg1, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial nint SendObject(nint receiver, nint selector, nint arg1, nint arg2, nint arg3);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial nint SendObject(nint receiver, nint selector, nint arg1, ulong arg2, ulong arg3);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial nint SendObject(nint receiver, nint selector, ulong pixelFormat, ulong textureType, MTLRange levels, MTLRange slices);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial ulong SendULong(nint receiver, nint selector);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial nint SendNInt(nint receiver, nint selector);

        // ObjC BOOL 是 signed char，按 byte 返回避免布尔编组歧义。
        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial byte SendByte(nint receiver, nint selector);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial byte SendByte(nint receiver, nint selector, ulong arg);

        [LibraryImport(LibSystem, EntryPoint = "dispatch_data_create")]
        internal static partial nint dispatch_data_create(nint buffer, nuint size, nint queue, nint destructor);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        internal static partial nint SendObject(
            nint receiver,
            nint selector,
            ulong arg1,
            ulong arg2,
            ulong arg3,
            ulong arg4);

        [LibraryImport(MetalFramework)]
        internal static partial nint MTLCreateSystemDefaultDevice();

        // CGWindowListCreateImage returns a retained CGImageRef. The image is released
        // with CGImageRelease after its pixels have been copied.
        [LibraryImport(CoreGraphics)]
        internal static partial nint CGWindowListCreateImage(CGRect windowBounds, uint options, uint windowId, uint imageOptions);

        [LibraryImport(CoreGraphics)]
        internal static partial nint CGImageGetDataProvider(nint image);

        [LibraryImport(CoreGraphics)]
        internal static partial nint CGDataProviderCopyData(nint provider);

        [LibraryImport(CoreFoundation)]
        internal static partial nuint CFDataGetLength(nint data);

        [LibraryImport(CoreFoundation)]
        internal static partial nint CFDataGetBytePtr(nint data);

        [LibraryImport(CoreGraphics)]
        internal static partial nuint CGImageGetWidth(nint image);

        [LibraryImport(CoreGraphics)]
        internal static partial nuint CGImageGetHeight(nint image);

        [LibraryImport(CoreGraphics)]
        internal static partial nuint CGImageGetBytesPerRow(nint image);

        [LibraryImport(CoreGraphics)]
        internal static partial void CGImageRelease(nint image);

        [LibraryImport(CoreFoundation)]
        internal static partial void CFRelease(nint value);

        internal static nint Sel(string name) => sel_getUid(name);

        internal static nint Class(string name) => objc_getClass(name);
    }
}
