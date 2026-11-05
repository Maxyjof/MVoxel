using System.Numerics;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 程序化树生成：按风格与参数把树干、枝条、树冠和叶片盖章成体素
/// </summary>
/// <remarks>
/// 生成完全由参数与种子决定，同一组参数永远得到同一棵树。
/// 所有形状都按体素中心判断是否覆盖，与曲线渲染器的盖章口径一致；
/// 单次生成有体素上限，超出立即停止并置位截断标记
/// </remarks>
public static class ProceduralTree
{
    /// <summary>
    /// 按参数生成一棵树，返回局部坐标的体素世界
    /// </summary>
    /// <param name="data">树参数</param>
    /// <param name="maximumVoxels">体素数量上限</param>
    /// <param name="truncated">是否因达到上限而截断</param>
    /// <returns>局部坐标的体素世界</returns>
    public static VoxelWorld Generate(TreeGeneratorComponentData data, int maximumVoxels, out bool truncated)
    {
        var builder = new TreeBuilder(data.Normalize(), maximumVoxels);
        builder.Build();
        truncated = builder.Truncated;
        return builder.World;
    }

    /// <summary>
    /// 单次生成的内部状态：体素世界、去重集合、随机流与体素上限
    /// </summary>
    private sealed class TreeBuilder
    {
        private readonly TreeGeneratorComponentData _data;
        private readonly VoxelWorld _world = new();
        private readonly VoxelWorld.BulkWriter _writer;
        private readonly HashSet<VoxelCoord64> _seen = [];
        private readonly int _maximumVoxels;
        private readonly float _height;
        private readonly float _leanAzimuth;
        private uint _randomState;

        internal TreeBuilder(TreeGeneratorComponentData data, int maximumVoxels)
        {
            _data = data;
            _maximumVoxels = Math.Max(1, maximumVoxels);
            _writer = _world.CreateBulkWriter();
            _height = data.TrunkHeight;
            _randomState = (uint)(data.Seed * 2654435761L) | 1U;
            _leanAzimuth = NextFloat() * MathF.Tau;
        }

        internal bool Truncated { get; private set; }

        internal VoxelWorld World => _world;

        internal void Build()
        {
            switch (_data.Style)
            {
                case TreeStyle.Conifer:
                    StampTrunk();
                    StampCrown();
                    break;
                case TreeStyle.Bush:
                    StampRoots(0.4F);
                    StampTrunk();
                    GrowBranches(leafy: true);
                    StampCrown();
                    break;
                case TreeStyle.Palm:
                    var top = StampTrunk();
                    StampPalmFronds(top);
                    break;
                case TreeStyle.Willow:
                    StampRoots(1F);
                    var willowTop = StampTrunk();
                    GrowBranches(leafy: true);
                    StampCrown();
                    StampWillowStrands(willowTop);
                    break;
                case TreeStyle.Dead:
                    StampRoots(1F);
                    StampTrunk();
                    GrowBranches(leafy: false);
                    if (_data.CrownShape != TreeCrownShape.None) StampCrown();
                    break;
                default:
                    StampRoots(1F);
                    StampTrunk();
                    GrowBranches(leafy: true);
                    StampCrown();
                    break;
            }
        }

        /// <summary>
        /// 树干中轴线：直线为主，倾斜与弯曲参数叠加
        /// </summary>
        private Vector3 TrunkPoint(float t)
        {
            var lean = MathF.Tan(_data.TrunkLean * MathF.PI / 180F) * _height * t;
            var x = MathF.Cos(_leanAzimuth) * lean;
            var z = MathF.Sin(_leanAzimuth) * lean;
            // 弯曲是垂直方向上的正弦起伏，两端回落到轴线，保证树根贴地
            var bend = _data.TrunkBend * _height * 0.16F * MathF.Sin(t * MathF.PI);
            x += MathF.Cos(_leanAzimuth + MathF.PI * .5F) * bend;
            z += MathF.Sin(_leanAzimuth + MathF.PI * .5F) * bend;
            return new Vector3(x, _height * t, z);
        }

        /// <summary>
        /// 树干半径：底部外扩、顶部按收缩参数变细
        /// </summary>
        private float TrunkRadiusAt(float t)
        {
            var flare = 1F + _data.RootFlare * 0.8F * MathF.Pow(1F - t, 3F);
            var taper = 1F - _data.TrunkTaper * t;
            return MathF.Max(0.45F, _data.TrunkRadius * flare * taper);
        }

        /// <summary>
        /// 盖章整根树干，返回树干顶部位置
        /// </summary>
        private Vector3 StampTrunk()
        {
            var steps = Math.Clamp((int)MathF.Ceiling(_height * 2.5F), 4, 400);
            var top = Vector3.Zero;
            for (var step = 0; step <= steps; step++)
            {
                var t = step / (float)steps;
                top = TrunkPoint(t);
                if (!StampSphere(top, TrunkRadiusAt(t), _data.BarkValue)) return top;
            }

            return top;
        }

        /// <summary>
        /// 树根：贴近地面的几条外扩短枝
        /// </summary>
        private void StampRoots(float strength)
        {
            if (_data.RootFlare * strength < 0.05F) return;
            var count = Math.Clamp((int)MathF.Round(3F + _data.TrunkRadius * 1.5F), 3, 8);
            for (var index = 0; index < count; index++)
            {
                var azimuth = MathF.Tau * index / count + NextFloat() * 0.6F;
                var reach = _data.TrunkRadius * (1.4F + _data.RootFlare * 1.8F) * (0.7F + NextFloat() * 0.5F);
                var start = new Vector3(0F, _data.TrunkRadius * 0.45F, 0F);
                var end = new Vector3(MathF.Cos(azimuth) * reach, 0.4F, MathF.Sin(azimuth) * reach);
                if (!StampTaperedLine(start, end, _data.TrunkRadius * 0.55F, 0.5F, _data.BarkValue)) return;
            }
        }

        /// <summary>
        /// 主枝与次级枝，数量、角度、长度与分叉层数来自参数
        /// </summary>
        private void GrowBranches(bool leafy)
        {
            if (_data.BranchCount <= 0) return;
            var top = MathF.Max(0.35F, _data.CrownStart);
            for (var index = 0; index < _data.BranchCount; index++)
            {
                var t = _data.BranchCount == 1
                    ? MathF.Min(0.95F, top + 0.2F)
                    : top * 0.9F + (0.95F - top * 0.9F) * (index / (float)(_data.BranchCount - 1));
                var origin = TrunkPoint(Math.Clamp(t, 0.15F, 0.98F));
                var azimuth = MathF.Tau * index / _data.BranchCount + MathF.PI * .25F + NextFloat() * 0.8F;
                var length = _data.BranchLength * (0.7F + NextFloat() * 0.6F);
                var direction = BranchDirection(azimuth, _data.BranchAngle);
                if (!GrowBranch(origin, direction, length, _data.BranchThickness, _data.BranchDepth, leafy))
                    return;
            }
        }

        /// <summary>
        /// 递归生长一根枝条，末端按需加叶团
        /// </summary>
        private bool GrowBranch(Vector3 origin, Vector3 direction, float length, float thickness, int depth,
            bool leafy)
        {
            var droop = new Vector3(0F, -_data.BranchDroop * length * 0.7F, 0F);
            var end = origin + direction * length + droop;
            if (!StampTaperedLine(origin, end, thickness, MathF.Max(0.4F, thickness * 0.55F), _data.BarkValue))
                return false;
            if (leafy && _data.CrownShape != TreeCrownShape.None &&
                (_data.Style is TreeStyle.Broadleaf or TreeStyle.Cherry or TreeStyle.Bush or TreeStyle.Willow))
                LeafCluster(end, MathF.Max(0.8F, _data.LeafSize * 0.9F));
            if (depth <= 0 || Truncated) return true;
            var children = 2 + (NextFloat() < 0.4F ? 1 : 0);
            for (var child = 0; child < children; child++)
            {
                var split = (child - (children - 1) * .5F) * (18F + NextFloat() * 22F);
                var childAzimuth = MathF.Atan2(direction.Z, direction.X) + split * MathF.PI / 180F;
                var childPitch = MathF.Max(4F, _data.BranchAngle * (0.55F + NextFloat() * 0.35F));
                var childDirection = BranchDirection(childAzimuth, childPitch);
                var start = Vector3.Lerp(origin, end, 0.65F);
                if (!GrowBranch(start, childDirection, length * 0.62F, MathF.Max(0.4F, thickness * 0.6F),
                        depth - 1, leafy)) return false;
            }

            return true;
        }

        /// <summary>
        /// 由方位角与上扬角构造单位枝条方向
        /// </summary>
        private static Vector3 BranchDirection(float azimuth, float pitchDegrees)
        {
            var pitch = pitchDegrees * MathF.PI / 180F;
            var horizontal = MathF.Cos(pitch);
            return new Vector3(MathF.Cos(azimuth) * horizontal, MathF.Sin(pitch), MathF.Sin(azimuth) * horizontal);
        }

        /// <summary>
        /// 树冠：按形态在树干顶部上方铺满叶片
        /// </summary>
        private void StampCrown()
        {
            if (_data.CrownShape == TreeCrownShape.None || _data.LeafDensity <= 0.01F) return;
            var bottom = _data.CrownStart * _height;
            var crownHeight = _data.CrownHeight;
            var centerY = bottom + crownHeight * 0.5F;
            var top = TrunkPoint(1F);
            var center = new Vector3(top.X, centerY, top.Z);
            var horizontalRadius = _data.CrownRadius;
            var verticalRadius = crownHeight * 0.5F;
            switch (_data.CrownShape)
            {
                case TreeCrownShape.Cone:
                    // 锥形树冠必须盖住树干顶端，否则树干会从树冠上方戳出来
                    var coneTop = MathF.Max(bottom + crownHeight, _height + 1.5F);
                    StampConeCrown(center.X, center.Z, bottom, coneTop, horizontalRadius);
                    break;
                case TreeCrownShape.Layered:
                    StampLayeredCrown(center.X, center.Z, bottom, bottom + crownHeight, horizontalRadius);
                    break;
                case TreeCrownShape.Umbrella:
                    StampDomeCrown(center, horizontalRadius, verticalRadius, shellBias: true);
                    break;
                case TreeCrownShape.Sphere:
                    StampEllipsoidCrown(center, horizontalRadius, horizontalRadius, horizontalRadius);
                    break;
                default:
                    StampEllipsoidCrown(center, horizontalRadius, verticalRadius, horizontalRadius);
                    break;
            }
        }

        /// <summary>
        /// 椭球树冠：按密度随机留空，边缘加一点扰动避免过于规整
        /// </summary>
        private void StampEllipsoidCrown(Vector3 center, float radiusX, float radiusY, float radiusZ)
        {
            var minX = (int)MathF.Floor(center.X - radiusX);
            var maxX = (int)MathF.Ceiling(center.X + radiusX);
            var minY = (int)MathF.Floor(center.Y - radiusY);
            var maxY = (int)MathF.Ceiling(center.Y + radiusY);
            var minZ = (int)MathF.Floor(center.Z - radiusZ);
            var maxZ = (int)MathF.Ceiling(center.Z + radiusZ);
            for (var x = minX; x <= maxX; x++)
            for (var y = minY; y <= maxY; y++)
            for (var z = minZ; z <= maxZ; z++)
            {
                var nx = (x + 0.5F - center.X) / MathF.Max(0.1F, radiusX);
                var ny = (y + 0.5F - center.Y) / MathF.Max(0.1F, radiusY);
                var nz = (z + 0.5F - center.Z) / MathF.Max(0.1F, radiusZ);
                var normalized = nx * nx + ny * ny + nz * nz;
                // 外缘按噪声抖动，让树冠轮廓不是标准椭球
                var wobble = 1F + (Noise(x, y, z) - 0.5F) * 0.16F;
                if (normalized > wobble) continue;
                if (NextFloat() > _data.LeafDensity) continue;
                if (!StampLeaf(x, y, z)) return;
            }
        }

        /// <summary>
        /// 针叶树的层叠锥形树冠
        /// </summary>
        private void StampConeCrown(float centerX, float centerZ, float bottom, float top, float radius)
        {
            var span = MathF.Max(1F, top - bottom);
            var layers = Math.Clamp((int)MathF.Round(span / 2.4F), 3, 18);
            // 收尖处留出树干顶端的半径，避免树干从锥顶戳出来
            var tipRadius = MathF.Max(0.9F, TrunkRadiusAt(1F));
            for (var y = (int)MathF.Floor(bottom); y <= (int)MathF.Ceiling(top); y++)
            {
                var t = Math.Clamp((y + 0.5F - bottom) / span, 0F, 1F);
                var phase = t * layers;
                phase -= MathF.Floor(phase);
                var layerRadius = radius * (1F - t) * (0.55F + 0.45F * (1F - phase));
                if (layerRadius < tipRadius) layerRadius = tipRadius;
                // 整层填实：锥形树冠的圆盘较小，留密度孔会让树干从缝里露出来
                StampDisc(centerX, y, centerZ, layerRadius, shellOnly: false, solid: true);
                if (Truncated) return;
            }
        }

        /// <summary>
        /// 层叠树冠：几层水平圆盘，越往上越小
        /// </summary>
        private void StampLayeredCrown(float centerX, float centerZ, float bottom, float top, float radius)
        {
            var span = MathF.Max(1F, top - bottom);
            var layers = Math.Clamp((int)MathF.Round(span / 3.2F), 2, 10);
            for (var layer = 0; layer < layers; layer++)
            {
                var t = layers == 1 ? 0F : layer / (float)(layers - 1);
                var y = bottom + span * (0.15F + t * 0.8F);
                var layerRadius = radius * (1F - t * 0.65F);
                var thickness = MathF.Max(1F, span / layers * 0.55F);
                for (var step = 0; step < (int)MathF.Ceiling(thickness); step++)
                    StampDisc(centerX, (int)MathF.Round(y) + step, centerZ, layerRadius, shellOnly: false);
                if (Truncated) return;
            }
        }

        /// <summary>
        /// 伞形树冠：中间高四周低的穹顶
        /// </summary>
        private void StampDomeCrown(Vector3 center, float radius, float height, bool shellBias)
        {
            var minX = (int)MathF.Floor(center.X - radius);
            var maxX = (int)MathF.Ceiling(center.X + radius);
            var minZ = (int)MathF.Floor(center.Z - radius);
            var maxZ = (int)MathF.Ceiling(center.Z + radius);
            for (var x = minX; x <= maxX; x++)
            for (var z = minZ; z <= maxZ; z++)
            {
                var dx = x + 0.5F - center.X;
                var dz = z + 0.5F - center.Z;
                var distance = MathF.Sqrt(dx * dx + dz * dz) / MathF.Max(0.1F, radius);
                if (distance > 1F) continue;
                var domeHeight = height * MathF.Sqrt(MathF.Max(0F, 1F - distance * distance));
                var top = (int)MathF.Ceiling(center.Y + domeHeight);
                var bottom = (int)MathF.Floor(center.Y + domeHeight * (shellBias ? 0.55F : 0.15F));
                for (var y = bottom; y <= top; y++)
                {
                    if (NextFloat() > _data.LeafDensity) continue;
                    if (!StampLeaf(x, y, z)) return;
                }
            }
        }

        /// <summary>
        /// 水平圆盘，密度仍按叶片参数过滤
        /// </summary>
        private void StampDisc(float centerX, int y, float centerZ, float radius, bool shellOnly, bool solid = false)
        {
            var minimumX = (int)MathF.Floor(centerX - radius);
            var maximumX = (int)MathF.Ceiling(centerX + radius);
            var minimumZ = (int)MathF.Floor(centerZ - radius);
            var maximumZ = (int)MathF.Ceiling(centerZ + radius);
            for (var x = minimumX; x <= maximumX; x++)
            for (var z = minimumZ; z <= maximumZ; z++)
            {
                var dx = x + 0.5F - centerX;
                var dz = z + 0.5F - centerZ;
                var distance = MathF.Sqrt(dx * dx + dz * dz);
                if (distance > radius) continue;
                if (shellOnly && distance < radius - 0.75F) continue;
                if (!solid && NextFloat() > _data.LeafDensity) continue;
                if (!StampLeaf(x, y, z)) return;
            }
        }

        /// <summary>
        /// 棕榈叶片：从树冠顶部向外伸展、向下垂的几条叶带
        /// </summary>
        private void StampPalmFronds(Vector3 top)
        {
            var fronds = Math.Clamp((int)MathF.Round(4F + _data.CrownRadius * 0.7F), 5, 14);
            var length = MathF.Max(2F, _data.CrownRadius);
            for (var frond = 0; frond < fronds; frond++)
            {
                var azimuth = MathF.Tau * frond / fronds + NextFloat() * 0.25F;
                var steps = Math.Clamp((int)MathF.Ceiling(length * 2F), 4, 80);
                for (var step = 0; step <= steps; step++)
                {
                    var t = step / (float)steps;
                    var reach = length * t;
                    var lift = length * 0.28F * MathF.Sin(t * MathF.PI * 0.9F);
                    var drop = length * (0.15F + _data.BranchDroop * 0.55F) * t * t;
                    var point = new Vector3(
                        top.X + MathF.Cos(azimuth) * reach,
                        top.Y + lift - drop,
                        top.Z + MathF.Sin(azimuth) * reach);
                    if (!LeafCluster(point, MathF.Max(0.7F, _data.LeafSize * 0.75F))) return;
                }
            }
        }

        /// <summary>
        /// 垂柳枝条：从树冠边缘垂下的细长叶串
        /// </summary>
        private void StampWillowStrands(Vector3 top)
        {
            var count = Math.Clamp((int)MathF.Round(_data.CrownRadius * 1.6F), 6, 48);
            var bottom = _data.CrownStart * _height;
            for (var strand = 0; strand < count; strand++)
            {
                var azimuth = NextFloat() * MathF.Tau;
                var radius = _data.CrownRadius * (0.55F + NextFloat() * 0.45F);
                var start = new Vector3(top.X + MathF.Cos(azimuth) * radius,
                    top.Y - _data.CrownHeight * 0.15F + NextFloat() * _data.CrownHeight * 0.3F,
                    top.Z + MathF.Sin(azimuth) * radius);
                var length = MathF.Max(2F, (start.Y - bottom) * (0.5F + NextFloat() * 0.6F));
                var steps = Math.Clamp((int)MathF.Ceiling(length * 1.6F), 3, 80);
                for (var step = 0; step <= steps; step++)
                {
                    var t = step / (float)steps;
                    var sway = MathF.Sin(t * 5F + strand) * 0.6F;
                    var point = new Vector3(start.X + sway, start.Y - length * t, start.Z - sway * .5F);
                    if (!StampLeafChain(point)) return;
                }
            }
        }

        /// <summary>
        /// 在给定位置盖一小团叶片
        /// </summary>
        private bool LeafCluster(Vector3 center, float radius)
        {
            return StampSphere(center, radius, null, leaf: true);
        }

        /// <summary>
        /// 垂柳的细叶串：每个位置只盖一小簇，保持纤细
        /// </summary>
        private bool StampLeafChain(Vector3 center)
        {
            var x = (int)MathF.Floor(center.X);
            var y = (int)MathF.Floor(center.Y);
            var z = (int)MathF.Floor(center.Z);
            if (Noise(x, y, z) > _data.LeafDensity) return true;
            return StampLeaf(x, y, z);
        }

        /// <summary>
        /// 盖章一个球体，可为树皮或叶片
        /// </summary>
        private bool StampSphere(Vector3 center, float radius, VoxelValue? bark, bool leaf = false)
        {
            var filled = bark ?? _data.BarkValue;
            if (radius <= 0.55F)
            {
                var cell = new VoxelCoord64((long)MathF.Floor(center.X), (long)MathF.Floor(center.Y),
                    (long)MathF.Floor(center.Z));
                return leaf ? StampLeaf(cell) : SetVoxel(cell, filled);
            }

            var minimumX = (int)MathF.Floor(center.X - radius);
            var maximumX = (int)MathF.Ceiling(center.X + radius);
            var minimumY = (int)MathF.Floor(center.Y - radius);
            var maximumY = (int)MathF.Ceiling(center.Y + radius);
            var minimumZ = (int)MathF.Floor(center.Z - radius);
            var maximumZ = (int)MathF.Ceiling(center.Z + radius);
            for (var x = minimumX; x <= maximumX; x++)
            for (var y = minimumY; y <= maximumY; y++)
            for (var z = minimumZ; z <= maximumZ; z++)
            {
                var dx = x + 0.5F - center.X;
                var dy = y + 0.5F - center.Y;
                var dz = z + 0.5F - center.Z;
                if (dx * dx + dy * dy + dz * dz > radius * radius) continue;
                var cell = new VoxelCoord64(x, y, z);
                if (leaf)
                {
                    if (!StampLeaf(cell)) return false;
                    continue;
                }

                if (!SetVoxel(cell, filled)) return false;
            }

            return true;
        }

        /// <summary>
        /// 盖章一段圆锥形枝条
        /// </summary>
        private bool StampTaperedLine(Vector3 start, Vector3 end, float startRadius, float endRadius, VoxelValue value)
        {
            var distance = Vector3.Distance(start, end);
            var steps = Math.Clamp((int)MathF.Ceiling(distance * 2F), 1, 512);
            for (var step = 0; step <= steps; step++)
            {
                var t = step / (float)steps;
                var center = Vector3.Lerp(start, end, t);
                var radius = MathF.Max(0.45F, startRadius + (endRadius - startRadius) * t);
                if (!StampSphere(center, radius, value)) return false;
            }

            return true;
        }

        /// <summary>
        /// 写入一片叶子，颜色按坐标哈希在主色与辅色之间混合
        /// </summary>
        private bool StampLeaf(int x, int y, int z) => StampLeaf(new VoxelCoord64(x, y, z));

        private bool StampLeaf(VoxelCoord64 cell)
        {
            var value = _data.ColorVariation > 0.001F &&
                        Noise((int)cell.X, (int)cell.Y, (int)cell.Z) < _data.ColorVariation
                ? _data.Leaf2Value : _data.LeafValue;
            return SetVoxel(cell, value);
        }

        /// <summary>
        /// 写入一个体素，重复坐标只记一次，超过上限立即截断
        /// </summary>
        /// <summary>
        /// 写入一个体素，重复坐标只记一次，超过上限立即截断
        /// </summary>
        /// <remarks>
        /// 重复坐标要用后写入的值覆盖：树叶在树干之后盖章，必须能盖住树干中心，
        /// 否则锥形树冠的尖端会露出一段棕色树干
        /// </remarks>
        private bool SetVoxel(VoxelCoord64 coordinate, VoxelValue value)
        {
            if (!_seen.Add(coordinate))
            {
                _writer.Set(coordinate, value);
                return true;
            }

            if (_seen.Count > _maximumVoxels)
            {
                Truncated = true;
                return false;
            }

            _writer.Set(coordinate, value);
            return true;
        }

        /// <summary>
        /// 坐标哈希噪声，稳定且与进程无关
        /// </summary>
        private static uint Hash(int x, int y, int z)
        {
            unchecked
            {
                var hash = (uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)z * 83492791u;
                hash ^= hash >> 13;
                hash *= 0x5BD1E995u;
                hash ^= hash >> 15;
                return hash;
            }
        }

        private static float Noise(int x, int y, int z) => (Hash(x, y, z) & 0xFFFFu) / 65535F;

        private float NextFloat()
        {
            unchecked
            {
                var state = _randomState;
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                _randomState = state;
                return (state & 0xFFFFFFu) / 16777216F;
            }
        }
    }
}
