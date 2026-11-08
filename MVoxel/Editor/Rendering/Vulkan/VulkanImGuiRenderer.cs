using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using MVoxel.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Shaderc;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MVoxel.Editor.Rendering.Vulkan;

/// <summary>
/// Dear ImGui 的 Vulkan 绘制后端
/// </summary>
internal sealed unsafe class VulkanImGuiRenderer : IDisposable
{
    private const int PushConstantSize = 16;
    private const ulong MaximumDrawBufferCapacity = 256UL * 1024UL * 1024UL;
    private readonly VulkanWindowContext _context;
    private readonly Vk _api;
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Pipeline _pipeline;
    private readonly PipelineLayout _pipelineLayout;
    private readonly DescriptorSetLayout _textureDescriptorSetLayout;
    private readonly DescriptorSetLayout _samplerDescriptorSetLayout;
    private readonly DescriptorPool _descriptorPool;
    private readonly DescriptorSet _textureDescriptorSet;
    private readonly DescriptorSet _samplerDescriptorSet;
    private readonly Sampler _fontSampler;
    private readonly Image _fontImage;
    private readonly DeviceMemory _fontImageMemory;
    private readonly ImageView _fontImageView;
    private VkBuffer _vertexBuffer;
    private DeviceMemory _vertexMemory;
    private VkBuffer _indexBuffer;
    private DeviceMemory _indexMemory;
    private readonly Dictionary<nint, TextureResource> _textures = new();
    private ulong _vertexCapacity;
    private ulong _indexCapacity;
    private long _nextTextureId = (long)VulkanImGuiController.FontTextureId;
    private bool _disposed;

    /// <summary>
    /// 创建 ImGui 的字体纹理、缓冲区、描述符和图形管线
    /// </summary>
    internal VulkanImGuiRenderer(VulkanWindowContext context, VulkanImGuiController controller)
    {
        _context = context;
        _api = context.Api;
        _device = context.Device;
        _physicalDevice = context.PhysicalDevice;
        _vertexCapacity = 16UL * 1024UL * 1024UL;
        _indexCapacity = 8UL * 1024UL * 1024UL;
        (_vertexBuffer, _vertexMemory) = CreateBuffer(
            _vertexCapacity,
            BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        (_indexBuffer, _indexMemory) = CreateBuffer(
            _indexCapacity,
            BufferUsageFlags.IndexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        (_fontImage, _fontImageMemory) = CreateFontImage(controller);
        _fontImageView = CreateImageView(_fontImage, Format.R8G8B8A8Unorm);
        _fontSampler = CreateFontSampler();
        _textureDescriptorSetLayout = CreateDescriptorSetLayout(DescriptorType.SampledImage);
        _samplerDescriptorSetLayout = CreateDescriptorSetLayout(DescriptorType.Sampler);
        _descriptorPool = CreateDescriptorPool();
        _textureDescriptorSet = AllocateDescriptorSet(_textureDescriptorSetLayout);
        _samplerDescriptorSet = AllocateDescriptorSet(_samplerDescriptorSetLayout);
        UpdateFontDescriptor();
        _pipelineLayout = CreatePipelineLayout();
        _pipeline = CreatePipeline();
    }

    /// <summary>
    /// 注册一张供 ImGui 使用的 Vulkan RGBA 纹理
    /// </summary>
    /// <param name="pixels">连续排列的 RGBA8 像素</param>
    /// <param name="width">纹理宽度</param>
    /// <param name="height">纹理高度</param>
    /// <returns>可传给 ImGui.Image 的纹理句柄</returns>
    internal nint RegisterTexture(byte[] pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("Vulkan 纹理像素尺寸不匹配", nameof(pixels));
        }

        var (image, memory) = CreateTextureImage(pixels, (uint)width, (uint)height);
        var view = CreateImageView(image, Format.R8G8B8A8Unorm);
        var textureSet = AllocateDescriptorSet(_textureDescriptorSetLayout);
        UpdateTextureDescriptor(textureSet, view);
        var id = (nint)Interlocked.Increment(ref _nextTextureId);
        _textures.Add(id, new TextureResource(image, memory, view, textureSet, true));
        return id;
    }

    /// <summary>
    /// 注册由其他 Vulkan 管线拥有的采样图像
    /// </summary>
    /// <param name="imageView">已转换为着色器只读布局的图像视图</param>
    /// <returns>可传给 ImGui.Image 的纹理句柄</returns>
    internal nint RegisterExternalTexture(ImageView imageView)
    {
        if (imageView.Handle == 0)
        {
            throw new ArgumentException("Vulkan 外部图像视图为空", nameof(imageView));
        }

        var textureSet = AllocateDescriptorSet(_textureDescriptorSetLayout);
        UpdateTextureDescriptor(textureSet, imageView);
        var id = (nint)Interlocked.Increment(ref _nextTextureId);
        _textures.Add(id, new TextureResource(default, default, imageView, textureSet, false));
        return id;
    }

    /// <summary>
    /// 注销外部管线使用的 ImGui 纹理描述符
    /// </summary>
    /// <param name="textureId">纹理句柄</param>
    internal void UnregisterTexture(nint textureId)
    {
        if (!_textures.Remove(textureId, out var texture))
        {
            return;
        }

        // ImGui 的上一帧命令可能仍在采样这张图。释放描述符和图像前等待
        // 当前设备完成，避免资源切换时出现 Vulkan use-after-free/设备丢失。
        _api.DeviceWaitIdle(_device);
        var descriptorSet = texture.TextureSet;
        _api.FreeDescriptorSets(_device, _descriptorPool, 1, in descriptorSet);
        if (texture.OwnsImage)
        {
            _api.DestroyImageView(_device, texture.View, null);
            _api.DestroyImage(_device, texture.Image, null);
            _api.FreeMemory(_device, texture.Memory, null);
        }
    }

    /// <summary>
    /// 把当前 ImGui 绘制数据记录到 Vulkan 命令缓冲
    /// </summary>
    internal void Record(CommandBuffer commandBuffer, VulkanImGuiController controller)
    {
        ImGui.SetCurrentContext(controller.Context);
        ImDrawDataPtr drawData = ImGui.GetDrawData();
        if (drawData.NativePtr is null || drawData.TotalVtxCount <= 0 || drawData.TotalIdxCount <= 0)
        {
            return;
        }

        ulong vertexBytes = checked((ulong)drawData.TotalVtxCount * (ulong)Unsafe.SizeOf<ImDrawVert>());
        ulong indexBytes = checked((ulong)drawData.TotalIdxCount * sizeof(ushort));
        EnsureDrawBuffers(vertexBytes, indexBytes);

        UploadDrawData(drawData, vertexBytes, indexBytes);
        _api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);
        Viewport viewport = new(0, 0, _context.Extent.Width, _context.Extent.Height, 0, 1);
        _api.CmdSetViewport(commandBuffer, 0, 1, in viewport);
        ulong vertexOffset = 0;
        _api.CmdBindVertexBuffers(commandBuffer, 0, 1, in _vertexBuffer, in vertexOffset);
        _api.CmdBindIndexBuffer(commandBuffer, _indexBuffer, 0, IndexType.Uint16);
        float displayWidth = Math.Max(drawData.DisplaySize.X, 1F);
        float displayHeight = Math.Max(drawData.DisplaySize.Y, 1F);
        Vector2 scale = new(2F / displayWidth, 2F / displayHeight);
        Vector2 translate = new(-1F, -1F);
        Span<float> pushConstants = stackalloc float[] { scale.X, scale.Y, translate.X, translate.Y };
        fixed (float* pushPointer = pushConstants)
        {
            _api.CmdPushConstants(commandBuffer, _pipelineLayout, ShaderStageFlags.VertexBit, 0, PushConstantSize, pushPointer);
        }

        int vertexBase = 0;
        int indexBase = 0;
        DescriptorSet* descriptorSets = stackalloc DescriptorSet[2];
        for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            ImDrawListPtr drawList = drawData.CmdLists[listIndex];
            for (int commandIndex = 0; commandIndex < drawList.CmdBuffer.Size; commandIndex++)
            {
                ImDrawCmdPtr drawCommand = drawList.CmdBuffer[commandIndex];
                int clipLeft = Math.Clamp((int)MathF.Floor(drawCommand.ClipRect.X), 0, (int)_context.Extent.Width);
                int clipTop = Math.Clamp((int)MathF.Floor(drawCommand.ClipRect.Y), 0, (int)_context.Extent.Height);
                int clipRight = Math.Clamp((int)MathF.Ceiling(drawCommand.ClipRect.Z), 0, (int)_context.Extent.Width);
                int clipBottom = Math.Clamp((int)MathF.Ceiling(drawCommand.ClipRect.W), 0, (int)_context.Extent.Height);
                if (clipRight <= clipLeft || clipBottom <= clipTop)
                {
                    continue;
                }

                Rect2D scissor = new(
                    new Offset2D(clipLeft, clipTop),
                    new Extent2D((uint)(clipRight - clipLeft), (uint)(clipBottom - clipTop)));
                _api.CmdSetScissor(commandBuffer, 0, 1, in scissor);
                var textureSet = _textureDescriptorSet;
                if (drawCommand.TextureId != IntPtr.Zero && drawCommand.TextureId != VulkanImGuiController.FontTextureId)
                {
                    // 字体图集也使用非零句柄，文字与面板的白像素均依赖它。
                    // 其他句柄才属于注册图像；过期图像仍不能回退到字体图集。
                    if (!_textures.TryGetValue(drawCommand.TextureId, out var texture)) continue;
                    textureSet = texture.TextureSet;
                }

                descriptorSets[0] = textureSet;
                descriptorSets[1] = _samplerDescriptorSet;
                _api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout, 0, 2, descriptorSets, 0, null);
                _api.CmdDrawIndexed(
                    commandBuffer,
                    drawCommand.ElemCount,
                    1,
                    (uint)(indexBase + (int)drawCommand.IdxOffset),
                    vertexBase + (int)drawCommand.VtxOffset,
                    0);
            }

            vertexBase += drawList.VtxBuffer.Size;
            indexBase += drawList.IdxBuffer.Size;
        }
    }

    /// <summary>
    /// 释放 ImGui Vulkan 资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _api.DeviceWaitIdle(_device);
        _api.DestroyPipeline(_device, _pipeline, null);
        _api.DestroyPipelineLayout(_device, _pipelineLayout, null);
        foreach (var texture in _textures.Values)
        {
            if (texture.OwnsImage)
            {
                _api.DestroyImageView(_device, texture.View, null);
                _api.DestroyImage(_device, texture.Image, null);
                _api.FreeMemory(_device, texture.Memory, null);
            }
        }

        _api.DestroyDescriptorPool(_device, _descriptorPool, null);
        _api.DestroyDescriptorSetLayout(_device, _samplerDescriptorSetLayout, null);
        _api.DestroyDescriptorSetLayout(_device, _textureDescriptorSetLayout, null);
        _api.DestroySampler(_device, _fontSampler, null);
        _api.DestroyImageView(_device, _fontImageView, null);
        _api.DestroyImage(_device, _fontImage, null);
        _api.FreeMemory(_device, _fontImageMemory, null);
        _api.DestroyBuffer(_device, _vertexBuffer, null);
        _api.FreeMemory(_device, _vertexMemory, null);
        _api.DestroyBuffer(_device, _indexBuffer, null);
        _api.FreeMemory(_device, _indexMemory, null);
    }

    /// <summary>
    /// 把 ImGui 顶点和索引数据写入主机可见缓冲区
    /// </summary>
    private void UploadDrawData(ImDrawDataPtr drawData, ulong vertexBytes, ulong indexBytes)
    {
        void* vertexMapped = null;
        void* indexMapped = null;
        Check(_api.MapMemory(_device, _vertexMemory, 0, vertexBytes, 0, &vertexMapped), "映射 ImGui 顶点缓冲");
        Check(_api.MapMemory(_device, _indexMemory, 0, indexBytes, 0, &indexMapped), "映射 ImGui 索引缓冲");
        try
        {
            byte* vertexTarget = (byte*)vertexMapped;
            byte* indexTarget = (byte*)indexMapped;
            ulong vertexOffset = 0;
            ulong indexOffset = 0;
            for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
            {
                ImDrawListPtr drawList = drawData.CmdLists[listIndex];
                ulong listVertexBytes = checked((ulong)drawList.VtxBuffer.Size * (ulong)Unsafe.SizeOf<ImDrawVert>());
                ulong listIndexBytes = checked((ulong)drawList.IdxBuffer.Size * sizeof(ushort));
                for (int vertexIndex = 0; vertexIndex < drawList.VtxBuffer.Size; vertexIndex++)
                {
                    ImDrawVertPtr vertex = drawList.VtxBuffer[vertexIndex];
                    *(ImDrawVert*)(vertexTarget + vertexOffset + (ulong)vertexIndex * (ulong)Unsafe.SizeOf<ImDrawVert>()) =
                        *vertex.NativePtr;
                }

                for (int index = 0; index < drawList.IdxBuffer.Size; index++)
                {
                    *(ushort*)(indexTarget + indexOffset + (ulong)index * sizeof(ushort)) = drawList.IdxBuffer[index];
                }
                vertexOffset += listVertexBytes;
                indexOffset += listIndexBytes;
            }
        }
        finally
        {
            _api.UnmapMemory(_device, _vertexMemory);
            _api.UnmapMemory(_device, _indexMemory);
        }
    }

    /// <summary>
    /// 返回不小于需求的二次幂容量。绘制数据偶尔暴增时只扩容，不在每帧反复分配。
    /// </summary>
    internal static ulong GrowBufferCapacity(ulong current, ulong required)
    {
        if (required <= current) return current;
        var capacity = Math.Max(current, 1UL);
        while (capacity < required)
        {
            capacity = checked(capacity * 2UL);
        }

        return capacity;
    }

    /// <summary>
    /// 按当前 ImGui 绘制数据扩容上传缓冲区。扩容只发生在需求超过现有容量时，
    /// 并在替换前等待设备完成，避免上一帧命令仍引用旧缓冲区。
    /// </summary>
    private void EnsureDrawBuffers(ulong vertexBytes, ulong indexBytes)
    {
        var nextVertexCapacity = GrowBufferCapacity(_vertexCapacity, vertexBytes);
        var nextIndexCapacity = GrowBufferCapacity(_indexCapacity, indexBytes);
        if (nextVertexCapacity == _vertexCapacity && nextIndexCapacity == _indexCapacity)
        {
            return;
        }

        if (nextVertexCapacity > MaximumDrawBufferCapacity || nextIndexCapacity > MaximumDrawBufferCapacity)
        {
            throw new InvalidOperationException(
                $"ImGui 绘制数据超过 Vulkan 上传缓冲区上限（顶点 {vertexBytes:N0} 字节，索引 {indexBytes:N0} 字节）");
        }

        _api.DeviceWaitIdle(_device);
        VkBuffer replacementVertex = default;
        DeviceMemory replacementVertexMemory = default;
        VkBuffer replacementIndex = default;
        DeviceMemory replacementIndexMemory = default;
        try
        {
            if (nextVertexCapacity != _vertexCapacity)
            {
                (replacementVertex, replacementVertexMemory) = CreateBuffer(
                    nextVertexCapacity,
                    BufferUsageFlags.VertexBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            }

            if (nextIndexCapacity != _indexCapacity)
            {
                (replacementIndex, replacementIndexMemory) = CreateBuffer(
                    nextIndexCapacity,
                    BufferUsageFlags.IndexBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            }

            if (nextVertexCapacity != _vertexCapacity)
            {
                _api.DestroyBuffer(_device, _vertexBuffer, null);
                _api.FreeMemory(_device, _vertexMemory, null);
                _vertexBuffer = replacementVertex;
                _vertexMemory = replacementVertexMemory;
                _vertexCapacity = nextVertexCapacity;
                replacementVertex = default;
                replacementVertexMemory = default;
            }

            if (nextIndexCapacity != _indexCapacity)
            {
                _api.DestroyBuffer(_device, _indexBuffer, null);
                _api.FreeMemory(_device, _indexMemory, null);
                _indexBuffer = replacementIndex;
                _indexMemory = replacementIndexMemory;
                _indexCapacity = nextIndexCapacity;
                replacementIndex = default;
                replacementIndexMemory = default;
            }

            MVoxelLog.Info($"ImGui Vulkan 上传缓冲区扩容：顶点 {_vertexCapacity / 1024 / 1024} MiB，索引 {_indexCapacity / 1024 / 1024} MiB");
        }
        finally
        {
            if (replacementVertex.Handle != 0)
            {
                _api.DestroyBuffer(_device, replacementVertex, null);
                _api.FreeMemory(_device, replacementVertexMemory, null);
            }

            if (replacementIndex.Handle != 0)
            {
                _api.DestroyBuffer(_device, replacementIndex, null);
                _api.FreeMemory(_device, replacementIndexMemory, null);
            }
        }
    }

    /// <summary>
    /// 创建字体图像并上传字体像素
    /// </summary>
    private (Image Image, DeviceMemory Memory) CreateFontImage(VulkanImGuiController controller)
    {
        return CreateTextureImage(controller.FontPixels, (uint)controller.FontWidth, (uint)controller.FontHeight);
    }

    /// <summary>
    /// 创建并上传一张设备本地 Vulkan 纹理
    /// </summary>
    private (Image Image, DeviceMemory Memory) CreateTextureImage(byte[] pixels, uint width, uint height)
    {
        ImageCreateInfo createInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        Image image = CreateImage(createInfo);
        MemoryRequirements requirements = default;
        _api.GetImageMemoryRequirements(_device, image, &requirements);
        DeviceMemory memory = AllocateMemory(requirements, MemoryPropertyFlags.DeviceLocalBit);
        Check(_api.BindImageMemory(_device, image, memory, 0), "绑定 ImGui 字体图像内存");
        (VkBuffer stagingBuffer, DeviceMemory stagingMemory) = CreateBuffer(
            (ulong)pixels.Length,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        void* mapped = null;
        Check(_api.MapMemory(_device, stagingMemory, 0, (ulong)pixels.Length, 0, &mapped),
            "映射 Vulkan 纹理暂存缓冲");
        fixed (byte* source = pixels)
        {
            System.Buffer.MemoryCopy(source, mapped, pixels.Length, pixels.Length);
        }

        _api.UnmapMemory(_device, stagingMemory);
        _context.ExecuteImmediate(commandBuffer => RecordFontImageUpload(
            commandBuffer, image, stagingBuffer, width, height));
        _api.DestroyBuffer(_device, stagingBuffer, null);
        _api.FreeMemory(_device, stagingMemory, null);

        return (image, memory);
    }

    /// <summary>
    /// 记录字体图像布局转换命令
    /// </summary>
    private void RecordFontImageTransition(CommandBuffer commandBuffer, Image image)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            DstAccessMask = AccessFlags.TransferWriteBit
        };
        _api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in barrier);
    }

    /// <summary>
    /// 记录字体图像上传和最终布局转换
    /// </summary>
    private void RecordFontImageUpload(CommandBuffer commandBuffer, Image image, VkBuffer stagingBuffer,
        uint width, uint height)
    {
        ImageMemoryBarrier toTransfer = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            DstAccessMask = AccessFlags.TransferWriteBit
        };
        _api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in toTransfer);
        BufferImageCopy copy = new()
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(width, height, 1)
        };
        _api.CmdCopyBufferToImage(commandBuffer, stagingBuffer, image, ImageLayout.TransferDstOptimal, 1, in copy);
        ImageMemoryBarrier toShader = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };
        _api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit,
            PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, in toShader);
    }

    /// <summary>
    /// 创建 Vulkan 图像句柄
    /// </summary>
    private Image CreateImage(ImageCreateInfo createInfo)
    {
        Image image = default;
        Check(_api.CreateImage(_device, &createInfo, null, &image), "创建 Vulkan 图像");
        return image;
    }

    /// <summary>
    /// 创建字体图像视图
    /// </summary>
    private ImageView CreateImageView(Image image, Format format)
    {
        ImageViewCreateInfo createInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
        };
        ImageView view = default;
        Check(_api.CreateImageView(_device, &createInfo, null, &view), "创建 Vulkan 纹理图像视图");
        return view;
    }

    /// <summary>
    /// 创建最近邻字体采样器
    /// </summary>
    private Sampler CreateFontSampler()
    {
        SamplerCreateInfo createInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            // 矢量字体按线性采样，最近邻会让中文笔画出现锯齿和模糊
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 1F
        };
        Sampler sampler = default;
        Check(_api.CreateSampler(_device, &createInfo, null, &sampler), "创建 ImGui 字体采样器");
        return sampler;
    }

    /// <summary>
    /// 创建字体纹理描述符布局
    /// </summary>
    private DescriptorSetLayout CreateDescriptorSetLayout(DescriptorType descriptorType)
    {
        DescriptorSetLayoutBinding binding = new()
        {
            Binding = 0,
            DescriptorType = descriptorType,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit
        };
        DescriptorSetLayoutCreateInfo createInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding
        };
        DescriptorSetLayout layout = default;
        Check(_api.CreateDescriptorSetLayout(_device, &createInfo, null, &layout), "创建 ImGui 描述符布局");
        return layout;
    }

    /// <summary>
    /// 创建字体描述符池
    /// </summary>
    private DescriptorPool CreateDescriptorPool()
    {
        // 每个纹理占一个描述符集：字体、Logo、资产预览、三条管线的输出纹理，
        // 再加资源浏览器缩略图。容量要按缩略图数量留够余量，池耗尽会让后续注册失败
        DescriptorPoolSize* poolSizes = stackalloc DescriptorPoolSize[2]
        {
            new DescriptorPoolSize(DescriptorType.SampledImage, 512),
            new DescriptorPoolSize(DescriptorType.Sampler, 1)
        };
        DescriptorPoolCreateInfo createInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            MaxSets = 513,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes
        };
        DescriptorPool pool = default;
        Check(_api.CreateDescriptorPool(_device, &createInfo, null, &pool), "创建 ImGui 描述符池");
        return pool;
    }

    /// <summary>
    /// 分配字体描述符集
    /// </summary>
    private DescriptorSet AllocateDescriptorSet(DescriptorSetLayout layout)
    {
        DescriptorSetAllocateInfo allocateInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };
        DescriptorSet descriptorSet = default;
        Check(_api.AllocateDescriptorSets(_device, &allocateInfo, &descriptorSet), "分配 ImGui 描述符集");
        return descriptorSet;
    }

    /// <summary>
    /// 写入字体图像描述符
    /// </summary>
    private void UpdateFontDescriptor()
    {
        UpdateTextureDescriptor(_textureDescriptorSet, _fontImageView);
        DescriptorImageInfo samplerInfo = new(_fontSampler, default, ImageLayout.Undefined);
        WriteDescriptorSet samplerWrite = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _samplerDescriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.Sampler,
            PImageInfo = &samplerInfo
        };
        _api.UpdateDescriptorSets(_device, 1, in samplerWrite, 0, null);
    }

    /// <summary>
    /// 写入单张纹理的图像描述符
    /// </summary>
    private void UpdateTextureDescriptor(DescriptorSet textureSet, ImageView imageView)
    {
        DescriptorImageInfo imageInfo = new(default, imageView, ImageLayout.ShaderReadOnlyOptimal);
        WriteDescriptorSet imageWrite = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = textureSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.SampledImage,
            PImageInfo = &imageInfo
        };
        _api.UpdateDescriptorSets(_device, 1, in imageWrite, 0, null);
    }

    /// <summary>
    /// ImGui 纹理对应的 Vulkan 资源
    /// </summary>
    private readonly record struct TextureResource(Image Image, DeviceMemory Memory, ImageView View,
        DescriptorSet TextureSet, bool OwnsImage);

    /// <summary>
    /// 创建图形管线布局
    /// </summary>
    private PipelineLayout CreatePipelineLayout()
    {
        PushConstantRange pushRange = new(ShaderStageFlags.VertexBit, 0, PushConstantSize);
        DescriptorSetLayout* descriptorLayouts = stackalloc DescriptorSetLayout[2]
        {
            _textureDescriptorSetLayout,
            _samplerDescriptorSetLayout
        };
        PipelineLayoutCreateInfo createInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = descriptorLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange
        };
        PipelineLayout pipelineLayout = default;
        Check(_api.CreatePipelineLayout(_device, &createInfo, null, &pipelineLayout), "创建 ImGui 管线布局");
        return pipelineLayout;
    }

    /// <summary>
    /// 创建 ImGui 顶点输入、混合和光栅化管线
    /// </summary>
    private Pipeline CreatePipeline()
    {
        ShaderModule vertexModule = CreateShaderModule(OfficialImGuiSpirv.Vertex);
        ShaderModule fragmentModule = CreateShaderModule(OfficialImGuiSpirv.Fragment);
        nint vertexEntryPoint = IntPtr.Zero;
        nint fragmentEntryPoint = IntPtr.Zero;
        try
        {
            PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
            vertexEntryPoint = SilkMarshal.StringToPtr("main");
            fragmentEntryPoint = SilkMarshal.StringToPtr("main");
            stages[0] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertexModule, PName = (byte*)vertexEntryPoint };
            stages[1] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragmentModule, PName = (byte*)fragmentEntryPoint };
            VertexInputBindingDescription binding = new(0, (uint)Unsafe.SizeOf<ImDrawVert>(), VertexInputRate.Vertex);
            VertexInputAttributeDescription* attributes = stackalloc VertexInputAttributeDescription[3];
            attributes[0] = new(0, 0, Format.R32G32Sfloat, 0);
            attributes[1] = new(1, 0, Format.R32G32Sfloat, 8);
            attributes[2] = new(2, 0, Format.R8G8B8A8Unorm, 16);
            PipelineVertexInputStateCreateInfo vertexInput = new()
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 3,
                PVertexAttributeDescriptions = attributes
            };
            PipelineInputAssemblyStateCreateInfo inputAssembly = new()
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = false
            };
            PipelineViewportStateCreateInfo viewportState = new()
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                PViewports = null,
                ScissorCount = 1,
                PScissors = null
            };
            DynamicState* dynamicStates = stackalloc DynamicState[2]
            {
                DynamicState.Viewport,
                DynamicState.Scissor
            };
            PipelineDynamicStateCreateInfo dynamicState = new()
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates
            };
            PipelineRasterizationStateCreateInfo rasterization = new()
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1F,
                DepthClampEnable = false,
                RasterizerDiscardEnable = false,
                DepthBiasEnable = false
            };
            PipelineMultisampleStateCreateInfo multisample = new()
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                SampleShadingEnable = false,
                RasterizationSamples = SampleCountFlags.Count1Bit
            };
            PipelineColorBlendAttachmentState blendAttachment = new()
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
            };
            PipelineColorBlendStateCreateInfo blend = new()
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment
            };
            PipelineDepthStencilStateCreateInfo depth = new()
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo
            };
            GraphicsPipelineCreateInfo createInfo = new()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PDepthStencilState = &depth,
                PColorBlendState = &blend,
                PDynamicState = &dynamicState,
                Layout = _pipelineLayout,
                RenderPass = _context.RenderPass,
                Subpass = 0,
                BasePipelineHandle = default
            };
            Pipeline pipeline = default;
            Check(_api.CreateGraphicsPipelines(_device, _context.PipelineCacheHandle, 1, in createInfo, null, out pipeline), "创建 ImGui Vulkan 图形管线");
            return pipeline;
        }
        finally
        {
            SilkMarshal.Free(vertexEntryPoint);
            SilkMarshal.Free(fragmentEntryPoint);
            _api.DestroyShaderModule(_device, vertexModule, null);
            _api.DestroyShaderModule(_device, fragmentModule, null);
        }
    }

    /// <summary>
    /// 创建 Vulkan ShaderModule
    /// </summary>
    private ShaderModule CreateShaderModule(uint[] words)
    {
        fixed (uint* code = words)
        {
            ShaderModuleCreateInfo createInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(words.Length * sizeof(uint)),
                PCode = code
            };
            ShaderModule module = default;
            Check(_api.CreateShaderModule(_device, &createInfo, null, &module), "创建 Vulkan ShaderModule");
            return module;
        }
    }

    /// <summary>
    /// 创建主机可见 Vulkan 缓冲区
    /// </summary>
    private (VkBuffer Buffer, DeviceMemory Memory) CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties)
    {
        BufferCreateInfo createInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };
        VkBuffer buffer = default;
        Check(_api.CreateBuffer(_device, &createInfo, null, &buffer), "创建 ImGui Vulkan 缓冲区");
        MemoryRequirements requirements = default;
        _api.GetBufferMemoryRequirements(_device, buffer, &requirements);
        DeviceMemory memory = AllocateMemory(requirements, properties);
        Check(_api.BindBufferMemory(_device, buffer, memory, 0), "绑定 ImGui Vulkan 缓冲区内存");
        return (buffer, memory);
    }

    /// <summary>
    /// 按内存属性分配 Vulkan 设备内存
    /// </summary>
    private DeviceMemory AllocateMemory(MemoryRequirements requirements, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties memoryProperties = default;
        _api.GetPhysicalDeviceMemoryProperties(_physicalDevice, &memoryProperties);
        uint index = 0;
        for (; index < memoryProperties.MemoryTypeCount; index++)
        {
            var type = memoryProperties.MemoryTypes[(int)index];
            // 跳过受保护内存：未启用受保护设备特性时使用它属于无效用法
            if ((requirements.MemoryTypeBits & (1u << (int)index)) != 0 &&
                (type.PropertyFlags & MemoryPropertyFlags.ProtectedBit) == 0 &&
                (type.PropertyFlags & properties) == properties)
            {
                break;
            }
        }

        if (index >= memoryProperties.MemoryTypeCount)
        {
            throw new InvalidOperationException($"没有满足 Vulkan 内存属性的内存类型：{properties}");
        }

        MemoryAllocateInfo allocateInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = index
        };
        DeviceMemory memory = default;
        Check(_api.AllocateMemory(_device, &allocateInfo, null, &memory), "分配 ImGui Vulkan 内存");
        return memory;
    }

    /// <summary>
    /// 检查 Vulkan 调用结果
    /// </summary>
    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation}失败：{result}");
        }
    }
}
