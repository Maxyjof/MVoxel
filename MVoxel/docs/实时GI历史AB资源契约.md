# 实时GI历史A/B资源契约

## 目标

为非光追实时GI提供安全的帧间历史混合，保持AMD与NVIDIA Vulkan Compute兼容。当前阶段只定义契约，TemporalWeight保持为0，未完成双缓冲实现前不得启用混合。

## 资源布局

```text
历史A：上一帧只读
历史B：当前帧只写
```

两组资源必须各自拥有独立的`Image`、`DeviceMemory`和`ImageView`，格式继续使用`R8G8B8A8Unorm`，与Shader的`rgba8`保持一致。

Descriptor建议：

- binding 11：历史只读图像
- binding 12：历史当前帧写入图像

如果Vulkan实现不接受同一Compute管线中不同访问限定符的两个Storage Image，应拆分为历史采样Compute管线和输出Compute管线，禁止让同一图像在同一Dispatch中同时读写。

## 每帧流程

1. 检查历史签名，签名包含分辨率、相机变换、工作集版本、GI参数、Volume参数和探针状态
2. 失效时，在提交队列中清零A与B，并将历史有效标记保持为`false`
3. 选择`readIndex`与`writeIndex`
4. 将read图像转换为`General`，源访问为`ShaderRead`
5. 将write图像转换为`General`，目标访问为`ShaderWrite`
6. Dispatch只从read读取、只向write写入
7. 输出完成后，将write作为下一帧read
8. 只有GPU提交顺序和对应栅栏确认后才交换索引

## 同步要求

- 每个环形槽位必须绑定当帧确定的read/write ImageView
- 不得在GPU仍使用槽位时改写该槽位的DescriptorSet
- 槽位复用前等待其Fence，并重置CommandBuffer
- 图像屏障必须覆盖`ShaderReadBit`、`ShaderWriteBit`和`TransferWriteBit`
- 历史清零必须先于本帧Dispatch提交
- 不得依赖隐式布局或队列提交顺序推断跨图像可见性

## TemporalWeight

`P_REALTIME_TEMPORAL_WEIGHT`已预留，默认值为`0`。只有以下条件同时满足后才允许设置为非零：

- A/B图像均已创建并绑定
- Shader完成上一帧读取与当前帧写入
- 失效时两张图像都已清零
- 相机、工作集、分辨率、Volume和探针变化可触发失效
- AMD与NVIDIA设备上完成动态启动和视觉验收

混合公式应为：

```glsl
vec3 current = ComputeRealtimeLighting(...);
vec3 history = imageLoad(previousHistory, pixel).rgb;
vec3 blended = mix(current, history, clamp(temporalWeight, 0.0, 0.99));
imageStore(currentHistory, pixel, vec4(blended, 1.0));
imageStore(outputImage, pixel, vec4(blended, 1.0));
```

此公式仅适用于历史有效且像素运动/深度检查通过的情况；未实现运动或深度拒绝前，不得对所有像素盲目混合。
