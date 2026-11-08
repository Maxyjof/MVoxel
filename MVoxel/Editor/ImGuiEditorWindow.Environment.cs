using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MVoxel.Scene;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private static readonly JsonSerializerOptions EnvironmentJsonOptions = new(JsonSerializerDefaults.Web) { IncludeFields = true };

    /// <summary>
    /// 打开独立的环境配置窗口并把它提到最前：渲染配置和“窗口”菜单都走这一个入口，
    /// 避免窗口被前面的大面板压住而看起来“点了没反应”。
    /// </summary>
    private void OpenEnvironmentWindow()
    {
        _showEnvironmentWindow = true;
        _focusEnvironmentWindow = true;
    }

    private void DrawEnvironmentWindow()
    {
        if (!_showEnvironmentWindow) return;
        if (_focusEnvironmentWindow)
        {
            ImGui.SetNextWindowFocus();
            _focusEnvironmentWindow = false;
        }
        ImGui.SetNextWindowSize(new Vector2(520F, 620F), ImGuiCond.FirstUseEver);
        var environmentVisible = BeginToolWindow("环境配置##EnvironmentWindow", PanelWindowFlags,
            () => _showEnvironmentWindow = false);
        if (environmentVisible)
        {
            ImGui.TextDisabled("当前场景唯一环境配置");
            ImGui.SameLine();
            if (ImGui.SmallButton("恢复默认"))
            {
                _sceneDocument?.Environment = new SceneEnvironmentSettings();
                _sceneDocument?.Environment.Normalize();
                if (_sceneDocument is not null)
                {
                    _sceneDocument.Environment.ApplyTo(_renderSettings);
                    MarkSceneDocumentDirty();
                    _pipeline?.ResetAccumulation();
                }
            }
            ImGui.Separator();
            if (_sceneDocument is null)
            {
                ImGui.TextDisabled("当前没有打开的场景");
            }
            else
            {
                var environment = _sceneDocument.Environment;
                var before = JsonSerializer.Serialize(environment, EnvironmentJsonOptions);
                DrawSkyAndGround(environment);
                DrawEnvironmentFog(environment);
                DrawEnvironmentWater(environment);
                DrawEnvironmentWeather(environment);
                environment.Normalize();
                environment.ApplyTo(_renderSettings);
                var after = JsonSerializer.Serialize(environment, EnvironmentJsonOptions);
                if (!string.Equals(before, after, StringComparison.Ordinal))
                {
                    MarkSceneDocumentDirty();
                    _pipeline?.ResetAccumulation();
                }
            }
        }
        ImGui.End();
    }

    private static void DrawEnvironmentWeather(SceneEnvironmentSettings environment)
    {
        if (!ImGui.CollapsingHeader("天气", ImGuiTreeNodeFlags.DefaultOpen)) return;
        ImGui.DragInt("天气种子", ref environment.WeatherSeed, 1F, 0, 65535);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("改变雨雪、闪电与热浪的随机分布，同种子同帧结果一致");
        SliderFloat("雨雪可见距离", ref environment.WeatherViewDistance, 1F, 256F, "%.0f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("雨雪在相机周围的最大显示距离，越大绘制开销越高");
        SliderFloat("风强度", ref environment.WeatherWindStrength, 0F, 4F, "%.2f");
        SliderFloat("风向", ref environment.WeatherWindDirection, 0F, 360F, "%.0f°");
        SliderFloat("降雨强度", ref environment.WeatherRainIntensity, 0F, 1F, "%.2f");
        SliderFloat("降雪强度", ref environment.WeatherSnowIntensity, 0F, 1F, "%.2f");
        SliderFloat("雷暴强度", ref environment.WeatherStormIntensity, 0F, 1F, "%.2f");
        SliderFloat("热浪强度", ref environment.WeatherHeatHaze, 0F, 1F, "%.2f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("屏幕空间位移模拟热浪，不是物理折射");
        SliderFloat("闪电频率", ref environment.WeatherLightningFrequency, 0F, 4F, "%.2f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("需要雷暴强度大于零，亮度为画面近似");
    }

    private static void DrawSkyAndGround(SceneEnvironmentSettings environment)
    {
        if (!ImGui.CollapsingHeader("天空与地面", ImGuiTreeNodeFlags.DefaultOpen)) return;
        ImGui.Checkbox("显示地面平面", ref environment.ShowGround);
        DrawColorSlider("背景颜色", ref environment.SceneBackgroundColor);
        DrawColorSlider("地面颜色", ref environment.GroundColor);
        SliderFloat("地面过渡", ref environment.GroundFade, 0F, 4096F, "%.0f 格");
        DrawColorSlider("天顶色", ref environment.SkyTop);
        DrawColorSlider("地平线色", ref environment.SkyHorizon);
        SliderFloat("天空环境光", ref environment.SkyAmbient, 0F, 2F, "%.2f");
        SliderFloat("暖色补光", ref environment.AmbientTint, 0F, 1F, "%.2f");
        SliderFloat("近处淡入距离", ref environment.NearFade, 0F, 512F, "%.0f 格");
    }

    private static void DrawEnvironmentFog(SceneEnvironmentSettings environment)
    {
        if (!ImGui.CollapsingHeader("雾气", ImGuiTreeNodeFlags.DefaultOpen)) return;

        static void Tip(string text) { if (ImGui.IsItemHovered()) ImGui.SetTooltip(text); }

        ImGui.SeparatorText("基础");
        DrawColorSlider("雾颜色", ref environment.FogColor);
        Tip("雾的内散射基色，与天空色按天空混合加权");
        SliderFloat("雾浓度", ref environment.FogDensity, 0F, .05F, "%.4f");
        Tip("消光系数，每格；0 关闭雾");
        SliderFloat("雾起始距离", ref environment.FogStart, 0F, 100000F, "%.0f 格");
        Tip("从相机起算的雾起始距离");
        SliderFloat("天空混合", ref environment.FogSkyBlend, 0F, 1F, "%.2f");
        Tip("0 用天空色作雾色，1 用上方雾颜色");
        SliderFloat("最大不透明度", ref environment.FogMaxOpacity, 0F, 1F, "%.2f");
        Tip("雾的最大遮蔽量");
        SliderFloat("内散射亮度", ref environment.FogInscatterStrength, 0F, 4F, "%.2f");
        Tip("雾被环境光照亮的倍率，不改变消光");

        ImGui.SeparatorText("高度与分布");
        SliderFloat("雾层基准高度", ref environment.FogHeight, -512F, 1024F, "%.1f 格");
        Tip("世界 Y 高度，此处密度等于雾浓度");
        SliderFloat("高度衰减", ref environment.FogHeightFalloff, 0F, .2F, "%.4f");
        Tip("沿高度方向的密度衰减，0 为均匀雾");
        SliderFloat("体积计算距离", ref environment.FogViewDistance, 0F, 8192F, "%.0f 格");
        Tip("0 表示跟随命中距离，非零时限制体积雾积分范围");
        SliderFloat("密度起伏", ref environment.FogNoiseStrength, 0F, 1F, "%.2f");
        Tip("三维噪声对密度的扰动幅度");
        SliderFloat("噪声频率", ref environment.FogNoiseScale, .001F, .5F, "%.3f");
        Tip("噪声空间频率，越小雾团越大");
        SliderFloat("噪声漂移", ref environment.FogNoiseSpeed, 0F, 4F, "%.2f");
        Tip("噪声随风与时间流动的速度，0 为静止");

        ImGui.SeparatorText("散射与光照");
        SliderFloat("各向异性 g", ref environment.FogAnisotropy, -.95F, .95F, "%.2f");
        Tip("相函数，正值越大前向散射光晕越集中");
        SliderFloat("单次散射反照率", ref environment.FogAlbedo, 0F, 1F, "%.2f");
        Tip("1 为纯散射雾，0 为纯吸收雾");
        SliderFloat("太阳内散射", ref environment.FogSunScatterStrength, 0F, 4F, "%.2f");
        Tip("太阳在雾中形成的光晕强度");
        DrawColorSlider("太阳内散射色", ref environment.FogSunScatterColor);
        Tip("叠加在太阳颜色上的色比");
        SliderFloat("天空雾强度", ref environment.FogSkyFogStrength, 0F, 1F, "%.2f");
        Tip("天空与背景融入雾色的比例");

        ImGui.SeparatorText("体积质量");
        SliderInt("体积采样步数（实时）", ref environment.FogVolumetricSteps, 0, 64);
        Tip("0 使用解析高度雾；大于 0 时抖动步进并查询太阳阴影，步数越高开销越大");
    }

    private static void DrawEnvironmentWater(SceneEnvironmentSettings environment)
    {
        if (!ImGui.CollapsingHeader("水面", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (ImGui.Checkbox("显示水面", ref environment.ShowWater) && environment.ShowWater)
            environment.WaterAnimationEnabled = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("无限水平水面，波浪只改表面法线");
        SliderFloat("水面高度", ref environment.WaterHeight, -100000F, 100000F, "%.1f 格");
        DrawColorSlider("水面颜色", ref environment.WaterColor);
        SliderFloat("波浪大小", ref environment.WaterWaveSize, 0F, 4F, "%.2f");
        SliderFloat("波长", ref environment.WaterWaveLength, .1F, 1024F, "%.1f 格");
        SliderFloat("水面粗糙度", ref environment.WaterRoughness, .01F, 1F, "%.2f");
        ImGui.Checkbox("水面动画", ref environment.WaterAnimationEnabled);
        SliderFloat("动画速度", ref environment.WaterAnimationSpeed, -8F, 8F, "%.2f");
        var flow = environment.WaterFlowDirection;
        BeginInspectorField("流向", "水面细节纹理的二维流动方向");
        if (ImGui.DragFloat2("##EnvironmentWaterFlow", ref flow, .01F, -1F, 1F, "%.2f")) environment.WaterFlowDirection = flow;
        SliderFloat("细节强度", ref environment.WaterDetailStrength, 0F, 2F, "%.2f");
        SliderFloat("高光强度", ref environment.WaterSpecularStrength, 0F, 4F, "%.2f");
    }
}
