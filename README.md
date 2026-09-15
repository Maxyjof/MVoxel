# MVoxel

MVoxel 是一个面向创作者的体素建模与渲染编辑器。它使用纯色体素和材质属性构建场景，并提供场景编辑、实时渲染、项目模板、插件和命令行工具。

## 快速开始

1. 下载并解压绿色版，双击 `MVoxel.exe`。
2. 在欢迎界面选择“新建项目”或“打开项目”。
3. 进入场景后，在层级中选择对象；右侧检查器用于编辑对象名称、坐标和组件。
4. 在场景窗口中使用左键放置、右键环绕、中键平移；按住 Shift 可进行破坏或加速，具体取决于编辑器设置中的操作模式。
5. 使用“项目 → 保存项目”保存场景和项目配置。

首次使用请阅读[入门教程](Docs/入门教程/索引.md)。构建、测试、插件运行时和渲染管线等开发内容统一放在[技术文档](Docs/技术文档/索引.md)中。

## 文档

- [入门教程](Docs/入门教程/索引.md)：从创建项目到编辑体素场景
- [技术文档](Docs/技术文档/索引.md)：架构、数据、渲染、编辑器、插件和工程规范
- [更新日志](Docs/更新日志/索引.md)：按日期查看已完成的功能和修复
- [插件开发指南](Docs/插件文档/插件开发指南.md)：使用 JavaScript 或 Lua 编写编辑器插件

## 当前能力

- Vulkan 编辑器窗口和 GPU 体素渲染
- 场景视图与渲染视图分离，使用统一的停靠布局
- 纯色体素、材质属性、调色板和代码生成的示例模板
- 稀疏体素世界、场景层级、相机、撤销重做和 PNG 导出
- 可选 Vulkan 硬件光追，默认使用 Compute 路径
- 编辑器级 JavaScript/Lua 插件、项目级启停、命令行和 MCP 控制

## 验证

```powershell
dotnet build .\MVoxel.sln --no-restore --configuration Debug -m:1
dotnet run --project .\MVoxel.Tests\MVoxel.Tests.csproj --no-build --configuration Debug
powershell -NoProfile -ExecutionPolicy Bypass -File .\Scripts\Test-Docs.ps1
```

开发约定集中在[编码规范](Docs/技术文档/11-编码规范.md)中。项目采用直接、清晰的当前实现，功能边界以技术文档和测试结果为准。
