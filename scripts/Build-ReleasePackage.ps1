<#
.SYNOPSIS
    在 GitHub Actions 上组装 Responsible 的 Windows x64 发行包。

.DESCRIPTION
    只做「构建 + 组装」，不构建原生组件：RvmEngine / RTray 由本机 MinGW 产出后
    随仓库提供（natives\win-x64\），CI 只负责复制。

    顺序：前端 -> 内核（pnpm）-> WPF publish -> 组装 -> 剪平台 -> 体积锁 -> zip。

.PARAMETER Version
    发行版本号，默认 0.0.1-beta。

.PARAMETER Root
    仓库根目录，默认脚本上一级。

.PARAMETER SkipKernelBuild
    跳过内核安装与构建，直接使用工作区里已有的产物（仅用于本地复现 CI 组装环节）。
#>
[CmdletBinding()]
param(
    [string]$Version = '0.0.1-beta',
    [string]$Root,
    [switch]$SkipKernelBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = if ($Root) { (Resolve-Path -LiteralPath $Root).Path } else { (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path }
$dist = Join-Path $Root 'dist'
$packageName = "Responsible-$Version-win-x64"
$out = Join-Path $dist $packageName
$coreSrc = Join-Path $Root 'RDshCore'
$natives = Join-Path $Root 'natives\win-x64'
$limit = 2GB

$script:clock = [System.Diagnostics.Stopwatch]::StartNew()

function Say([string]$message) {
    Write-Host ("[{0,6:N1}m] {1}" -f $script:clock.Elapsed.TotalMinutes, $message) -ForegroundColor Cyan
}
function Die([string]$message) { throw "BUILD-FAILED: $message" }
function Assert-True([bool]$condition, [string]$message) { if (-not $condition) { Die $message } }

# 运行一条外部命令并在非零退出时立即失败（不吞错、不猜）。
function Run([string]$what, [scriptblock]$command) {
    Say $what
    & $command
    if ($LASTEXITCODE -ne 0) { Die "$what（exit $LASTEXITCODE）" }
}

# 目录体积。CI 上目录是可扫描的真实树（没有符号链接环），这里可以安全递归。
function Get-TreeBytes([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return [int64]0 }
    $sum = Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum
    if ($null -eq $sum -or $null -eq $sum.Sum) { return [int64]0 }
    return [int64]($sum.Sum)
}

function Assert-UnderLimit([string]$stage) {
    $bytes = Get-TreeBytes $out
    Say ("      {0}: {1:N0} MB" -f $stage, ($bytes / 1MB))
    if ($bytes -le $limit) { return }
    Get-ChildItem -LiteralPath $out -Force -ErrorAction SilentlyContinue | ForEach-Object {
        $childBytes = if ($_.PSIsContainer) { Get-TreeBytes $_.FullName } else { [int64]$_.Length }
        Write-Host ("        {0,-28} {1,8:N0} MB" -f $_.Name, ($childBytes / 1MB))
    }
    Die "发行包超过 2GB 硬上限（阶段：$stage，实际 $([math]::Round($bytes / 1MB)) MB）"
}

# rd 不跟随 reparse point，用来删除 pnpm 的 junction 树。
function Remove-Tree([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    & cmd /c rd /s /q "$path" 2>$null
    if (Test-Path -LiteralPath $path) { Die "无法删除 $path" }
}

# 删掉指向已移除 .pnpm 目录的悬挂 junction（Test-Path 对悬挂链接返回 false）。
function Clear-DanglingReparse([string]$directory) {
    Get-ChildItem -LiteralPath $directory -Force -ErrorAction SilentlyContinue | Where-Object {
        ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -and -not (Test-Path -LiteralPath $_.FullName)
    } | ForEach-Object { & cmd /c rd "$($_.FullName)" 2>$null }
}

Say "仓库：$Root"
Say "产物：$out"

# ------------------------------------------------------------------ 0. 前置
Say '校验输入'
foreach ($required in @(
        (Join-Path $coreSrc 'pnpm-workspace.yaml'),
        (Join-Path $Root 'Responsible\Responsible.csproj'),
        (Join-Path $Root 'RWebView\build.mjs'),
        (Join-Path $natives 'RvmEngine.exe'),
        (Join-Path $natives 'RTray.exe')
    )) {
    Assert-True (Test-Path -LiteralPath $required) "缺少 $required"
}

# ------------------------------------------------------------------ 1. 前端
Push-Location (Join-Path $Root 'RWebView')
try {
    Run '前端：npm ci' { npm ci --no-audit --no-fund }
    Run '前端：node build.mjs' { node build.mjs }
    Assert-True (Test-Path -LiteralPath 'dist\index.html') '前端没有产出 dist/index.html'
}
finally { Pop-Location }

# ------------------------------------------------------------------ 2. 内核
if (-not $SkipKernelBuild) {
    Push-Location $coreSrc
    try {
        # 构建需要 devDependencies（tsc / tsdown / tsx），所以先做完整安装。
        Run '内核：pnpm install（含 dev）' { pnpm install --frozen-lockfile }
        Run '内核：pnpm run build' { pnpm run build }
        Assert-True (Test-Path -LiteralPath 'apps\cli\lib\bin.js') '内核没有产出 apps/cli/lib/bin.js'

        # 发行只需 CLI 的生产依赖闭包；重新安装会剪掉 devDependencies 与其它 workspace。
        Run '内核：重装为 CLI 生产闭包' {
            pnpm install --prod --frozen-lockfile --ignore-scripts `
                --filter '@deepseek-ai/dsh...' --os=win32 --cpu=x64
        }
        Assert-True (Test-Path -LiteralPath 'apps\cli\lib\bin.js') '重装后 apps/cli/lib/bin.js 消失了'
    }
    finally { Pop-Location }
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null

# ------------------------------------------------------------------ 3. 内核归档
# tar 认硬链接：pnpm 的去重内容在归档里只存一份，避免展开成数 GB。
$tarPath = Join-Path $dist 'rdsh-core.tar'
if (Test-Path -LiteralPath $tarPath) { Remove-Item -LiteralPath $tarPath -Force }
Run '内核：tar 归档' {
    tar -cf $tarPath -C $coreSrc `
        --exclude=./src --exclude=./tests --exclude=./website --exclude=./benchmarks `
        --exclude=./.git --exclude=./.turbo --exclude=./.github --exclude=./.agents `
        --exclude=./python --exclude=./scripts --exclude=./test --exclude=./__tests__ .
}
$tarMb = [math]::Round((Get-Item -LiteralPath $tarPath).Length / 1MB)
Say "      内核归档：$tarMb MB"

# ------------------------------------------------------------------ 4. WPF
Run '宿主：dotnet publish' {
    dotnet publish (Join-Path $Root 'Responsible\Responsible.csproj') `
        -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=false -p:DebugType=none `
        -p:Version=$Version -p:AssemblyVersion=0.0.1.0 -p:FileVersion=0.0.1.0 `
        -o $out
}
Assert-True (Test-Path -LiteralPath (Join-Path $out 'Responsible.exe')) 'dotnet publish 没有产出 Responsible.exe'

# ------------------------------------------------------------------ 5. 随包组件
Say '复制 node / 插件 / 原生'
$nodeExe = (Get-Command node -ErrorAction SilentlyContinue).Source
Assert-True ([bool]$nodeExe) '找不到 node.exe'
New-Item -ItemType Directory -Path (Join-Path $out 'node') -Force | Out-Null
Copy-Item -LiteralPath $nodeExe -Destination (Join-Path $out 'node\node.exe') -Force

Copy-Item -LiteralPath (Join-Path $Root 'RDshPlugins') -Destination (Join-Path $out 'RDshPlugins') -Recurse -Force
Get-ChildItem (Join-Path $out 'RDshPlugins') -Recurse -File -Include *.md -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

foreach ($file in @('RvmEngine.exe', 'RTray.exe', 'libgcc_s_seh-1.dll', 'libstdc++-6.dll', 'libwinpthread-1.dll')) {
    $source = Join-Path $natives $file
    Assert-True (Test-Path -LiteralPath $source) "缺少原生组件 $file"
    Copy-Item -LiteralPath $source -Destination $out -Force
}

Assert-True (Test-Path -LiteralPath (Join-Path $out 'RWebView\index.html')) '宿主输出里没有 RWebView\index.html'

# ------------------------------------------------------------------ 6. 内核解包
$coreDst = Join-Path $out 'RDshCore'
New-Item -ItemType Directory -Path $coreDst -Force | Out-Null
Run '内核：解包到发行目录' { tar -xf $tarPath -C $coreDst }
Remove-Item -LiteralPath $tarPath -Force
Assert-True (Test-Path -LiteralPath (Join-Path $coreDst 'apps\cli\lib\bin.js')) '解包后缺少内核入口'
Assert-UnderLimit '解包完成'

# ------------------------------------------------------------------ 7. 剪掉非 Windows 平台包
Say '裁剪非 win32 平台的原生包'
$platformPattern = '-(darwin|linux|freebsd|android|openbsd|netbsd|sunos|aix|arm64|armv7|ia32|ppc64|s390x|loong64|riscv64)-'
$nm = Join-Path $coreDst 'node_modules'
foreach ($base in @($nm, (Join-Path $nm '.pnpm'))) {
    if (-not (Test-Path -LiteralPath $base)) { continue }
    Get-ChildItem -LiteralPath $base -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $platformPattern } |
        ForEach-Object { Remove-Tree $_.FullName }
}
# 被剪掉的包留下的悬挂 junction 一并清掉，否则打包会失败。
foreach ($scopeDir in (Get-ChildItem -LiteralPath $nm -Directory -Force -ErrorAction SilentlyContinue)) {
    Clear-DanglingReparse $scopeDir.FullName
    if ($scopeDir.Name.StartsWith('@')) {
        Clear-DanglingReparse (Join-Path $nm $scopeDir.Name)
        $left = Get-ChildItem -LiteralPath $scopeDir.FullName -Force -ErrorAction SilentlyContinue
        if (-not $left) { & cmd /c rd "$($scopeDir.FullName)" 2>$null }
    }
}
Clear-DanglingReparse $nm
Clear-DanglingReparse (Join-Path $nm '.pnpm')
Assert-UnderLimit '平台裁剪完成'

# ------------------------------------------------------------------ 8. 元数据
Say '写版本信息'
@(
    "Responsible $Version",
    "built: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') (UTC$(if ([TimeZoneInfo]::Local.BaseUtcOffset.TotalHours -ge 0) { '+' })$([TimeZoneInfo]::Local.BaseUtcOffset.TotalHours))",
    "commit: $($env:GITHUB_SHA)",
    "runtime: .NET self-contained + bundled node $(node --version)",
    "kernel: DSH CLI production closure (no GUI client, no website)",
    "native: RVM/RTray built with MinGW-w64 (Release)"
) -join [Environment]::NewLine | Set-Content -LiteralPath (Join-Path $out 'version.txt') -Encoding utf8

foreach ($doc in @('README.md', 'BUILD-GUIDE.md')) {
    $source = Join-Path $Root $doc
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $out -Force }
}

Assert-UnderLimit '组装完成'

# ------------------------------------------------------------------ 9. 打包
Say '生成 zip'
$zip = Join-Path $dist "$packageName.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
# bsdtar 写 zip：符号链接按链接保存，不会把内容展开成多份。
Run 'zip' { tar -a -cf $zip -C $dist $packageName }

$zipMb = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB)
$outMb = [math]::Round((Get-TreeBytes $out) / 1MB)
Say ("完成：目录 {0:N0} MB / zip {1:N0} MB（上限 {2:N0} MB），用时 {3:N1} 分钟" -f
    $outMb, $zipMb, ($limit / 1MB), $script:clock.Elapsed.TotalMinutes)
