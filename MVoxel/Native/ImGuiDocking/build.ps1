param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$locator = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio/Installer/vswhere.exe"
$installation = & $locator -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw "Visual Studio C++ build tools required." }
$cmake = Join-Path $installation "Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"
& $cmake -S $PSScriptRoot -B "$PSScriptRoot/obj" -G "Visual Studio 17 2022" -A x64
if ($LASTEXITCODE) { throw "CMake configuration failed." }
& $cmake --build "$PSScriptRoot/obj" --config $Configuration
if ($LASTEXITCODE) { throw "Native build failed." }
