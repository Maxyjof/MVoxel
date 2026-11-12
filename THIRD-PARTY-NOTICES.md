# 第三方组件声明

MVoxel 本体以 [MIT 许可证](LICENSE) 开源，Copyright (c) 2026 Maxyjof。

本文件列出随 MVoxel 源码、构建产物和绿色版分发的第三方组件，以及它们各自要求的版权与许可声明。各组件的完整许可文本位于：

- [Licenses/](Licenses/) 目录：本仓库补充的通用许可全文
- 组件自带目录：上游随源码附带的原始许可文件

分发 MVoxel 或绿色版时，请保留本文件与上述许可文本。

## 直接依赖

| 组件 | 版本 | 许可证 | 版权声明 | 许可文件 |
| --- | --- | --- | --- | --- |
| Dear ImGui | v1.91.6 docking (947aa9c9) | MIT | Copyright (c) 2014-2024 Omar Cornut | `MVoxel/Native/ImGuiDocking/imgui/LICENSE.txt` |
| cimgui | 1.91.6dock (970c6148) | MIT | Copyright (c) 2015 Stephan Dilly | `MVoxel/Native/ImGuiDocking/LICENSE` |
| stb_image / stb_truetype / stb_textedit / stb_rectpack | 随 Dear ImGui 内嵌 | MIT 或 Public Domain | Copyright (c) 2017 Sean Barrett | 各头文件内许可段 |
| ImGui.NET | 1.91.6.1 | MIT | Copyright (c) Eric Mellino | `Licenses/MIT.txt` |
| Jint | 4.2.1 | BSD-2-Clause | Copyright (c) Sebastien Ros | [Licenses/BSD-2-Clause.txt](Licenses/BSD-2-Clause.txt) |
| MoonSharp | 2.0.0 | BSD-3-Clause | Copyright (c) 2014-2016 Marco Mastropaolo | [Licenses/BSD-3-Clause.txt](Licenses/BSD-3-Clause.txt) |
| OpenTK 及子包 | 4.9.4 | MIT | Copyright (c) 2006-2020 Stefanos Apostolopoulos | `Licenses/MIT.txt` |
| Silk.NET.Vulkan | 2.23.0 | MIT | .NET Foundation | `Licenses/MIT.txt` |
| Silk.NET.Vulkan.Extensions.KHR | 2.23.0 | MIT | .NET Foundation | `Licenses/MIT.txt` |
| Silk.NET.Shaderc（托管绑定） | 2.23.0 | MIT | .NET Foundation | `Licenses/MIT.txt` |
| Silk.NET.Core | 2.23.0 | MIT | .NET Foundation | `Licenses/MIT.txt` |

## 传递依赖与原生库

| 组件 | 版本 | 许可证 | 版权声明 | 许可文件 |
| --- | --- | --- | --- | --- |
| Acornima（Jint 的 JS 解析器） | 1.1.1 | BSD-3-Clause | Copyright (c) 2024 Adam Simon | [Licenses/BSD-3-Clause.txt](Licenses/BSD-3-Clause.txt) |
| Silk.NET.Shaderc.Native（`shaderc_shared.dll`） | 2.23.0 | Apache-2.0 | Copyright (c) The Shaderc Authors | [Licenses/Apache-2.0.txt](Licenses/Apache-2.0.txt) |
| glslang（静态编入 `shaderc_shared.dll`） | Khronos 14.1.0 | BSD-3-Clause | Copyright (c) The Khronos Group Inc. | [Licenses/BSD-3-Clause.txt](Licenses/BSD-3-Clause.txt) |
| GLFW（`glfw3.dll`，经 OpenTK.redist.glfw 3.4.0.44 分发） | 3.4 | zlib | Copyright (c) 2002-2006 Marcus Geelnard, 2006-2010 Camilla Berglund | [Licenses/zlib.txt](Licenses/zlib.txt) |
| Microsoft.Extensions.DependencyModel | 9.0.9 | MIT | .NET Foundation | `Licenses/MIT.txt` |
| Microsoft.DotNet.PlatformAbstractions | 3.1.6 | MIT | .NET Foundation | `Licenses/MIT.txt` |
| .NET 10 自包含运行时 | 随发布配置 | MIT | .NET Foundation 及各自版权方 | 发布目录内 `LICENSE.txt` |

绿色版由 .NET 自包含发布生成，包内包含 .NET 运行时与上述原生库，按各自许可证分发。

## 字体

| 字体 | 版本 | 许可证 | 版权声明 | 许可文件 |
| --- | --- | --- | --- | --- |
| Sarasa Gothic SC（更纱黑体 SC） | 0.36.0 | SIL OFL 1.1 | Copyright (c) 2015-2025 Renzhi Li (Belleve Invis) 及 Inter、Adobe、Google 等贡献者 | `MVoxel/Editor/Fonts/OFL-SarasaGothic.txt` |
| Smiley Sans Oblique（得意黑斜体） | 2024 版 | SIL OFL 1.1 | Copyright (c) 2022-2024 atelierAnchor | `MVoxel/Editor/Fonts/OFL-SmileySans.txt` |

两款字体均按上游原样分发，未修改字形或名称，保留字体名条款（RFN）未被触发。字体可随本软件自由复制与分发，但不得单独售卖字体本身。

## 图标

| 资产 | 许可证 | 版权声明 | 许可文件 |
| --- | --- | --- | --- |
| Fluent UI System Icons（`MVoxel/Editor/Icons/` 下的 SVG 与 PNG） | MIT | Copyright (c) Microsoft Corporation | [Licenses/MIT-FluentUI.txt](Licenses/MIT-FluentUI.txt) |

图标取自 Microsoft 开源图标集，未修改图形；PNG 是下载 SVG 后在本地栅格化得到的文件。MIT 许可仅授予版权许可，不授予 Microsoft 商标权，项目中未将图标用作 MVoxel 的品牌标识。

## 参考实现

| 组件 | 许可证 | 说明 | 许可文件 |
| --- | --- | --- | --- |
| MrBytesized 的 Vulkan 示例 | MIT | 参考其 C#/Vulkan 初始化与交换链结构，相关代码已按 MVoxel 的架构重写 | `MVoxel/Editor/Rendering/LICENSE.Bytesized.txt` |

## 对第三方源码的修改

MVoxel 对 Dear ImGui 的定制仅限 `MVoxel/Native/ImGuiDocking/imgui/imgui.cpp`，用于停靠命中判定与预览渲染的调整，具体改动见该目录的 `README.md`。其余 vendored 文件保持上游原样。

## 格式说明

`.vox` 文件格式依据社区文档 [MagicaVoxel-file-format-vox.txt](https://github.com/ephtracy/voxel-model/blob/master/MagicaVoxel-file-format-vox.txt) 独立实现，未包含 MagicaVoxel 的代码或资源。MagicaVoxel 是其权利人的商标，本项目与 MagicaVoxel 官方无隶属关系。
