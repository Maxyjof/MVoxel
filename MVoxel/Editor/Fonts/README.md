# MVoxel UI 字体

这里内置了 Fusion Pixel Font 的简体中文比例模式字体，用于 MVoxel 的 ImGui 界面：

- `FusionPixel-8px-SC.ttf`：保留给紧凑辅助用途，不用于放大的中文正文
- `FusionPixel-10px-SC.ttf`：可选整数倍字号档位
- `FusionPixel-12px-SC.ttf`：默认正文与欢迎界面

来源：<https://github.com/TakWolf/fusion-pixel-font/releases/tag/2026.07.20>

该字体项目的字体文件采用 SIL Open Font License 1.1，完整授权文本见同目录的 `OFL.txt`。字体版本提供 8px、10px、12px 三种尺寸和简体中文 `zh_hans` 字形；应用随包携带字体，不依赖用户机器上的系统字体，也不会修改 Windows 的全局字体安装。

## 清晰度规则

默认 12px 指字形像素网格，不是 ImGui/stb_truetype 的整行高度。这批字体的 `hhea` 上升部减下降部为 `(字形尺寸 + 4) * 100`，每像素 100 单位。因此 12px 字形应以 16px 行高加载，10px 字形以 14px 行高加载。

- 横纵过采样均为 1，开启水平像素对齐，字体纹理使用最近邻采样。
- 不用 `FontGlobalScale` 做小数缩放。正文从 10px、12px 的整数倍档位中选择接近 DPI 的字号；125% 保留 12px 正文，避免放大 8px 中文后笔画难以辨认。
- 正文字库包含完整 BMP 中日韩常用范围，避免简体常用子集遗漏“渲”等界面字符。大号品牌标题只加载拉丁字符，减少图集内存。
- 字体变化只在上一帧结束后、新一帧开始前执行；每个原生窗口分别持有字体和 ImGui 上下文。
- 后端必须用 `ClientSize` 作为界面尺寸，不能使用包含标题栏与边框的 `Size`。

技术依据：[Dear ImGui 1.91.6 字体文档](https://github.com/ocornut/imgui/blob/v1.91.6/docs/FONTS.md)、[字体构建实现](https://github.com/ocornut/imgui/blob/v1.91.6/imgui_draw.cpp)。
