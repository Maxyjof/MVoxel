using System.Numerics;
using MVoxel.Rendering;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MVoxel.Editor.Rendering.Vulkan;

internal sealed unsafe partial class VulkanVoxelRenderPipeline
{
    private VkBuffer _sunDepthBuffer;
    private DeviceMemory _sunDepthMemory;
    private ulong _sunDepthCapacity;
    private int _sunDepthVersion = -1;
    private float[]? _sunDepthKey;
    private bool _sunReconstructionLimitReported;
    private long? _sunReconstructionKey;
    private ulong _sunBufferFailedBytes;
    private long _sunBufferFailedAtMs;
    private const long SunBufferRetryDelayMs = 5000;
    internal string? SunShadowFallbackReason { get; private set; }

    private void EnsureSunDepthBuffer(ulong bytes)
    {
        if (_sunDepthCapacity >= bytes) return;
        WaitForComputeIdle();
        // 新资源成功创建后才释放旧资源，分配失败不会破坏已有描述符。
        var replacement = CreateBuffer(bytes, BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.DeviceLocalBit);
        DestroySunDepthBuffer();
        (_sunDepthBuffer, _sunDepthMemory) = replacement;
        _sunDepthCapacity = bytes;
    }

    private bool TryEnsureSunDepthBuffer(ulong bytes)
    {
        if (_sunDepthCapacity >= bytes)
        {
            if (_sunBufferFailedBytes != bytes) _sunBufferFailedBytes = 0;
            return true;
        }
        // 显存压力可能来自其他应用；限制重试频率，但不永久锁定回退状态。
        if (_sunBufferFailedBytes == bytes &&
            Environment.TickCount64 - _sunBufferFailedAtMs < SunBufferRetryDelayMs) return false;
        try
        {
            EnsureSunDepthBuffer(bytes);
            _sunBufferFailedBytes = 0;
            return true;
        }
        catch (InvalidOperationException exception) when (_context?.IsDeviceLost != true &&
            (exception.Message.Contains("ErrorOutOfDeviceMemory", StringComparison.OrdinalIgnoreCase) ||
             exception.Message.Contains("ErrorOutOfHostMemory", StringComparison.OrdinalIgnoreCase) ||
             exception.Message.Contains("没有满足体素工作集内存属性", StringComparison.Ordinal)))
        {
            _sunBufferFailedBytes = bytes;
            _sunBufferFailedAtMs = Environment.TickCount64;
            MVoxel.Core.MVoxelLog.Warn($"太阳阴影缓冲申请失败，改用直接太阳采样：{exception.Message}");
            return false;
        }
    }

    private void DestroySunDepthBuffer()
    {
        if (_sunDepthBuffer.Handle == 0 || _api is null) return;
        _api.DestroyBuffer(_device, _sunDepthBuffer, null);
        _api.FreeMemory(_device, _sunDepthMemory, null);
        _sunDepthBuffer = default;
        _sunDepthMemory = default;
        _sunDepthCapacity = 0;
        _sunDepthKey = null;
        _sunReconstructionKey = null;
    }

    private void PrepareSunDepthMap(RenderSettings settings, float[] parameters)
    {
        SunShadowFallbackReason = null;
        // 角尺寸为零时三种模式共用硬阴影查询，不需要分配或重建预通道。
        if (settings.ShadowSoftness <= 0.0001F) return;
        if (settings.SunEnabled && settings.SunShadowMethod == SunShadowMethod.Reconstructed && parameters[115] < 0.5F)
        {
            PrepareSunReconstruction(parameters);
            return;
        }
        if (!settings.SunEnabled || settings.SunShadowMethod != SunShadowMethod.ShadowMap ||
            parameters[VoxelRenderParameters.ScenePreview] > 0.5F) return;
        var size = settings.SunShadowMapResolution;
        var extent = new Vector3(parameters[20], parameters[21], parameters[22]);
        var center = new Vector3(parameters[16], parameters[17], parameters[18]) + extent * 0.5F;
        var radius = Math.Max(2F, extent.Length() * 0.5F + 2F);
        parameters[VoxelRenderParameters.SunMapCenterX] = center.X;
        parameters[VoxelRenderParameters.SunMapCenterY] = center.Y;
        parameters[VoxelRenderParameters.SunMapCenterZ] = center.Z;
        parameters[VoxelRenderParameters.SunMapRadius] = radius;
        float[] key = [parameters[24], parameters[25], parameters[26], center.X, center.Y, center.Z,
            radius, size, parameters[VoxelRenderParameters.BrickMode], parameters[VoxelRenderParameters.VoxelCount]];
        if (_sunDepthVersion == SyncCompletedCount && _sunDepthKey is not null &&
            key.AsSpan().SequenceEqual(_sunDepthKey)) return;
        var oldCapacity = _sunDepthCapacity;
        if (!TryEnsureSunDepthBuffer(checked((ulong)size * (ulong)size * sizeof(float))))
        {
            parameters[VoxelRenderParameters.SunMapRadius] = 0F;
            SunShadowFallbackReason = "阴影贴图缓冲分配失败，当前使用直接太阳采样";
            return;
        }
        if (oldCapacity != _sunDepthCapacity) UpdateComputeDescriptors();
        _sunReconstructionKey = null; // C 将覆盖 B 的屏幕空间数据。
        var pass = (float[])parameters.Clone();
        pass[VoxelRenderParameters.SunShadowPass] = 1F;
        // 独立参数副本，分条提交，避免污染当前采样轮或显示通道。
        for (var row = 0; row < size; row += 32)
        {
            var slot = AcquireRingSlot();
            UploadParameters(slot, pass);
            var command = _ringCommands[slot];
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit
            };
            _api!.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);
            BindComputeTile(command, _ringSets[slot], 0, row);
            _api.CmdDispatch(command, ((uint)size + 7) / 8, 4, 1);
            _api.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);
            CommitRingSlot(slot);
        }
        _sunDepthVersion = SyncCompletedCount;
        _sunDepthKey = key;
    }

    private void PrepareSunReconstruction(float[] parameters)
    {
        var width = (int)parameters[11];
        var height = (int)parameters[15];
        // 采样帧变化不影响 B 的几何导引，避免静止和路径收敛时每次重算。
        var keyParameters = (float[])parameters.Clone();
        keyParameters[VoxelRenderParameters.Accumulated] = 0F;
        var key = ComputeRealtimeHistorySignature(keyParameters, width, height, SyncCompletedCount);
        if (_sunReconstructionKey == key)
        {
            parameters[VoxelRenderParameters.SunReconstructionAvailable] = 1F;
            return;
        }
        var required = checked((ulong)width * (ulong)height * 12 * sizeof(float));
        PhysicalDeviceProperties properties = default;
        _api!.GetPhysicalDeviceProperties(_context!.PhysicalDevice, &properties);
        if (required > properties.Limits.MaxStorageBufferRange)
        {
            SunShadowFallbackReason = "当前视口的半影重建缓冲超过设备上限，已回退直接太阳采样";
            if (!_sunReconstructionLimitReported)
            {
                MVoxel.Core.MVoxelLog.Warn($"太阳半影重建需 {required} 字节，超过设备存储缓冲上限 {properties.Limits.MaxStorageBufferRange}；保留全分辨率并使用太阳直接采样");
                _sunReconstructionLimitReported = true;
            }
            return;
        }
        _sunReconstructionLimitReported = false;
        var oldCapacity = _sunDepthCapacity;
        if (!TryEnsureSunDepthBuffer(required))
        {
            SunShadowFallbackReason = "半影重建缓冲分配失败，当前使用直接太阳采样";
            return;
        }
        if (oldCapacity != _sunDepthCapacity) UpdateComputeDescriptors();
        _sunDepthKey = null; // B 与 C 互斥复用缓冲。
        parameters[VoxelRenderParameters.SunReconstructionAvailable] = 1F;
        var pass = (float[])parameters.Clone();
        for (var stage = 2; stage <= 3; stage++)
        {
            pass[VoxelRenderParameters.SunShadowPass] = stage;
            for (var row = 0; row < height; row += 32)
            {
                var slot = AcquireRingSlot();
                UploadParameters(slot, pass);
                var command = _ringCommands[slot];
                var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit };
                _api!.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit,
                    PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);
                BindComputeTile(command, _ringSets[slot], 0, row);
                _api.CmdDispatch(command, ((uint)width + 7) / 8, 4, 1);
                _api.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit,
                    PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);
                CommitRingSlot(slot);
            }
        }
        _sunReconstructionKey = key;
    }

    private const string SunDepthShader = """
        layout(set = 0, binding = 14, std430) buffer SunDepthBuffer { float sunDepth[]; };

        /* SUN_RECONSTRUCTION */
        bool TraceSunMapClosest(vec3 origin, vec3 direction, float maxDistance, out float closest)
        {
            #ifdef P_HW_RT
            // 候选三角形只负责找到可能的盒，实际距离用软件相同的盒求交。
            // 起点位于体素内部时通过出口三角形找到该盒，再计算其出口距离。
            rayQueryEXT query;
            rayQueryInitializeEXT(query, shadowTopLevel, gl_RayFlagsNoOpaqueEXT, 0xFF,
                origin - vec3(values[16],values[17],values[18]), 0.0, direction, maxDistance);
            closest = maxDistance;
            bool hit = false;
            while (rayQueryProceedEXT(query))
            {
                if (rayQueryGetIntersectionTypeEXT(query, false) != gl_RayQueryCandidateIntersectionTriangleEXT) continue;
                int index = rayQueryGetIntersectionPrimitiveIndexEXT(query, false) / 12;
                if (index < 0 || index >= int(values[94])) continue;
                VoxelCell candidate = cells[index];
                if (candidate.color.a <= 0.0 || ((candidate.metadata.x >> 16) & 1u) != 0u) continue;
                ivec3 extent = ivec3(max(candidate.position.w & 255,1),
                    max((candidate.position.w >> 8) & 255,1), max((candidate.position.w >> 16) & 255,1));
                vec3 minimum = vec3(values[16],values[17],values[18]) + vec3(candidate.position.xyz);
                float d; vec3 n;
                if (IntersectBox(origin,direction,minimum,minimum + vec3(extent),d,n,false) && d < closest)
                { closest=d; hit=true; }
            }
            return hit;
            #else
            vec3 n; vec4 color; uint flags,slot; ivec3 cell,extent;
            return TraceClosestWithin(origin,direction,closest,n,color,flags,slot,cell,extent,true,maxDistance,false);
            #endif
        }
        vec3 SunMapCenter() { return vec3(values[P_SUN_MAP_X], values[P_SUN_MAP_Y], values[P_SUN_MAP_Z]); }
        void BuildSunDepth(ivec2 pixel)
        {
            int size = int(values[P_SUN_MAP_SIZE]);
            if (any(greaterThanEqual(pixel, ivec2(size)))) return;
            vec3 light = normalize(vec3(values[24], values[25], values[26]));
            vec3 tangent, bitangent;
            SunDiskBasis(light, tangent, bitangent);
            float radius = values[P_SUN_MAP_RADIUS];
            vec2 plane = ((vec2(pixel) + 0.5) / float(size) * 2.0 - 1.0) * radius;
            vec3 origin = SunMapCenter() + light * radius + tangent * plane.x + bitangent * plane.y;
            float distance;
            vec3 normal;
            vec4 color;
            uint flags, slot;
            ivec3 cell, extent;
            bool hit = TraceSunMapClosest(origin, -light, radius * 2.0, distance);
            sunDepth[pixel.y * size + pixel.x] = hit ? distance : 1.0e30;
        }
        float SunDepthAt(ivec2 pixel)
        {
            int size = int(values[P_SUN_MAP_SIZE]);
            if (any(lessThan(pixel, ivec2(0))) || any(greaterThanEqual(pixel, ivec2(size)))) return 1.0e30;
            return sunDepth[pixel.y * size + pixel.x];
        }
        float SunCompare(vec2 pixel, float receiver, vec2 receiverPixel, vec2 depthSlope)
        {
            ivec2 p = ivec2(floor(pixel));
            vec2 f = fract(pixel);
            float a = receiver + dot(vec2(p) - receiverPixel, depthSlope);
            return mix(mix(step(a, SunDepthAt(p)), step(a + depthSlope.x, SunDepthAt(p + ivec2(1,0))), f.x),
                mix(step(a + depthSlope.y, SunDepthAt(p + ivec2(0,1))), step(a + depthSlope.x + depthSlope.y, SunDepthAt(p + ivec2(1))), f.x), f.y);
        }
        float SunPcss(vec3 origin, vec3 normal, vec3 light, float maxDistance)
        {
            float radius = values[P_SUN_MAP_RADIUS];
            if (radius <= 0.0 || maxDistance < radius * 2.0) return -1.0;
            vec3 tangent, bitangent;
            SunDiskBasis(light, tangent, bitangent);
            vec3 delta = origin - SunMapCenter();
            float receiver = radius - dot(delta, light);
            float size = values[P_SUN_MAP_SIZE];
            float texel = 2.0 * radius / size;
            vec2 pixel = (vec2(dot(delta, tangent), dot(delta, bitangent)) / (2.0 * radius) + 0.5) * size - 0.5;
            float normalLight = dot(normal, light);
            // 接近平行时深度梯度不稳定，改用直接射线避免巨大的偏移和漏光。
            if (normalLight <= 0.05) return -1.0;
            vec2 depthSlope = vec2(dot(normal,tangent), dot(normal,bitangent)) * (texel / normalLight);
            // 接收点可在包围盒外（例如无限地面）；光源锥越过贴图边缘时直接求交回退。
            float search = max(1.0, max(receiver, 0.0) * values[31] / texel);
            // 接收点可以位于包围球以外；其到最前方深度图的距离可能超过
            // 2*radius。贴图仅保存最近层，无法丢弃超出 maxDistance 的遮挡。
            if (receiver <= 0.0 || receiver > maxDistance || any(lessThan(pixel - search, vec2(0))) ||
                any(greaterThan(pixel + search, vec2(size - 1.0))))
                return -1.0;
            float bias = max(0.002, texel * 0.35);
            float z = receiver - bias;
            int count = clamp(int(values[32]) * 2, 8, 32);
            // 搜索盘的离散点不一定命中中心 texel；显式计入中心，避免细遮挡漏检。
            ivec2 centerPixel = ivec2(floor(pixel + 0.5));
            float centerDepth = SunDepthAt(centerPixel) - dot(vec2(centerPixel) - pixel, depthSlope);
            bool centerBlocked = centerDepth < z && receiver - centerDepth <= maxDistance;
            float blockers = centerBlocked ? 1.0 : 0.0;
            float sumDepth = centerBlocked ? centerDepth : 0.0;
            for (int i = 0; i < 32; ++i)
            {
                if (i >= count) break;
                float angle = float(i) * 2.39996323;
                vec2 offset = vec2(cos(angle), sin(angle)) * sqrt((float(i) + 0.5) / float(count));
                ivec2 samplePixel = ivec2(floor(pixel + offset * search + 0.5));
                float d = SunDepthAt(samplePixel) - dot(vec2(samplePixel) - pixel, depthSlope);
                if (d < z && receiver - d <= maxDistance) { sumDepth += d; blockers += 1.0; }
            }
            if (blockers == 0.0) return SunCompare(pixel, z, pixel, depthSlope);
            // 正交方向光：半影半径=遮挡物与接收面距离*tan(太阳角半径)。
            float filterRadius = max(0.5, (receiver - sumDepth / blockers) * values[31] / texel);
            float visibility = 0.0;
            for (int i = 0; i < 32; ++i)
            {
                if (i >= count) break;
                float angle = float(i) * 2.39996323;
                vec2 offset = vec2(cos(angle), sin(angle)) * sqrt((float(i) + 0.5) / float(count));
                visibility += SunCompare(pixel + offset * filterRadius, z, pixel, depthSlope);
            }
            return visibility / float(count);
        }
        """;
}
