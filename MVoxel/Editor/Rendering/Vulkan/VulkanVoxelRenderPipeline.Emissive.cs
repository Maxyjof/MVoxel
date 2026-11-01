using System.Buffers.Binary;
using System.Numerics;
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

        // 树按面数平衡切分，实际下降层数约 ceil(log2(面数/8))；
        // 遍历上限取节点数：树有 N 个节点，任何路径都不超过 N 步，数据异常成环也会退出。
        // 着色器里的死循环会被驱动判成 GPU 超时并直接丢失设备。
        bool UseLightTree()
        {
            return values[118] > 0.5 && emissiveHeader.y > 0 && emissiveHeader.x > 32;
        }

        EmissiveFace LightNode(int index) { return emissiveFaces[emissiveHeader.x + index * 2]; }
        vec3 LightNodeMaximum(int index) { return emissiveFaces[emissiveHeader.x + index * 2 + 1].colorSlot.xyz; }

        float LightNodeScore(int index, vec3 receiver)
        {
            EmissiveFace node = LightNode(index);
            vec3 size = LightNodeMaximum(index) - node.colorSlot.xyz;
            vec3 offset = node.colorSlot.xyz + size * 0.5 - receiver;
            return node.colorSlot.w / max(dot(offset, offset) + dot(size, size) * 0.25, 1.0);
        }

        float LightLeftProbability(EmissiveFace node, vec3 receiver)
        {
            EmissiveFace left = LightNode(node.key.x);
            float uniformProbability = float(left.key.w - left.key.z) / float(node.key.w - node.key.z);
            float a = LightNodeScore(node.key.x, receiver);
            float b = LightNodeScore(node.key.y, receiver);
            float largest = max(a, b);
            if (largest <= 0.0 || isnan(largest) || isinf(largest)) return uniformProbability;
            a /= largest; b /= largest;
            // 每个节点保留按面数分配的概率，不裁掉暗灯或极远光源。
            return mix(uniformProbability, a / (a + b), 0.95);
        }

        float LightFaceProbability(int faceIndex, vec3 origin)
        {
            if (!UseLightTree()) return 1.0 / float(emissiveHeader.x);
            vec3 receiver = origin - vec3(values[16], values[17], values[18]);
            int index = 0;
            float probability = 1.0;
            for (int step = 0; step < emissiveHeader.y; step++)
            {
                if (index < 0 || index >= emissiveHeader.y) break;
                EmissiveFace node = LightNode(index);
                if (node.key.x < 0) return probability / float(node.key.w - node.key.z);
                float leftProbability = LightLeftProbability(node, receiver);
                if (faceIndex < LightNode(node.key.x).key.w)
                { probability *= leftProbability; index = node.key.x; }
                else { probability *= 1.0 - leftProbability; index = node.key.y; }
            }

            return 1.0 / float(max(emissiveHeader.x, 1));
        }

        int DrawLightFace(vec3 origin, inout uint state, out float probability)
        {
            int count = emissiveHeader.x;
            if (!UseLightTree())
            {
                probability = 1.0 / float(count);
                return min(int(Random(state) * float(count)), count - 1);
            }
            vec3 receiver = origin - vec3(values[16], values[17], values[18]);
            int index = 0;
            probability = 1.0;
            for (int step = 0; step < emissiveHeader.y; step++)
            {
                if (index < 0 || index >= emissiveHeader.y) break;
                EmissiveFace node = LightNode(index);
                if (node.key.x < 0)
                {
                    int length = node.key.w - node.key.z;
                    probability /= float(max(length, 1));
                    return node.key.z + min(int(Random(state) * float(max(length, 1))), max(length, 1) - 1);
                }
                float leftProbability = LightLeftProbability(node, receiver);
                if (Random(state) < leftProbability)
                { probability *= leftProbability; index = node.key.x; }
                else { probability *= 1.0 - leftProbability; index = node.key.y; }
            }

            probability = 1.0 / float(max(count, 1));
            return min(int(Random(state) * float(max(count, 1))), max(count, 1) - 1);
        }

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
            ivec3 brickA = a.xyz / 8;
            ivec3 brickB = b.xyz / 8;
            for (int axis = 0; axis < 3; axis++)
                if (brickA[axis] != brickB[axis]) return brickA[axis] < brickB[axis] ? -1 : 1;
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
            return cosine > 0.0 ? LightFaceProbability(lo, origin) * distanceSquared / cosine : 0.0;
        }

        vec3 EmissiveNextEvent(vec3 origin, vec3 normal, vec3 viewDirection,
            vec3 baseColor, float metalness, float roughness, bool continuation, inout uint state)
        {
            int count = emissiveHeader.x;
            if (count <= 0 || values[80] <= 0.0 || values[117] < 0.5) return vec3(0.0);
            float faceProbability;
            int index = DrawLightFace(origin, state, faceProbability);
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
            float lightPdf = faceProbability * distanceSquared / lightCosine;
            float bsdfPdf = continuation
                ? SurfaceSamplingPdf(baseColor, metalness, roughness, normal, viewDirection, direction) : 0.0;
            float hitDistance; vec3 hitNormal; vec4 hitColor;
            uint hitFlags; uint hitSlot; ivec3 hitCell; ivec3 hitExtent;
            // 不忽略透明介质：直连估计不承担折射与吸收，避免与介质路径重复或漏光。
            if (TraceClosestWithin(origin, direction, hitDistance, hitNormal, hitColor, hitFlags,
                hitSlot, hitCell, hitExtent, false, max(distance - 0.0001, 0.0), true)) return vec3(0.0);
            float scale = materialSlots[int(face.colorSlot.w) % 256].scalars.z;
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
    private EmissiveFaceCatalog.Face[] _packedEmissiveFaces = [];
    private EmissiveLightHierarchy.Node[] _lightNodes = [];
    private byte[] _emissiveUploadedBytes = [];
    internal int EmissiveFaceCount => _emissiveCatalog.FaceCount;
    internal int OutputWidth => (int)_outputWidth;
    internal int OutputHeight => (int)_outputHeight;
    internal int EmissiveTreeNodeCount { get; private set; }
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
        UploadBytes(new byte[16], _emissiveBuffer, 0);
    }

    private void UploadEmissiveFaces()
    {
        if (_context is null || _api is null || _context.IsDeviceLost) return;
        // 描述符会在本次同步后重新写入，先等上一轮分块计算结束
        WaitForComputeIdle();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // 相对锚点的 8³ 空间砖排序与 Shader 的 CompareFaceKey 保持一致。
        var faces = _emissiveCatalog.Faces.OrderBy(f => (f.Voxel.X - _workset.Origin.X) / 8)
            .ThenBy(f => (f.Voxel.Y - _workset.Origin.Y) / 8)
            .ThenBy(f => (f.Voxel.Z - _workset.Origin.Z) / 8)
            .ThenBy(f => f.Voxel.X).ThenBy(f => f.Voxel.Y)
            .ThenBy(f => f.Voxel.Z).ThenBy(f => f.Direction).ToArray();
        var positions = new Vector3[faces.Length];
        var powers = new float[faces.Length];
        for (var i = 0; i < faces.Length; i++)
        {
            var face = faces[i];
            positions[i] = new Vector3(checked((int)(face.Voxel.X - _workset.Origin.X)),
                checked((int)(face.Voxel.Y - _workset.Origin.Y)), checked((int)(face.Voxel.Z - _workset.Origin.Z)));
            var slot = face.Value.MaterialSlot % MVoxel.Project.MaterialAsset.TableSlots *
                MVoxel.Project.MaterialAsset.FloatsPerSlot + 2;
            var scale = slot < _materialTable.Length && _materialTable[slot] >= 0F ? _materialTable[slot] : 1F;
            powers[i] = (0.2126F * Linear(face.Value.Color.R) + 0.7152F * Linear(face.Value.Color.G)
                + 0.0722F * Linear(face.Value.Color.B)) * (float.IsFinite(scale) ? scale : 1F);
        }
        bool sameTopology = faces.Length == _packedEmissiveFaces.Length && _lightNodes.Length > 0;
        for (var i = 0; sameTopology && i < faces.Length; i++)
            sameTopology = faces[i].Voxel == _packedEmissiveFaces[i].Voxel && faces[i].Direction == _packedEmissiveFaces[i].Direction;
        var nodes = faces.Length <= 32 ? [] : sameTopology
            ? EmissiveLightHierarchy.Refit(_lightNodes, positions, powers)
            : EmissiveLightHierarchy.Build(positions, powers);
        EmissiveTreeNodeCount = nodes.Length;
        var bytes = new byte[checked(16 + faces.Length * 32 + nodes.Length * 64)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, faces.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), nodes.Length);
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
        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            var offset = 16 + faces.Length * 32 + i * 64;
            WriteInt(offset, node.Left); WriteInt(offset + 4, node.Right);
            WriteInt(offset + 8, node.Start); WriteInt(offset + 12, node.End);
            WriteFloat(offset + 16, node.Minimum.X); WriteFloat(offset + 20, node.Minimum.Y);
            WriteFloat(offset + 24, node.Minimum.Z); WriteFloat(offset + 28, node.Power);
            WriteFloat(offset + 48, node.Maximum.X); WriteFloat(offset + 52, node.Maximum.Y);
            WriteFloat(offset + 56, node.Maximum.Z);
        }
        EnsureEmissiveBuffer((ulong)bytes.Length);
        UploadChangedEmissiveRanges(bytes);
        _packedEmissiveFaces = faces;
        _lightNodes = nodes;
        UpdateComputeDescriptors();
        EmissiveSyncMilliseconds += watch.Elapsed.TotalMilliseconds;
        void WriteInt(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        void WriteFloat(int offset, float value) => WriteInt(offset, BitConverter.SingleToInt32Bits(value));
        static float Linear(byte channel)
        {
            var value = channel / 255F;
            return value <= 0.04045F ? value / 12.92F : MathF.Pow((value + 0.055F) / 1.055F, 2.4F);
        }
    }

    private void UploadChangedEmissiveRanges(byte[] bytes)
    {
        if (_emissiveUploadedBytes.Length != bytes.Length)
            UploadBytes(bytes, _emissiveBuffer, 0);
        else
        {
            // 按 256 字节块合并连续脏区；范围过多时一次上传，避免大量队列提交。
            var ranges = new List<(int Start, int End)>();
            int start = -1;
            for (var offset = 0; offset < bytes.Length; offset += 256)
            {
                var length = Math.Min(256, bytes.Length - offset);
                bool changed = !bytes.AsSpan(offset, length).SequenceEqual(_emissiveUploadedBytes.AsSpan(offset, length));
                if (changed && start < 0) start = offset;
                if (!changed && start >= 0) { ranges.Add((start, offset)); start = -1; }
            }
            if (start >= 0) ranges.Add((start, bytes.Length));
            if (ranges.Count > 32) UploadBytes(bytes, _emissiveBuffer, 0);
            else foreach (var range in ranges)
                UploadBytes(bytes[range.Start..range.End], _emissiveBuffer, range.Start);
        }
        _emissiveUploadedBytes = bytes;
    }

    private void DestroyEmissiveBuffer()
    {
        if (_emissiveBuffer.Handle == 0 || _api is null) return;
        _api.DestroyBuffer(_device, _emissiveBuffer, null);
        _api.FreeMemory(_device, _emissiveMemory, null);
        _emissiveBuffer = default;
        _emissiveMemory = default;
        _emissiveCapacity = 0;
        _emissiveUploadedBytes = [];
    }
}
