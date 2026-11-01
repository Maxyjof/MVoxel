using System.Buffers.Binary;
using MVoxel.Rendering;
using MVoxel.Voxels;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MVoxel.Editor.Rendering.Vulkan;

internal sealed unsafe partial class VulkanVoxelRenderPipeline
{
    private const string EmissiveSamplingShader = """
        struct EmissiveFace { ivec4 key; vec4 colorSlot; };
        layout(set = 0, binding = 10, std430) readonly buffer EmissiveFaces
        {
            ivec4 emissiveHeader;
            EmissiveFace emissiveFaces[];
        };

        vec3 EmissiveNormal(int direction)
        {
            vec3 n = vec3(0.0);
            n[direction / 2] = (direction & 1) == 0 ? -1.0 : 1.0;
            return n;
        }

        float SurfaceSamplingPdf(vec3 baseColor, float metalness, float roughness,
            vec3 n, vec3 v, vec3 l)
        {
            float nl = dot(n, l);
            if (nl <= 0.0 || dot(n, v) <= 0.0) return 0.0;
            vec3 h = normalize(v + l);
            float nh = max(dot(n, h), 0.0);
            float vh = max(dot(v, h), 0.0);
            float a = max(roughness * roughness, 0.002);
            float denominator = nh * nh * (a * a - 1.0) + 1.0;
            float d = a * a / max(3.14159265 * denominator * denominator, 1e-12);
            vec3 f0 = mix(vec3(clamp(values[75], 0.0, 1.0)), baseColor, metalness);
            float probability = clamp(max(max(f0.r, f0.g), f0.b), 0.05, 0.95);
            return probability * d * nh / max(4.0 * vh, 1e-12)
                + (1.0 - probability) * nl / 3.14159265;
        }

        int CompareFaceKey(ivec4 a, ivec4 b)
        {
            for (int i = 0; i < 4; i++)
                if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            return 0;
        }

        float EmissiveHitPdf(vec3 origin, vec3 point, vec3 normal)
        {
            int count = emissiveHeader.x;
            if (count <= 0 || values[80] <= 0.0 || values[117] < 0.5) return 0.0;
            int axis = abs(normal.x) > 0.5 ? 0 : (abs(normal.y) > 0.5 ? 1 : 2);
            int direction = axis * 2 + (normal[axis] > 0.0 ? 1 : 0);
            vec3 region = vec3(values[16], values[17], values[18]);
            ivec4 key = ivec4(ivec3(floor(point - region - normal * 0.001)), direction);
            int lo = 0, hi = count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (CompareFaceKey(emissiveFaces[mid].key, key) < 0) lo = mid + 1;
                else hi = mid;
            }
            if (lo >= count || CompareFaceKey(emissiveFaces[lo].key, key) != 0) return 0.0;
            vec3 offset = point - origin;
            float distanceSquared = dot(offset, offset);
            if (distanceSquared <= 1e-10) return 0.0;
            float cosine = dot(normal, -normalize(offset));
            return cosine > 0.0 ? distanceSquared / (float(count) * cosine) : 0.0;
        }

        vec3 EmissiveNextEvent(vec3 origin, vec3 normal, vec3 viewDirection,
            vec3 baseColor, float metalness, float roughness, bool continuation, inout uint state)
        {
            int count = emissiveHeader.x;
            if (count <= 0 || values[80] <= 0.0 || values[117] < 0.5) return vec3(0.0);
            int index = min(int(Random(state) * float(count)), count - 1);
            EmissiveFace face = emissiveFaces[index];
            int axis = face.key.w / 2;
            vec3 lightNormal = EmissiveNormal(face.key.w);
            vec3 point = vec3(values[16], values[17], values[18]) + vec3(face.key.xyz);
            point[axis] += (face.key.w & 1) == 0 ? 0.0 : 1.0;
            point[(axis + 1) % 3] += Random(state);
            point[(axis + 2) % 3] += Random(state);
            vec3 offset = point - origin;
            float distanceSquared = dot(offset, offset);
            if (distanceSquared <= 1e-10) return vec3(0.0);
            float distance = sqrt(distanceSquared);
            vec3 direction = offset / distance;
            float lightCosine = dot(lightNormal, -direction);
            if (lightCosine <= 0.0 || dot(normal, direction) <= 0.0) return vec3(0.0);
            float lightPdf = distanceSquared / (float(count) * lightCosine);
            float bsdfPdf = continuation
                ? SurfaceSamplingPdf(baseColor, metalness, roughness, normal, viewDirection, direction) : 0.0;
            float hitDistance; vec3 hitNormal; vec4 hitColor;
            uint hitFlags; uint hitSlot; ivec3 hitCell; ivec3 hitExtent;
            // 不忽略透明介质：直连估计不承担折射与吸收，避免与介质路径重复或漏光。
            if (TraceClosestWithin(origin, direction, hitDistance, hitNormal, hitColor, hitFlags,
                hitSlot, hitCell, hitExtent, false, max(distance - 0.0001, 0.0), true)) return vec3(0.0);
            float scale = materialSlots[int(face.colorSlot.w) % 64].scalars.z;
            scale = scale >= 0.0 ? scale : 1.0;
            vec3 emission = min(SrgbToLinear(face.colorSlot.rgb) * values[80] * scale,
                vec3(max(values[77] * 0.1, 1.0)));
            return emission * SunSurfaceResponse(baseColor, metalness, roughness,
                normal, viewDirection, direction) / (3.14159265 * (lightPdf + bsdfPdf));
        }
        """;

    private EmissiveFaceCatalog _emissiveCatalog = new();
    private VoxelWorld? _emissiveSource;
    private int _emissiveVersion = -1;
    private VkBuffer _emissiveBuffer;
    private DeviceMemory _emissiveMemory;
    private ulong _emissiveCapacity;
    internal int EmissiveFaceCount => _emissiveCatalog.FaceCount;
    internal double EmissiveSyncMilliseconds { get; private set; }

    private void RefreshEmissiveCatalog(VoxelWorld world, bool exactGeometry)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (!exactGeometry)
        {
            _emissiveCatalog = new();
            _emissiveSource = null;
            _emissiveVersion = -1;
        }
        else if (!ReferenceEquals(_emissiveSource, world))
        {
            _emissiveCatalog.Rebuild(world);
            _emissiveSource = world;
            _emissiveVersion = world.ContentVersion;
        }
        else if (_emissiveVersion != world.ContentVersion)
        {
            if (world.TryTakeDirty(out var dirty)) _emissiveCatalog.Update(world, dirty);
            else _emissiveCatalog.Rebuild(world);
            _emissiveVersion = world.ContentVersion;
        }
        EmissiveSyncMilliseconds = watch.Elapsed.TotalMilliseconds;
    }

    private void EnsureEmissiveBuffer(ulong bytes)
    {
        if (_emissiveBuffer.Handle != 0 && _emissiveCapacity >= bytes) return;
        _api!.DeviceWaitIdle(_device);
        DestroyEmissiveBuffer();
        _emissiveCapacity = NextCapacity(bytes);
        (_emissiveBuffer, _emissiveMemory) = CreateBuffer(_emissiveCapacity,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit);
        // 所有描述符始终绑定有效缓冲；未同步世界时头部数量为零。
        UploadStaging(new byte[16], _emissiveBuffer);
    }

    private void UploadEmissiveFaces()
    {
        if (_context is null || _api is null) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var faces = _emissiveCatalog.Faces.OrderBy(f => f.Voxel.X).ThenBy(f => f.Voxel.Y)
            .ThenBy(f => f.Voxel.Z).ThenBy(f => f.Direction).ToArray();
        var bytes = new byte[checked(16 + faces.Length * 32)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, faces.Length);
        for (var i = 0; i < faces.Length; i++)
        {
            var face = faces[i];
            var offset = 16 + i * 32;
            WriteInt(offset, checked((int)(face.Voxel.X - _workset.Origin.X)));
            WriteInt(offset + 4, checked((int)(face.Voxel.Y - _workset.Origin.Y)));
            WriteInt(offset + 8, checked((int)(face.Voxel.Z - _workset.Origin.Z)));
            WriteInt(offset + 12, face.Direction);
            WriteFloat(offset + 16, face.Value.Color.R / 255F);
            WriteFloat(offset + 20, face.Value.Color.G / 255F);
            WriteFloat(offset + 24, face.Value.Color.B / 255F);
            WriteFloat(offset + 28, face.Value.MaterialSlot);
        }
        EnsureEmissiveBuffer((ulong)bytes.Length);
        UploadStaging(bytes, _emissiveBuffer);
        UpdateComputeDescriptors();
        EmissiveSyncMilliseconds += watch.Elapsed.TotalMilliseconds;
        void WriteInt(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        void WriteFloat(int offset, float value) => WriteInt(offset, BitConverter.SingleToInt32Bits(value));
    }

    private void DestroyEmissiveBuffer()
    {
        if (_emissiveBuffer.Handle == 0 || _api is null) return;
        _api.DestroyBuffer(_device, _emissiveBuffer, null);
        _api.FreeMemory(_device, _emissiveMemory, null);
        _emissiveBuffer = default;
        _emissiveMemory = default;
        _emissiveCapacity = 0;
    }
}
