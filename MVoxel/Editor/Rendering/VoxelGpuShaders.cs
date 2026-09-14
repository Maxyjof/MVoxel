using MVoxel.Rendering;

namespace MVoxel.Editor.Rendering;

/// <summary>
/// 体素视口的 GLSL 4.50 源码, 所有通道共用同一个光线追踪内核, 通过普通的
/// 全屏片元通道驱动(跨驱动与上下文更稳健; 本 NVIDIA/OpenGL 栈上 compute
/// image 写入会被静默丢弃, 已实测验证):
///
/// 场景通道: 一个片元对应一条主射线(MagicaVoxel 风格的直接光、角点 AO、
/// 柔和太阳阴影、雾与地面渐隐)
/// 追踪通道: 一个片元对应 N 个新的路径追踪采样, 混合进上一帧累积纹理
/// (ping-pong 的 RGBA32F 渲染目标)
/// 呈现通道: 对 HDR 累积结果做色调映射, 输出到 LDR 显示纹理
///
/// 技术谱系: 角点 AO、调色板着色与单元描边来自 MagicaVoxel; Whitted 传输、
/// 权重阈值终止、Schlick 菲涅尔、Beer-Lambert 吸收与抖动软阴影移植自参考
/// 项目 Ray-tracing-by-AI, 并补上了它缺失的余弦加权采样、GGX 重要性采样、
/// next-event MIS(太阳/天空/自发光)、辐亮度钳制与渐进式累积
///
/// 色彩管理: 体素字节为 sRGB, 光照在线性空间计算, 输出经过显式色调曲线
/// 与 gamma(见文档 05 §5)
/// </summary>
internal static class VoxelGpuShaders
{
    /// <summary>
    /// 由 gl_VertexID 直接生成全屏三角形, 尺寸交由视口决定
    /// </summary>
    internal const string Vertex = """
#version 450
void main()
{
    vec2 corners[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
    gl_Position = vec4(corners[gl_VertexID], 0.0, 1.0);
}
""";

    /// <summary>
    /// 共享内核, 其中 <c>#include "defines"</c> 槽位会被 <see cref="BuildCommon"/>
    /// 生成的参数表替换
    /// </summary>
    internal const string Common = """
#include "defines"

layout(std430, binding = 0) readonly buffer MVParams { float U[]; };
layout(std430, binding = 1) readonly buffer MVLights { vec4 MVLight[]; };
// 灯光按对存放: [center.xyz, intensity] [color.rgb, pad]

uniform sampler3D uColors; // RGBA8 区域工作集(sRGB 颜色 + alpha)
uniform usampler3D uFlags; // R8UI: 占用|材质标记(0 表示空格)

const float PI = 3.141592653589793;

// ImGui 在四边形顶边采样纹理第 0 行, 而第 0 行恰好对应
// gl_FragCoord.y == 0 的位置; 因此片元行索引本身就是 SceneProjection
// 使用的自上而下屏幕像素, 无需翻转
ivec2 screen_pixel()
{
    return ivec2(gl_FragCoord.xy);
}

// ===== 确定性 RNG(PCG 哈希流) =====
uint g_seed;
void seed_rng(uint s)
{
    s ^= s >> 16u; s *= 0x7feb352dU; s ^= s >> 15u; s *= 0x846ca68bU; s ^= s >> 16u;
    g_seed = s | 1U;
}
uint hash_u()
{
    g_seed = g_seed * 747796405U + 2891336453U;
    uint word = ((g_seed >> ((g_seed >> 28U) + 4U)) ^ g_seed) * 277803737U;
    return word ^ (word >> 22U);
}
float rnd() { return float(hash_u()) * (1.0 / 4294967296.0); }
vec2 rnd2_disk() { float a = rnd() * 6.2831853; float r = sqrt(rnd()); return vec2(cos(a), sin(a)) * r; }

// ===== 色彩空间 =====
vec3 srgb_to_linear(vec3 c)
{
    vec3 lo = c / 12.92;
    vec3 hi = pow((c + 0.055) / 1.055, vec3(2.4));
    return mix(hi, lo, vec3(lessThanEqual(c, vec3(0.04045))));
}

vec3 hue_rotate(vec3 color, float degrees)
{
    float a = radians(degrees);
    const vec3 k = vec3(0.57735027);
    float cs = cos(a), sn = sin(a);
    return color * cs + cross(k, color) * sn + k * dot(k, color) * (1.0 - cs);
}

vec3 palette_shade(vec3 linearColor)
{
    if (U[P_PALETTE_SHADING] < 0.5) return max(linearColor, vec3(0.0));
    vec3 c = hue_rotate(linearColor, U[P_HUE_SHIFT]);
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
    c = mix(vec3(luma), c, U[P_SATURATION]);
    c = (c - 0.5) * U[P_CONTRAST] + 0.5;
    return max(c * U[P_BRIGHTNESS], vec3(0.0));
}

// ===== 区域采样 =====
ivec3 region_min() { return ivec3(int(U[P_REGION_MIN_X]), int(U[P_REGION_MIN_Y]), int(U[P_REGION_MIN_Z])); }
ivec3 region_size() { return ivec3(int(U[P_REGION_SIZE_X]), int(U[P_REGION_SIZE_Y]), int(U[P_REGION_SIZE_Z])); }

bool in_region(ivec3 cell)
{
    return all(greaterThanEqual(cell, ivec3(0))) && all(lessThan(cell, region_size()));
}

bool solid_at(ivec3 regionCell)
{
    if (!in_region(regionCell)) return false;
    return texelFetch(uFlags, regionCell, 0).r != 0U;
}

vec3 sky_color(vec3 direction)
{
    float t = clamp(direction.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 horizon = vec3(U[P_SKY_HORIZON_R], U[P_SKY_HORIZON_G], U[P_SKY_HORIZON_B]);
    vec3 top = vec3(U[P_SKY_TOP_R], U[P_SKY_TOP_G], U[P_SKY_TOP_B]);
    return mix(srgb_to_linear(horizon), srgb_to_linear(top) * 1.05, pow(t, 0.75));
}

vec3 sky_ambient(vec3 normal)
{
    vec3 horizon = srgb_to_linear(vec3(U[P_SKY_HORIZON_R], U[P_SKY_HORIZON_G], U[P_SKY_HORIZON_B]));
    vec3 top = srgb_to_linear(vec3(U[P_SKY_TOP_R], U[P_SKY_TOP_G], U[P_SKY_TOP_B]));
    vec3 hemisphere = mix(horizon, top, clamp(normal.y * 0.5 + 0.5, 0.0, 1.0));
    return hemisphere * U[P_SKY_AMBIENT] + vec3(1.0, 0.92, 0.82) * U[P_AMBIENT_TINT];
}

vec3 sun_color() { return vec3(U[P_SUN_R], U[P_SUN_G], U[P_SUN_B]) * U[P_SUN_INTENSITY]; }
vec3 sun_direction() { return vec3(U[P_SUN_X], U[P_SUN_Y], U[P_SUN_Z]); }

// ===== Amanatides 与 Woo 体素遍历 =====
// 托管侧的 VoxelDda 与本遍历逐步一致, 保证拾取结果与像素吻合
struct VHit
{
    bool hit;
    ivec3 region;
    vec3 point;
    vec3 normal;
    uint flags;
    vec4 color;
    float t;
};

bool voxel_region_ray(vec3 origin, vec3 direction, out float nearDistance, out float farDistance)
{
    vec3 minimum = vec3(region_min());
    vec3 maximum = minimum + vec3(region_size());
    if (abs(direction.x) < 1e-7 && (origin.x < minimum.x || origin.x > maximum.x)) return false;
    if (abs(direction.y) < 1e-7 && (origin.y < minimum.y || origin.y > maximum.y)) return false;
    if (abs(direction.z) < 1e-7 && (origin.z < minimum.z || origin.z > maximum.z)) return false;

    vec3 safeDirection = vec3(
        abs(direction.x) < 1e-7 ? 1.0 : direction.x,
        abs(direction.y) < 1e-7 ? 1.0 : direction.y,
        abs(direction.z) < 1e-7 ? 1.0 : direction.z);
    vec3 inverseDirection = 1.0 / safeDirection;
    vec3 first = (minimum - origin) * inverseDirection;
    vec3 second = (maximum - origin) * inverseDirection;
    vec3 lower = min(first, second);
    vec3 upper = max(first, second);
    nearDistance = max(max(lower.x, lower.y), lower.z);
    farDistance = min(min(upper.x, upper.y), upper.z);
    return farDistance >= max(nearDistance, 0.0);
}

VHit voxel_ray(vec3 origin, vec3 direction, float maxDistance, ivec3 skipCell)
{
    VHit result;
    result.hit = false;
    result.t = maxDistance;
    result.region = ivec3(0);
    result.point = origin;
    result.normal = vec3(0.0);
    result.flags = 0U;
    result.color = vec4(0.0);

    float regionNear;
    float regionFar;
    if (!voxel_region_ray(origin, direction, regionNear, regionFar) || regionNear > maxDistance)
    {
        return result;
    }

    float travelled = max(regionNear, 0.0);
    vec3 entryPoint = origin + direction * travelled + direction * 0.0001;
    vec3 stepSign = sign(direction);
    ivec3 step = ivec3(stepSign);
    ivec3 cell = ivec3(floor(entryPoint)) - region_min();

    vec3 o = entryPoint - vec3(region_min());
    float tMax[3];
    float tDelta[3];
    float absDir[3] = float[3](abs(direction.x), abs(direction.y), abs(direction.z));
    float dd[3] = float[3](direction.x, direction.y, direction.z);
    float oo[3] = float[3](o.x, o.y, o.z);
    float sgn[3] = float[3](stepSign.x, stepSign.y, stepSign.z);
    for (int axis = 0; axis < 3; axis++)
    {
        tDelta[axis] = absDir[axis] < 1e-7 ? 1e30 : 1.0 / max(absDir[axis], 1e-30);
        float boundary = sgn[axis] > 0.0 ? floor(oo[axis]) + 1.0 : floor(oo[axis]);
        tMax[axis] = absDir[axis] < 1e-7 ? 1e30 : travelled + abs((boundary - oo[axis]) / dd[axis]);
    }

    // 射线起点已在体素内部时的入射面: 取主导轴
    vec3 entryNormal;
    if (absDir[0] >= absDir[1] && absDir[0] >= absDir[2]) entryNormal = vec3(-stepSign.x, 0.0, 0.0);
    else if (absDir[1] >= absDir[2]) entryNormal = vec3(0.0, -stepSign.y, 0.0);
    else entryNormal = vec3(0.0, 0.0, -stepSign.z);

    int budget = int(U[P_MAX_STEPS]);
    while (budget-- > 0)
    {
        if (in_region(cell) && cell != skipCell)
        {
            uint flags = texelFetch(uFlags, cell, 0).r;
            if (flags != 0U)
            {
                result.hit = true;
                result.region = cell;
                result.t = travelled;
                result.normal = entryNormal;
                result.flags = flags;
                result.color = texelFetch(uColors, cell, 0);
                result.point = origin + direction * travelled;
                return result;
            }
        }

        if (tMax[0] <= tMax[1] && tMax[0] <= tMax[2])
        {
            travelled = tMax[0];
            tMax[0] += tDelta[0];
            cell.x += step.x;
            entryNormal = vec3(-stepSign.x, 0.0, 0.0);
        }
        else if (tMax[1] <= tMax[2])
        {
            travelled = tMax[1];
            tMax[1] += tDelta[1];
            cell.y += step.y;
            entryNormal = vec3(0.0, -stepSign.y, 0.0);
        }
        else
        {
            travelled = tMax[2];
            tMax[2] += tDelta[2];
            cell.z += step.z;
            entryNormal = vec3(0.0, 0.0, -stepSign.z);
        }
        if (travelled > maxDistance) break;
    }
    return result;
}

VHit voxel_ray(vec3 origin, vec3 direction, float maxDistance)
{
    return voxel_ray(origin, direction, maxDistance, ivec3(-999999));
}

bool shadow_ray(vec3 origin, vec3 direction, float maxDistance)
{
    return !voxel_ray(origin, direction, maxDistance).hit;
}

// ===== 地面平面(y = 0) =====
bool ground_hit(vec3 origin, vec3 direction, out float t)
{
    t = 0.0;
    if (U[P_GROUND_SHOWN] < 0.5 || direction.y > -1e-5) return false;
    float candidate = -origin.y / direction.y;
    if (candidate <= 0.0 || candidate >= 1e9 || candidate > 4096.0) return false;
    t = candidate;
    return true;
}

vec3 ground_radiance(vec3 point, vec3 rayDirection)
{
    vec3 groundBase = srgb_to_linear(vec3(U[P_GROUND_R], U[P_GROUND_G], U[P_GROUND_B]));
    float lit = 1.0;
    vec3 sun = sun_direction();
    if (U[P_SUN_INTENSITY] > 0.0 && sun.y > 0.0)
    {
        VHit blocker = voxel_ray(point + vec3(0.0, 0.02, 0.0), sun, 512.0);
        lit = blocker.hit ? 0.12 : 1.0; // 少量弹射补光, 保证阴影区域仍然可读
    }
    vec3 litColor = groundBase * (sky_ambient(vec3(0.0, 1.0, 0.0)) * 0.55 + sun_color() * max(sun.y, 0.0) * lit * 0.7);
    float fade = clamp(1.0 - distance(point, vec3(U[P_CAM_X], 0.0, U[P_CAM_Z])) / U[P_GROUND_FADE], 0.0, 1.0);
    vec3 result = mix(sky_color(rayDirection), litColor, fade);

    // 深度正确的编辑器网格: 次级线与主线(每 8 格一条)的世界空间直线,
    // 借助屏幕空间导数保持约 1 像素宽, 并随距离渐隐
    if (U[P_GRID_SHOWN] > 0.5 && U[P_GRID_STEP] > 0.001)
    {
        float stepSize = U[P_GRID_STEP];
        vec2 g = point.xz / stepSize;
        vec2 d = abs(fract(g - 0.5) - 0.5) / max(fwidth(g), vec2(1e-5));
        float minor = 1.0 - min(min(d.x, d.y), 1.0);
        vec2 gm = point.xz / (stepSize * 8.0);
        vec2 dm = abs(fract(gm - 0.5) - 0.5) / max(fwidth(gm), vec2(1e-5));
        float major = 1.0 - min(min(dm.x, dm.y), 1.0);
        float reach = clamp(1.0 - length(point.xz - vec2(U[P_CAM_X], U[P_CAM_Z])) / (stepSize * 192.0), 0.0, 1.0);
        float gridAlpha = clamp(minor * 0.42 + major * 0.34, 0.0, 1.0) * reach * fade;
        vec3 gridColor = mix(vec3(0.20, 0.25, 0.32), vec3(0.33, 0.40, 0.51), clamp(major, 0.0, 1.0));
        result = mix(result, gridColor, gridAlpha);
    }
    return result;
}

// ===== MagicaVoxel 风格角点 AO =====
float corner_term(ivec3 base, ivec3 su, ivec3 sv)
{
    bool s1 = solid_at(base + su);
    bool s2 = solid_at(base + sv);
    bool sc = solid_at(base + su + sv);
    return (s1 && s2) ? 0.0 : (3.0 - (float(s1) + float(s2) + float(sc))) / 3.0;
}

float corner_ao(ivec3 cell, vec3 normal, vec3 point)
{
    if (U[P_AO_ENABLED] < 0.5) return 1.0;
    int axis = abs(normal.x) > 0.5 ? 0 : (abs(normal.y) > 0.5 ? 1 : 2);
    vec3 u = axis == 0 ? vec3(0, 1, 0) : vec3(1, 0, 0);
    vec3 v = axis == 2 ? vec3(0, 1, 0) : vec3(0, 0, 1);
    ivec3 ai = axis == 0 ? ivec3(1, 0, 0) : (axis == 1 ? ivec3(0, 1, 0) : ivec3(0, 0, 1));
    ivec3 iu = ivec3(u), iv = ivec3(v);
    ivec3 base = cell + ai;
    float fracU = fract(dot(point, u));
    float fracV = fract(dot(point, v));

    float c00 = corner_term(base, -iu, -iv);
    float c01 = corner_term(base, -iu, iv);
    float c10 = corner_term(base, iu, -iv);
    float c11 = corner_term(base, iu, iv);
    float raw = mix(mix(c00, c01, fracV), mix(c10, c11, fracV), fracU);
    float shaded = mix(1.0, raw, U[P_AO_STRENGTH]);
    return clamp(pow(max(shaded, 0.0), U[P_AO_POWER]), 0.0, 1.0);
}

// ===== 相机 =====
// 场景视图与 SceneProjection.Project 完全一致(同一焦距约定),
// 渲染视图则使用自己的 FOV; 屏幕 Y 轴与 ImGui 相同, 向下增长
vec3 camera_ray_direction(vec2 pixel, out vec3 rayOrigin)
{
    rayOrigin = vec3(U[P_CAM_X], U[P_CAM_Y], U[P_CAM_Z]);
    vec3 forward = vec3(U[P_FWD_X], U[P_FWD_Y], U[P_FWD_Z]);
    vec3 right = vec3(U[P_RIGHT_X], U[P_RIGHT_Y], U[P_RIGHT_Z]);
    vec3 up = vec3(U[P_UP_X], U[P_UP_Y], U[P_UP_Z]);
    vec2 view = vec2(U[P_VIEW_W], U[P_VIEW_H]);
    vec2 jitter = vec2(rnd() - 0.5, rnd() - 0.5);
    vec2 offset = (pixel + 0.5 + jitter) - view * 0.5;
    if (U[P_MODE_PATH] > 0.5)
    {
        float tanHalf = tan(radians(U[P_FOV_DEGREES]) * 0.5);
        float aspect = view.x / max(view.y, 1.0);
        return normalize(forward + right * (offset.x / (view.x * 0.5) * tanHalf * aspect) - up * (offset.y / (view.y * 0.5) * tanHalf));
    }
    float focal = U[P_FOCAL];
    return normalize(forward + right * (offset.x / focal) - up * (offset.y / focal));
}

// ===== BSDF 组件 =====
vec3 fresnel_schlick(float cosine, vec3 f0)
{
    return f0 + (vec3(1.0) - f0) * pow(clamp(1.0 - cosine, 0.0, 1.0), 5.0);
}

vec3 sample_cosine(vec3 n)
{
    float r1 = rnd(), r2 = rnd();
    float phi = 2.0 * PI * r1;
    float sinT = sqrt(r2), cosT = sqrt(max(0.0, 1.0 - r2));
    vec3 t1 = abs(n.x) < 0.9 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 t2 = normalize(cross(n, t1));
    t1 = cross(n, t2);
    return normalize(t1 * sinT * cos(phi) + t2 * sinT * sin(phi) + n * cosT);
}

float ggx_g1(float cosTheta, float alpha)
{
    float a2 = alpha * alpha;
    return 2.0 * cosTheta / (cosTheta + sqrt(max(a2 + (1.0 - a2) * cosTheta * cosTheta, 1e-9)));
}

vec3 ggx_sample(vec3 n, vec3 v, float alpha)
{
    vec3 t1 = abs(n.x) < 0.9 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    t1 = normalize(t1 - n * dot(t1, n));
    vec3 t2 = cross(n, t1);
    float r1 = rnd(), r2 = rnd();
    float phi = 2.0 * PI * r1;
    float cosTheta = sqrt((1.0 - r2) / max(1.0 + (alpha * alpha - 1.0) * r2, 1e-9));
    float sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));
    vec3 h = normalize(t1 * sinTheta * cos(phi) + t2 * sinTheta * sin(phi) + n * cosTheta);
    return normalize(2.0 * dot(v, h) * h - v);
}

float ggx_pdf(vec3 n, vec3 v, vec3 l, float alpha)
{
    float nl = dot(n, l);
    if (nl <= 0.0) return 0.0;
    vec3 h = normalize(v + l);
    float nh = max(dot(n, h), 0.0);
    float vh = max(dot(v, h), 0.0);
    float a2 = alpha * alpha;
    float denom = nh * nh * (a2 - 1.0) + 1.0;
    float d = a2 / max(PI * denom * denom, 1e-9);
    return d * nh / max(4.0 * vh, 1e-5);
}

vec3 bsdf_eval(int kind, float roughness, vec3 f0, vec3 baseColor, vec3 n, vec3 v, vec3 l)
{
    float nl = dot(n, l);
    if (nl <= 0.0) return vec3(0.0);
    if (kind == 1)
    {
        vec3 h = normalize(v + l);
        float alpha = max(roughness * roughness, 0.0009);
        float nv = max(dot(n, v), 1e-5);
        float nh = max(dot(n, h), 0.0);
        float vh = max(dot(v, h), 0.0);
        float a2 = alpha * alpha;
        float denom = nh * nh * (a2 - 1.0) + 1.0;
        float d = a2 / max(PI * denom * denom, 1e-9);
        vec3 f = fresnel_schlick(vh, f0);
        float g = ggx_g1(nv, alpha) * ggx_g1(nl, alpha);
        return d * g * f / max(4.0 * nv * nl, 1e-6);
    }
    return baseColor * (1.0 / PI);
}

float bsdf_pdf(int kind, float roughness, vec3 n, vec3 v, vec3 l)
{
    float nl = dot(n, l);
    if (nl <= 0.0) return 0.0;
    if (kind == 1) return ggx_pdf(n, v, l, max(roughness * roughness, 0.0009));
    return nl / PI;
}

// ===== 色调曲线与呈现 =====
vec3 tonemap(vec3 color)
{
    color *= pow(2.0, U[P_EXPOSURE]);
    int mode = int(U[P_TONE_MODE]);
    if (mode == 1)
    {
        color = color / (color + vec3(1.0));
    }
    else if (mode == 2)
    {
        color = clamp((color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14), 0.0, 1.0);
    }
    else if (mode == 3)
    {
        color = max(vec3(0.0), (color * (color + 0.0245786) - 0.000090537) /
            (color * (0.983729 * color + 0.4329510) + 0.238081));
    }
    else
    {
        color = clamp(color, 0.0, 1.0);
    }
    return clamp(color, 0.0, 1.0);
}

vec3 present(vec3 linearColor, vec2 pixel)
{
    linearColor *= vec3(U[P_WB_R], U[P_WB_G], U[P_WB_B]);
    vec3 mapped = tonemap(linearColor);
    vec2 uv = pixel / max(vec2(U[P_VIEW_W], U[P_VIEW_H]), vec2(1.0));
    vec2 q = (uv - 0.5) * U[P_VIGNETTE];
    mapped *= clamp(1.0 - dot(q, q) * 2.4, 0.0, 1.0);
    vec3 srgb = pow(clamp(mapped, 0.0, 1.0), vec3(1.0 / max(U[P_GAMMA], 1.0)));
    if (U[P_DITHER] > 0.5)
    {
        seed_rng(uint(pixel.x) * 1973U + uint(pixel.y) * 9277U + uint(U[P_PIXEL_SEED]) * 26699U);
        srgb += (rnd() - 0.5) * (1.6 / 255.0);
    }
    return clamp(srgb, 0.0, 1.0);
}

uint frame_salt() { return uint(max(U[P_FRAME], 0.0)) * 0xC2B2AE35U; }
""";

    /// <summary>
    /// 场景视图射线投射: MagicaVoxel 风格的直接光、AO、柔和太阳阴影、雾与地面
    /// </summary>
    internal const string SceneFragment = """
#version 450
layout(location = 0) out vec4 FragColor;

float sun_visibility(vec3 point, vec3 normal)
{
    if (U[P_SUN_ENABLED] < 0.5 || U[P_SUN_INTENSITY] <= 0.0) return 1.0;
    vec3 sun = sun_direction();
    float ndl = dot(normal, sun);
    if (ndl <= 0.0) return 0.0;
    int samples = clamp(int(U[P_SHADOW_SAMPLES]), 1, 8);
    float softness = U[P_SHADOW_SOFTNESS];
    vec3 tangent = normalize(cross(sun, abs(sun.y) < 0.9 ? vec3(0, 1, 0) : vec3(1, 0, 0)));
    vec3 bitangent = cross(sun, tangent);
    float lit = 0.0;
    for (int i = 0; i < samples; i++)
    {
        vec3 direction = sun;
        if (softness > 0.0 && samples > 1)
        {
            vec2 disc = rnd2_disk() * softness * 3.0;
            direction = normalize(sun + tangent * disc.x + bitangent * disc.y);
        }
        lit += shadow_ray(point, direction, 512.0) ? 1.0 : 0.0;
    }
    return lit / float(samples);
}

vec3 shade_voxel(VHit hit)
{
    vec3 base = palette_shade(srgb_to_linear(hit.color.rgb));
    float ao = corner_ao(hit.region, hit.normal, hit.point);
    vec3 sun = sun_direction();
    float ndl = max(dot(hit.normal, sun), 0.0);
    float lit = sun_visibility(hit.point + hit.normal * 0.012, hit.normal);
    vec3 color = base * (sky_ambient(hit.normal) + sun_color() * ndl * lit);
    bool emissive = (hit.flags & 2U) != 0U;
    if (emissive) color += base * min(U[P_EMISSIVE_INTENSITY], 2.5);
    color *= emissive ? 1.0 : ao;

    if (U[P_OUTLINES] > 0.5)
    {
        int axis = abs(hit.normal.x) > 0.5 ? 0 : (abs(hit.normal.y) > 0.5 ? 1 : 2);
        vec3 p = hit.point + vec3(0.5);
        float e1, e2;
        if (axis == 0) { e1 = fract(p.y); e2 = fract(p.z); }
        else if (axis == 1) { e1 = fract(p.x); e2 = fract(p.z); }
        else { e1 = fract(p.x); e2 = fract(p.y); }
        e1 = min(e1, 1.0 - e1);
        e2 = min(e2, 1.0 - e2);
        float edge = min(e1, e2);
        float w = clamp(0.030 * max(1.0, hit.t * 0.05), 0.012, 0.075);
        color *= mix(0.32, 1.0, smoothstep(0.0, w, edge));
    }
    return color;
}

vec3 apply_fog(vec3 color, float travelled, vec3 rayDirection)
{
    if (U[P_NEAR_FADE] > 0.5 && travelled < U[P_NEAR_FADE])
    {
        float t = travelled / U[P_NEAR_FADE];
        color = mix(color * 0.25 + sky_color(rayDirection) * 0.35, color, t);
    }
    float fog = 1.0 - exp(-travelled * travelled * U[P_FOG_DENSITY] * U[P_FOG_DENSITY]);
    return mix(color, srgb_to_linear(vec3(U[P_FOG_R], U[P_FOG_G], U[P_FOG_B])), clamp(fog, 0.0, 1.0));
}

void main()
{
    ivec2 pixel = screen_pixel();
    vec3 rayOrigin;
    seed_rng(uint(pixel.x) * 0x9E3779B9U ^ uint(pixel.y) * 0x85EBCA6Bu ^ frame_salt());
    vec3 direction = camera_ray_direction(vec2(pixel), rayOrigin);

    VHit hit = voxel_ray(rayOrigin, direction, 4096.0);
    float groundT;
    bool hasGround = ground_hit(rayOrigin, direction, groundT);
    vec3 linearColor;

    if (hit.hit && (!hasGround || hit.t <= groundT))
    {
        // 最多两层半透明叠加在其后方内容之上, 不做完整递归,
        // 以此近似 MagicaVoxel 的透明层效果
        vec3 accumulated = vec3(0.0);
        float transmittance = 1.0;
        vec3 cursor = rayOrigin;
        ivec3 skip = ivec3(-999999);
        for (int layer = 0; layer < 3; layer++)
        {
            VHit layerHit = voxel_ray(cursor, direction, 4096.0, skip);
            float layerGroundT;
            bool layerHasGround = ground_hit(cursor, direction, layerGroundT);
            if (!layerHit.hit || (layerHasGround && layerGroundT <= layerHit.t + 1e-4))
            {
                vec3 behind = layerHasGround
                    ? ground_radiance(cursor + direction * layerGroundT, direction)
                    : sky_color(direction);
                accumulated += transmittance * behind;
                break;
            }
            accumulated += transmittance * shade_voxel(layerHit);
            if ((layerHit.flags & 1U) == 0U || transmittance * layerHit.color.a < 0.03) break;
            transmittance *= mix(0.35, 0.9, layerHit.color.a);
            cursor = layerHit.point + direction * 0.02;
            skip = layerHit.region;
        }
        linearColor = apply_fog(accumulated, hit.t, direction);
    }
    else if (hasGround)
    {
        linearColor = apply_fog(ground_radiance(rayOrigin + direction * groundT, direction), groundT, direction);
    }
    else
    {
        linearColor = apply_fog(sky_color(direction), 260.0, direction);
    }

    FragColor = vec4(present(linearColor, vec2(pixel)), 1.0);
}
""";

    /// <summary>
    /// 渐进式路径追踪: GGX、余弦采样、太阳/天空/自发光的 MIS、俄罗斯轮盘
    /// 终止与辐亮度钳制; 读取上一帧累积纹理并写出运行平均(ping-pong 渲染目标)
    /// </summary>
    internal const string TraceFragment = """
#version 450
layout(binding = 7) uniform mediump sampler2D uPrevAccum;
layout(location = 0) out vec4 FragColor;

uint light_count() { return max(1U, uint(max(U[P_LIGHT_COUNT], 1.0))); }

// 对随机选中的一个自发光体素面做 next-event 估计
vec3 emissive_nee(vec3 point, vec3 normal, vec3 viewDir, int kind, float roughness, vec3 f0, vec3 base)
{
    uint count = light_count();
    uint index = uint(rnd() * float(count)) % count;
    vec4 head = MVLight[index * 2U];
    vec4 tail = MVLight[index * 2U + 1U];
    vec3 center = head.xyz;
    vec3 toPoint = point - center;
    float m = max(abs(toPoint.x), max(abs(toPoint.y), abs(toPoint.z)));
    if (m < 0.9) return vec3(0.0); // 着色点基本位于灯光体素内部
    vec3 faceNormal = abs(toPoint.x) >= m - 1e-5 ? vec3(sign(toPoint.x), 0, 0)
        : (abs(toPoint.y) >= m - 1e-5 ? vec3(0, sign(toPoint.y), 0) : vec3(0, 0, sign(toPoint.z)));
    if (solid_at(ivec3(floor(center)) - region_min() + ivec3(faceNormal)))
        faceNormal = -faceNormal; // 让光从暴露面发射
    vec3 t1 = abs(faceNormal.x) < 0.9 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    t1 = normalize(t1 - faceNormal * dot(t1, faceNormal));
    vec3 t2 = cross(faceNormal, t1);
    vec2 jitter = vec2(rnd(), rnd()) - 0.5;
    vec3 lightPoint = center + faceNormal * 0.5 + t1 * jitter.x + t2 * jitter.y;
    vec3 direction = lightPoint - point;
    float distanceSquared = max(dot(direction, direction), 1e-8);
    direction /= sqrt(distanceSquared);
    float cosPoint = dot(normal, direction);
    float cosLight = dot(-faceNormal, direction);
    if (cosPoint <= 0.0 || cosLight <= 0.0) return vec3(0.0);
    if (!shadow_ray(point + normal * 0.02, direction, sqrt(distanceSquared) - 0.06)) return vec3(0.0);

    float pdfLightArea = 1.0 / float(count); // 面为单位面积
    float pdfLightSolid = pdfLightArea * distanceSquared / cosLight;
    float pdfBsdf = bsdf_pdf(kind, roughness, normal, viewDir, direction);
    float weight = pdfLightSolid / (pdfLightSolid + max(pdfBsdf, 1e-9));
    float geometry = cosPoint * cosLight / distanceSquared;
    vec3 emission = tail.rgb * head.w * U[P_EMISSIVE_INTENSITY];
    return bsdf_eval(kind, roughness, f0, base, normal, viewDir, direction) * emission * weight * geometry;
}

// 太阳的 next-event 估计: 方向光是一个 Dirac delta, BSDF 采样永远命中不了,
// 因此无偏的灯光采样估计器保持全部权重(MIS 降权只对面积光成立)
vec3 sun_nee(vec3 point, vec3 normal, vec3 viewDir, int kind, float roughness, vec3 f0, vec3 base)
{
    if (U[P_SUN_INTENSITY] <= 0.0 || kind != 0) return vec3(0.0);
    vec3 sun = sun_direction();
    float cosSun = dot(normal, sun);
    if (cosSun <= 0.0) return vec3(0.0);
    if (!shadow_ray(point, sun, 512.0)) return vec3(0.0);
    return bsdf_eval(kind, roughness, f0, base, normal, viewDir, sun) * sun_color() * cosSun;
}

void main()
{
    ivec2 pixel = screen_pixel();
    int samplesThisFrame = clamp(int(U[P_SAMPLES_PER_FRAME]), 1, 8);
    vec3 rayOriginBase;
    vec3 baseDirection = camera_ray_direction(vec2(pixel), rayOriginBase);
    vec3 radianceSum = vec3(0.0);

    for (int sampleIndex = 0; sampleIndex < samplesThisFrame; sampleIndex++)
    {
        seed_rng(uint(pixel.x) * 0x2545F491U ^ uint(pixel.y) * 0x9E3779B9U ^ frame_salt() + uint(sampleIndex) * 0xC2B2AE35U);
        vec3 origin = rayOriginBase;
        vec3 direction = baseDirection;

        if (U[P_APERTURE] > 0.0)
        {
            // 围绕焦平面的薄透镜景深
            vec3 forward = vec3(U[P_FWD_X], U[P_FWD_Y], U[P_FWD_Z]);
            float focusT = U[P_FOCUS_DISTANCE] / max(dot(direction, forward), 1e-5);
            vec3 focusPoint = origin + direction * focusT;
            float radius = U[P_APERTURE] * sqrt(rnd());
            float angle = rnd() * 2.0 * PI;
            vec3 right = vec3(U[P_RIGHT_X], U[P_RIGHT_Y], U[P_RIGHT_Z]);
            vec3 up = vec3(U[P_UP_X], U[P_UP_Y], U[P_UP_Z]);
            origin += right * (cos(angle) * radius) + up * (sin(angle) * radius);
            direction = normalize(focusPoint - origin);
        }

        vec3 throughput = vec3(1.0);
        float roughness = U[P_ROUGHNESS];
        ivec3 skipCell = ivec3(-999999);

        for (int bounce = 0; bounce < int(max(U[P_MAX_BOUNCES], 1.0)); bounce++)
        {
            VHit hit = voxel_ray(origin, direction, 4096.0, skipCell);
            skipCell = ivec3(-999999);
            float groundT;
            bool hasGround = ground_hit(origin, direction, groundT);

            if (!hit.hit || (hasGround && groundT < hit.t))
            {
                radianceSum += throughput * (hasGround
                    ? ground_radiance(origin + direction * groundT, direction)
                    : sky_color(direction));
                break;
            }

            vec3 normal = hit.normal;
            vec3 point = hit.point;
            bool translucent = (hit.flags & 1U) != 0U;
            bool emissive = (hit.flags & 2U) != 0U;
            vec3 base = palette_shade(srgb_to_linear(hit.color.rgb));

            if (emissive) radianceSum += throughput * base * U[P_EMISSIVE_INTENSITY];
            if (emissive && !translucent) break; // 不透明发光体终止传输

            int kind = rnd() < U[P_METALNESS] ? 1 : 0;
            vec3 f0 = kind == 1 ? base : vec3(U[P_SPECULAR_F0]);
            vec3 viewDir = -direction;
            vec3 scatter = vec3(0.0);

            if (translucent)
            {
                // 电介质玻璃: IOR 1.5, Schlick 分光, Beer-Lambert 吸收
                vec3 incident = direction;
                float cosI = dot(incident, normal);
                vec3 outwardNormal;
                float eta;
                if (cosI > 0.0) { outwardNormal = -normal; eta = 1.5; }
                else { outwardNormal = normal; eta = 1.0 / 1.5; }
                vec3 f = fresnel_schlick(abs(cosI), vec3(0.04));
                vec3 refracted = refract(incident, outwardNormal, eta);
                bool tir = dot(refracted, refracted) < 1e-8;
                if (tir || rnd() < max(max(f.r, f.g), f.b))
                {
                    scatter = reflect(incident, outwardNormal);
                }
                else
                {
                    throughput *= exp(-(vec3(1.0) - base) * U[P_ABSORPTION] * 1.7);
                    scatter = refracted;
                    skipCell = hit.region; // 退出刚进入的玻璃单元
                }
                origin = point + scatter * 0.02;
                direction = normalize(scatter);
                if (max(max(throughput.r, throughput.g), throughput.b) < 1e-4) break;
                continue;
            }

            if (U[P_MIS] > 0.5)
            {
                vec3 shadingPoint = point + normal * 0.02;
                radianceSum += throughput * sun_nee(shadingPoint, normal, viewDir, kind, roughness, f0, base);
                radianceSum += throughput * emissive_nee(shadingPoint, normal, viewDir, kind, roughness, f0, base);
            }

            if (kind == 1)
            {
                float alpha = max(roughness * roughness, 0.0009);
                scatter = ggx_sample(normal, viewDir, alpha);
                if (dot(normal, scatter) <= 0.0) break;
                vec3 h = normalize(scatter + viewDir);
                throughput *= fresnel_schlick(max(dot(viewDir, h), 0.0), f0);
            }
            else
            {
                scatter = sample_cosine(normal);
                // 沿 BSDF 射线取环境光, 与均匀天空采样做 MIS
                float pdfSky = 1.0 / (2.0 * PI);
                float pdfBsdf = max(dot(normal, scatter), 0.0) / PI;
                float weight = pdfBsdf / (pdfBsdf + pdfSky);
                vec3 environment;
                float envT;
                VHit envHit = voxel_ray(point + normal * 0.02, scatter, 512.0);
                bool envGround = ground_hit(point + normal * 0.02, scatter, envT);
                bool environmentVisible = !envHit.hit || (envGround && envT < envHit.t);
                if (environmentVisible)
                {
                    environment = envGround
                        ? ground_radiance(point + normal * 0.02 + scatter * envT, scatter)
                        : sky_color(scatter);
                    radianceSum += throughput * base * environment * weight;
                    break;
                }
                else
                {
                    environment = vec3(0.0);
                }
                throughput *= base;
            }

            origin = point + normal * 0.02;
            direction = normalize(scatter);

            if (bounce >= 2)
            {
                // 传输权重跌破约 1/255 后启用俄罗斯轮盘
                float p = min(max(max(throughput.r, throughput.g), throughput.b), 0.95);
                if (p < U[P_ROULETTE_THRESHOLD]) break;
                if (rnd() > p) break;
                throughput /= p;
            }
        }
    }

    float brightest = max(max(radianceSum.r, radianceSum.g), radianceSum.b);
    if (brightest > U[P_RADIANCE_CLAMP]) radianceSum *= U[P_RADIANCE_CLAMP] / brightest;

    float previous = max(U[P_ACCUMULATED], 0.0);
    vec3 old = previous > 0.0
        ? texture(uPrevAccum, gl_FragCoord.xy / vec2(textureSize(uPrevAccum, 0))).rgb
        : vec3(0.0);
    float newCount = previous + float(samplesThisFrame);
    FragColor = vec4((old * previous + radianceSum) / max(newCount, 1.0), 1.0);
}
""";

    /// <summary>
    /// 呈现通道: 把 HDR 累积映射到 LDR 显示目标
    /// </summary>
    internal const string PostFragment = """
#version 450
layout(binding = 7) uniform sampler2D uAccum;
layout(location = 0) out vec4 FragColor;

vec3 denoise_accumulation(vec2 uv)
{
    vec3 center = texture(uAccum, uv).rgb;
    float strength = clamp(U[P_DENOISE_STRENGTH], 0.0, 1.0);
    if (U[P_DENOISE_ENABLED] < 0.5 || strength <= 0.001) return center;

    vec2 texel = 1.0 / vec2(textureSize(uAccum, 0));
    vec2 offsets[4] = vec2[](
        vec2(1.0, 0.0), vec2(-1.0, 0.0),
        vec2(0.0, 1.0), vec2(0.0, -1.0));
    float centerLuma = dot(center, vec3(0.2126, 0.7152, 0.0722));
    vec3 sum = center;
    float weightSum = 1.0;

    for (int index = 0; index < 4; index++)
    {
        vec3 neighbour = texture(uAccum, clamp(uv + offsets[index] * texel, vec2(0.0), vec2(1.0))).rgb;
        float neighbourLuma = dot(neighbour, vec3(0.2126, 0.7152, 0.0722));
        float contrast = abs(neighbourLuma - centerLuma) / (0.08 + max(centerLuma, neighbourLuma));
        float weight = 0.25 * exp(-contrast * 8.0);
        sum += neighbour * weight;
        weightSum += weight;
    }

    return mix(center, sum / weightSum, strength);
}

void main()
{
    ivec2 pixel = screen_pixel();
    // 累积目标与本通道一样是自下而上渲染的, 因此用未翻转的坐标采样,
    // 这样无论分辨率缩放比例如何, 像素都能对齐
    vec2 uv = gl_FragCoord.xy / vec2(textureSize(uAccum, 0));
    vec3 hdr = denoise_accumulation(uv);
    FragColor = vec4(present(hdr, vec2(pixel)), 1.0);
}
""";

    /// <summary>
    /// 用生成的参数表展开共享内核源码
    /// </summary>
    /// <returns>替换过 defines 槽位的共享内核 GLSL</returns>
    internal static string BuildCommon() =>
        Common.Replace("#include \"defines\"", VoxelRenderParameters.Defines().TrimEnd('\n'));

    /// <summary>
    /// 组装完整片元着色器: 版本声明加共享内核加通道源码
    /// </summary>
    /// <param name="fragment">待拼接的通道片元源码</param>
    /// <returns>可直接编译的完整 GLSL 源码</returns>
    internal static string Compose(string fragment) =>
        "#version 450\n" + BuildCommon() + "\n" + fragment.Replace("#version 450\n", string.Empty);
}
