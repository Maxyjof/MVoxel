# MVoxel 停靠交互

上游 cimgui 1.91.6dock（970c614802935f51f451aa21ae06e838bdcf9349）及 Dear ImGui（947aa9c9722bd6ff740dd757da609ff41f4d3ba3），与 ImGui.NET 1.91.6.1 配套。许可证见 LICENSE 和 imgui/LICENSE.txt。
https://github.com/cimgui/cimgui/tree/1.91.6dock

定制仅在 imgui/imgui.cpp：DockNodeCalcDropRectsAndTestMousePos 使用窄边缘带命中，仅实际标签栏或标题栏合并标签，正文区域不触发停靠，角落取最近边；DockNodePreviewDockRender 移除五向方格，保留目标区域和标签预览。布局保存和分隔条沿用原实现。

构建和发布自动编译并复制定制 DLL，不修改 NuGet 缓存。需要 VS 2022 C++ x64 构建工具和 CMake，目前仅支持 win-x64。升级 ImGui.NET 时必须同步更新源码和 ABI。
