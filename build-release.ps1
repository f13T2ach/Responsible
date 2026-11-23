#Requires -Version 5.1
<#
.SYNOPSIS
    本机只构建 RVM 引擎与 RTray，产物落到 natives\win-x64\，随仓库提交。

.DESCRIPTION
    发行包本身不在这里构建：内核安装、WPF 发布、组装与打包全部在 GitHub Actions
    上做（.github/workflows/build-release.yml -> scripts/Build-ReleasePackage.ps1）。
    本机跑完整流程会把磁盘吃穿，原因见 BUILD-GUIDE.md 的陷阱清单。

    需要 MinGW-w64（g++ 支持 C++20）与 CMake >= 4.3、Ninja。

.PARAMETER Configuration
    构建类型，默认 Release。

.PARAMETER Force
    强制重新配置（删除 build-release 缓存）。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '.')).Path
$natives = Join-Path $root 'natives\win-x64'

foreach ($tool in @('cmake', 'ninja', 'gcc', 'g++')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "缺少 $tool；先把 MinGW-w64 与 CMake/Ninja 放进 PATH"
    }
}

function Invoke-Build([string]$name, [string]$sourceDir, [string[]]$extraArgs) {
    $buildDir = Join-Path $sourceDir "build-release"
    if ($Force -and (Test-Path -LiteralPath $buildDir)) {
        & cmd /c rd /s /q "$buildDir" 2>$null
    }
    Write-Host "==> 配置 $name" -ForegroundColor Cyan
    $cmakeArgs = @('-S', $sourceDir, '-B', $buildDir, '-G', 'Ninja', "-DCMAKE_BUILD_TYPE=$Configuration") + $extraArgs
    & cmake @cmakeArgs
    if ($LASTEXITCODE -ne 0) { throw "$name 配置失败（exit $LASTEXITCODE）" }

    Write-Host "==> 构建 $name" -ForegroundColor Cyan
    & cmake --build $buildDir
    if ($LASTEXITCODE -ne 0) { throw "$name 构建失败（exit $LASTEXITCODE）" }
    return (Join-Path $buildDir 'bin')
}

# 复用已下载的 protobuf 源码，避免每次重新抓 20MB 并解压。
$protobufCache = Join-Path $root 'RVM\build\_deps\protobuf-src'
$rvmArgs = @('-DRVM_BUILD_TESTS=OFF')
if (Test-Path -LiteralPath (Join-Path $protobufCache 'CMakeLists.txt')) {
    $rvmArgs += "-DRVM_PROTOBUF_SOURCE_DIR=$($protobufCache -replace '\\', '/')"
}

$rvmBin = Invoke-Build 'RvmEngine' (Join-Path $root 'RVM') $rvmArgs
$rtrayBin = Invoke-Build 'RTray' (Join-Path $root 'RTray') @()

New-Item -ItemType Directory -Path $natives -Force | Out-Null
foreach ($file in @('RvmEngine.exe', 'RTray.exe', 'libgcc_s_seh-1.dll', 'libstdc++-6.dll', 'libwinpthread-1.dll')) {
    $candidate = @(
        (Join-Path $rvmBin $file),
        (Join-Path $rtrayBin $file)
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $candidate) { throw "构建产物里找不到 $file" }
    Copy-Item -LiteralPath $candidate -Destination $natives -Force
}

Get-ChildItem -LiteralPath $natives | Select-Object Length, Name | Format-Table -AutoSize
$total = [math]::Round(((Get-ChildItem -LiteralPath $natives | Measure-Object Length -Sum).Sum / 1MB), 2)
Write-Host "原生组件已更新：$natives（$total MB）" -ForegroundColor Green
Write-Host "发行包在 GitHub Actions 构建：git push 后看 Actions 的 build-release" -ForegroundColor DarkGray
