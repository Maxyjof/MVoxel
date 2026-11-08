# MVoxel UI 字体

这里内置两款 SIL Open Font License 1.1 中文字体，随程序分发，不依赖用户系统字体：

- `SarasaGothicSC-Regular.ttf`：更纱黑体简体常规，正文、菜单、检查器与所有控件文字
- `SmileySans-Oblique.ttf`：得意黑斜体，欢迎界面品牌标题

来源：

- 更纱黑体：<https://github.com/be5invis/Sarasa-Gothic>
- 得意黑：<https://github.com/atelier-anchor/smiley-sans>

授权文本见同目录 `OFL-SarasaGothic.txt` 与 `OFL-SmileySans.txt`，采用 SIL Open Font License 1.1，可随程序自由分发，不修改 Windows 全局字体。

## 字号规则

- 正文基准 13px，标题基准 16px，均乘以当前 DPI 缩放，按 0.5px 取整
- 横纵过采样 2 倍，关闭像素对齐，保证高 DPI 下中文笔画和斜线干净
- 正文加载完整 BMP 中日韩范围，避免项目名和说明里出现缺字方块；标题只加载拉丁字符
- 字体变化只在上一帧结束后、新一帧开始前执行；每个原生窗口分别持有字体和 ImGui 上下文
- 后端必须用 `ClientSize` 作为界面尺寸，不能使用包含标题栏与边框的 `Size`

技术依据：[Dear ImGui 1.91.6 字体文档](https://github.com/ocornut/imgui/blob/v1.91.6/docs/FONTS.md)、[字体构建实现](https://github.com/ocornut/imgui/blob/v1.91.6/imgui_draw.cpp)。
