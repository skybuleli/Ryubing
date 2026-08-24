# Documents Index

This repo includes several documents that explain both high-level and low-level concepts about Ryujinx and its functions. These are very useful for contributors, to get context that can be very difficult to acquire from just reading code.

Intro to Ryujinx
==================

Ryujinx is an open-source Nintendo Switch emulator, created by gdkchan, written in C#. 
* The CPU emulator, ARMeilleure, emulates an ARMv8 CPU and currently has support for most 64-bit ARMv8 and some of the ARMv7 (and older) instructions.
* The GPU emulator emulates the Switch's Maxwell GPU using either the OpenGL (version 4.5 minimum), Vulkan, or Metal (via MoltenVK) APIs through a custom build of OpenTK or Silk.NET respectively.
* Audio output is entirely supported via C# wrappers for SDL3, with OpenAL & libsoundio as fallbacks.

Getting Started
===============

- [Installing the .NET SDK](https://dotnet.microsoft.com/download)
- [Official .NET Docs](https://docs.microsoft.com/dotnet/core/)

Contributing (Building, testing, benchmarking, profiling, etc.)
===============

If you want to contribute a code change to this repo, start here.

- [Contributor Guide](../CONTRIBUTING.md)

Coding Guidelines
=================

- [C# coding style](coding-guidelines/coding-style.md)
- [Service Implementation Guidelines - WIP](https://gist.github.com/gdkchan/84ba88cd50efbe58d1babfaa7cd7c455)

Project Docs
=================

## P1 Metal — Slang->DXIL->MSC

- [00 主蓝图与验收](p1-metal/00-MASTER.md)
- [01 工具链契约](p1-metal/01-TOOLCHAIN.md)
- [02 架构 GAL与Bridge](p1-metal/02-ARCHITECTURE.md)
- [03 着色器管线](p1-metal/03-SHADER-PIPELINE.md)
- [04 分阶段计划](p1-metal/04-PHASE-PLAN.md)
- [05 验证与验收](p1-metal/05-VERIFICATION.md)
- [Git 工作流](workflow/git-workflow.md) · [分支保护](workflow/branch-protection.md)

To be added. Many project files will contain basic XML docs for key functions and classes in the meantime.
