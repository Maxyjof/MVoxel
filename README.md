# MVoxel

MVoxel 是一个面向创作者的体素建模与渲染编辑器。它使用纯色体素和材质属性构建场景，并提供场景编辑、实时渲染、项目模板、插件和命令行工具。

## 快速开始

1. 安装 .NET 10 SDK
2. 构建解决方案：`dotnet build .\MVoxel.sln --configuration Debug`
3. 启动编辑器：`dotnet run --project .\MVoxel\MVoxel.csproj`

## 文档

- [入门教程](Docs/入门教程/索引.md)：从创建项目到编辑体素场景
- [技术文档](Docs/技术文档/索引.md)：架构、数据、渲染、编辑器、插件和工程规范
- [更新日志](Docs/更新日志/索引.md)：按日期查看已完成的功能和修复
- [插件开发指南](Docs/插件文档/插件开发指南.md)：使用 JavaScript 编写编辑器插件

## 当前能力

- Vulkan 编辑器窗口和 GPU 体素渲染
- 场景视图与渲染视图分离，使用统一的停靠布局
- 纯色体素、材质属性、调色板和代码生成的示例模板
- 稀疏体素世界、场景层级、相机、撤销重做和 PNG 导出
- 可选 Vulkan 硬件光追，默认使用 Compute 路径
- 编辑器级 JavaScript 插件、项目级启停、命令行和 MCP 控制

## 验证

```powershell
dotnet build .\MVoxel.sln --no-restore --configuration Debug -m:1
dotnet run --project .\MVoxel.Tests\MVoxel.Tests.csproj --no-build --configuration Debug
powershell -NoProfile -ExecutionPolicy Bypass -File .\Scripts\Test-Docs.ps1
```

开发约定集中在[编码规范](Docs/技术文档/11-编码规范.md)中。项目采用直接、清晰的当前实现，功能边界以技术文档和测试结果为准。
