$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'MVoxel\MVoxel.csproj'
$stage = Join-Path ([IO.Path]::GetTempPath()) ('MVoxel-portable-' + [guid]::NewGuid().ToString('N'))
$package = Join-Path $stage 'MVoxel'
$archive = Join-Path $repo ('MVoxel-portable-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip')
$archiveTemporary = Join-Path $stage 'MVoxel-portable.zip'
if (Test-Path -LiteralPath $archive) { throw "Package already exists: $archive" }
try {
    # 自包含发布：包内自带 .NET 运行时，用户解压后不用另装 .NET
    & dotnet publish $project --configuration Release --runtime win-x64 --self-contained true `
        -p:NuGetAudit=false -p:NoWarn=NU1900 `
        -p:DebugType=None -p:DebugSymbols=false --output $package
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    $exe = Join-Path $package 'MVoxel.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Self-contained publish output missing MVoxel.exe: $package"
    }
    foreach ($native in @('cimgui.dll', 'glfw3.dll', 'shaderc_shared.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $package $native) -PathType Leaf)) {
            throw "Self-contained publish output missing native dependency: $native"
        }
    }
    # 自包含发布会把原生库平铺到根目录，.NET 宿主按 deps.json 能直接加载；
    # 但 Silk.NET 的加载器按 runtimes/<rid>/native 探测，这里再放一份兼容副本
    $nativeDir = Join-Path $package 'runtimes\win-x64\native'
    New-Item -ItemType Directory -Path $nativeDir -Force | Out-Null
    foreach ($native in @('cimgui.dll', 'glfw3.dll', 'shaderc_shared.dll')) {
        Copy-Item -LiteralPath (Join-Path $package $native) -Destination (Join-Path $nativeDir $native)
    }
    foreach ($content in @('Editor\Fonts', 'Editor\Guide\Docs', 'Editor\Icons', 'Editor\Brand', 'Plugins')) {
        if (-not (Test-Path -LiteralPath (Join-Path $package $content) -PathType Container)) {
            throw "Self-contained publish output missing content directory: $content"
        }
    }
    $license = Join-Path $package 'Editor\Rendering\LICENSE.Bytesized.txt'
    if (-not (Test-Path -LiteralPath $license -PathType Leaf)) {
        throw "Self-contained publish output missing rendering license: $license"
    }
    Get-ChildItem -LiteralPath $package -Recurse -File -Filter '*.pdb' | Remove-Item -Force
    $readme = @"
MVoxel Windows x64 绿色版

请先解压整个文件夹，再双击 MVoxel.exe；请保留文件夹内的全部文件，不要只单独拖出 exe。
程序已自带 .NET 运行时，不需要安装 .NET。
运行需要支持 Vulkan 的显卡和驱动，驱动过旧时请先更新显卡驱动。
首次运行默认把配置、缓存、日志和恢复数据写入用户的 LocalAppData；需要便携运行时，请使用 MVoxel.exe --portable 或在程序目录放置空文件 MVoxel.portable。旧版本程序目录中的 MVoxelData 会在首次默认启动时复制到新的用户数据目录，原目录保留不删除。
"@
    [IO.File]::WriteAllText((Join-Path $package '使用说明.txt'), ($readme -replace "`n", "`r`n"),
        [Text.UTF8Encoding]::new($false))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($package, $archiveTemporary, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $moveCommand = 'move /Y "' + $archiveTemporary + '" "' + $archive + '"'
    & cmd.exe /d /c $moveCommand | Out-Null
    Write-Host "Package created: $archive"
} catch {
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    throw
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
