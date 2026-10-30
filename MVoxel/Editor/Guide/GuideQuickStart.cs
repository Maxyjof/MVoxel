namespace MVoxel.Editor.Guide;

/// <summary>快速入门：十到十五分钟走完从建项目到出图的主干流程</summary>
internal static class GuideQuickStart
{
    internal const string Title = "快速入门";

    internal const string Summary =
        "下面这条路线覆盖第一次使用最常做的事，十到十五分钟能走完。每节只讲必要的操作，没展开的细节可以去完整教程里查对应章节。";

    internal static readonly GuideLesson[] Lessons =
    [
        new(
            "新建项目",
            "MVoxel 的所有编辑都在项目里进行，所以第一件事是建项目。",
            [
                "启动后停在欢迎界面，点“新建项目”。填写名称和保存位置，再挑一个模板，“小屋与花园”适合第一次练手。",
                "模板会生成初始场景。目标位置已经有内容时会自动带序号新建，不会覆盖。模板控制项里能看到真实几何缩略图和三步练习，缩略图来自模板体素颜色，不是最终光照画面。",
                "建好后主窗口打开，层级里已经有对象树，场景里能看到模型。",
            ],
            "默认保存位置是文档目录下的 MVoxel Projects。打开已有项目时选择包含 Project.mvoxproj 的文件夹。",
            [new GuideAction("新建项目…", GuideActionId.NewProject), new GuideAction("打开项目…", GuideActionId.OpenProject)]),
        new(
            "认识界面",
            "花一分钟记住四块区域的位置，后面找功能会快很多。",
            [
                "左侧“层级”是场景对象树，可选择对象并控制显隐。",
                "中间的“场景”用来快速编辑，“渲染”用来看最终画面。",
                "右侧“检查器”和“调色板”通过标签切换；需要画面参数时可从“视图 → 渲染配置”打开。",
                "底部“项目资产”按分类浏览资产，双击体素数据可以切换当前编辑对象。",
            ],
            "面板被关掉或者挤乱了，用“布局 → 建模布局”恢复。",
            [new GuideAction("应用建模布局", GuideActionId.ModelingLayout), new GuideAction("显示调色板", GuideActionId.ShowModelingTools)]),
        new(
            "观察视角",
            "MVoxel 的左键留给绘制，观察模型靠另外三个动作。",
            [
                "右键拖动环绕，中键拖动平移，滚轮缩放。",
                "场景右上角的相机按钮可以调移动速度、近远裁剪和视场角；点 XYZ 方向控件能切到对应方向观察。",
                "开启大写锁定进入第一人称：WASD 移动、Q/E 升降、鼠标转向、Shift 加速，在大型场景里穿行很方便。",
                "切到相机工具后左键不再编辑，只用来观察和构图。",
            ],
            null,
            [new GuideAction("场景页", GuideActionId.SceneTab)]),
        new(
            "放下第一笔",
            "新建空项目包含示例场景；可通过“项目 → 新建体素模型”建立自己的对象。",
            [
                "在调色板里点一个颜色，把鼠标移到网格或模型表面按左键。",
                "Shift+左键擦除；按住左键移动鼠标可以连续绘制，停住不动不会重复盖章。",
                "画错了按 Ctrl+Z，Ctrl+Y 重做，按住撤销键会连续回退。",
                "如果没有反应，先看对象或图层是不是被隐藏、模型组件是不是被禁用；状态栏会显示操作反馈。",
            ],
            null,
            [new GuideAction("显示调色板", GuideActionId.ShowModelingTools)]),
        new(
            "搭出体积",
            "有了第一笔，按住鼠标连续绘制或擦除，逐步搭出形状。",
            [
                "按住左键并移动鼠标连续绘制；按住 Shift 则连续擦除。",
                "连续绘制按笔刷经过的位置落笔，不会自动填充连通区域。",
                "笔刷形状可以切立方或球，半径 1 到 8，半径 1 是单格。",
                "模型行右侧的锁可以固定编辑目标，锁定后新方块只归这个模型，不会跑到相邻对象里。",
            ],
            "笔刷颜色只影响新画的方块。想给已有方块整体换色，选中模型后用检查器里的“重绘当前模型”。",
            [new GuideAction("项目设置", GuideActionId.ProjectSettings)]),
        new(
            "出图与保存",
            "最后把作品变成文件，这一步在“渲染”页完成。",
            [
                "按 F2 或点“渲染”标签。场景里没有相机对象时，渲染视图跟随场景视角；有相机时可以在顶部下拉选择。",
                "移动渲染视角会写回相机对象，所以调构图就是调相机。",
                "渲染工具栏的导出菜单可以输出当前相机 PNG/HDR 或全部相机 PNG，任务队列会显示路径和进度。",
                "模型和场景用“项目 → 导出”输出原生 .mvmodel / .mvworld，保留颜色、材质槽和标记；完整场景层级用“另存项目”。",
                "最后 Ctrl+S 保存。自动保存只是恢复快照，不能代替正式保存。",
            ],
            null,
            [new GuideAction("渲染页", GuideActionId.RenderTab), new GuideAction("保存项目", GuideActionId.SaveProject)]),
    ];

    internal const string Closing =
        "走完这六步，可以回到完整教程按章节补细节；如果是从 MagicaVoxel 过来，先看迁移指南里的概念对照会更顺。";
}
