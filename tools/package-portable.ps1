$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'MVoxel\bin\Release\net10.0-windows'
$exe = Join-Path $source 'MVoxel.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Release executable not found: $exe. Build Release first."
}
$stage = Join-Path ([IO.Path]::GetTempPath()) ('MVoxel-portable-' + [guid]::NewGuid().ToString('N'))
$package = Join-Path $stage 'MVoxel'
$archive = Join-Path $repo ('MVoxel绿色版-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip')
if (Test-Path -LiteralPath $archive) { throw "Package already exists: $archive" }
try {
    New-Item -ItemType Directory -Path $package | Out-Null
    Get-ChildItem -LiteralPath $source -File | Where-Object {
        $_.Extension -in '.dll', '.exe', '.json'
    } | Copy-Item -Destination $package
    foreach ($dir in @('Editor', 'Plugins')) {
        $path = Join-Path $source $dir
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $package -Recurse }
    }
    $native = Join-Path $source 'runtimes\win-x64'
    if (-not (Test-Path -LiteralPath (Join-Path $native 'native\cimgui.dll'))) {
        throw 'Missing Windows x64 native dependencies in Release output.'
    }
    New-Item -ItemType Directory -Path (Join-Path $package 'runtimes') | Out-Null
    Copy-Item -LiteralPath $native -Destination (Join-Path $package 'runtimes') -Recurse
    $readme = "MVoxel Windows x64 绿色版`r`n`r`n请先解压整个文件夹，再双击 MVoxel.exe。请保留文件夹内的全部文件。`r`n运行需要 .NET 10 x64 运行时及支持 Vulkan 的显卡和驱动。`r`n如果电脑没有安装运行时，请根据启动时的提示安装。`r`n首次运行会在程序目录中创建配置和缓存。`r`n"
    [IO.File]::WriteAllText((Join-Path $package '使用说明.txt'), $readme, [Text.Encoding]::UTF8)
    Compress-Archive -LiteralPath $package -DestinationPath $archive -CompressionLevel Optimal
    Write-Host "Package created: $archive"
} catch {
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    throw
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
