<#
.SYNOPSIS
    在 GitHub Actions 上组装 Responsible 的 Windows x64 发行包。

.DESCRIPTION
    只做「构建 + 组装」，不构建原生组件：RvmEngine / RTray 由本机 MinGW 产出后
    随仓库提供（natives\win-x64\），CI 只负责复制。

    发行包里不能出现符号链接 / junction：
      * pnpm 的 isolated 布局是 junction 森林，tar 会跟着递归（实测直接把 bsdtar
        跑到 access violation 0xC0000005），robocopy 也会被环拖死；
      * zip 解压时 Windows 普通账号建不出符号链接，用户拿到的树是坏的。
    所以内核用 pnpm 的 hoisted 布局装成真文件，再由 Materialize-KernelTree.mjs
    兜底把残余链接物化成真目录/真文件。

.PARAMETER Phase
    all（默认）或分阶段跑：frontend / kernel / package。Actions 里分阶段调用，
    这样每一步都有自己的名字，跑的时候能看出进度。

.PARAMETER Version
    发行版本号，默认 0.0.1-beta。
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'frontend', 'kernel', 'package')]
    [string]$Phase = 'all',
    [string]$Version = '0.0.1-beta',
    [string]$Root
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
$script:runAll = ($Phase -eq 'all')

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

# 目录体积。CI 上目录是可扫描的真实树，这里可以安全递归。
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

# rd 不跟随 reparse point，用来删 junction。
function Remove-Tree([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    & cmd /c rd /s /q "$path" 2>$null
    if (Test-Path -LiteralPath $path) { Die "无法删除 $path" }
}

Say "仓库：$Root"
Say "产物：$out"
Say "阶段：$Phase"

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
if ($script:runAll -or $Phase -eq 'frontend') {
    Push-Location (Join-Path $Root 'RWebView')
    try {
        Run '前端：npm ci' { npm ci --no-audit --no-fund }
        Run '前端：node build.mjs' { node build.mjs }
        Assert-True (Test-Path -LiteralPath 'dist\index.html') '前端没有产出 dist/index.html'
    }
    finally { Pop-Location }
}

# ------------------------------------------------------------------ 2. 内核
if ($script:runAll -or $Phase -eq 'kernel') {
    Push-Location $coreSrc
    try {
        # 构建需要 devDependencies（tsc / tsdown / tsx），所以先做完整安装。
        Run '内核：pnpm install（含 dev）' { pnpm install --frozen-lockfile }
        Run '内核：pnpm run build' { pnpm run build }
        Assert-True (Test-Path -LiteralPath 'apps\cli\lib\bin.js') '内核没有产出 apps/cli/lib/bin.js'

        # 发行树必须没有 junction。nodeLinker 只能写在 pnpm-workspace.yaml 里，
        # 而在已有的 isolated 树上原地切布局会让 pnpm 去删旧的 workspace 链接并报
        # EPERM，所以在干净目录里重装一遍 CLI 的生产闭包。
        Say '内核：复制源码到干净的发行工作区'
        New-Item -ItemType Directory -Path $dist -Force | Out-Null
        $stage = Join-Path $dist '_kernel-stage'
        Remove-Tree $stage
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        & robocopy $coreSrc $stage /E /XD node_modules .turbo /XF .git /NFL /NDL /NJH /NJS /R:1 /W:1 | Out-Null
        Assert-True ($LASTEXITCODE -le 7) "robocopy 复制内核源码失败（exit $LASTEXITCODE）"

        $stageWorkspace = Join-Path $stage 'pnpm-workspace.yaml'
        $workspaceText = Get-Content -LiteralPath $stageWorkspace -Raw
        if ($workspaceText -notmatch '(?m)^nodeLinker:') {
            Add-Content -LiteralPath $stageWorkspace -Value "`n# CI 发行用：真文件布局，避免 junction 森林`nnodeLinker: hoisted"
        }

        Push-Location $stage
        try {
            Run '内核：安装 CLI 生产闭包（hoisted）' {
                pnpm install --prod --frozen-lockfile --ignore-scripts `
                    --filter '@deepseek-ai/dsh...' --os=win32 --cpu=x64
            }
        }
        finally { Pop-Location }

        Assert-True (Test-Path -LiteralPath (Join-Path $stage 'apps\cli\lib\bin.js')) '发行工作区里缺少 apps/cli/lib/bin.js'
    }
    finally { Pop-Location }
}

# ------------------------------------------------------------------ 3. 打包
if ($script:runAll -or $Phase -eq 'package') {
    New-Item -ItemType Directory -Path $dist -Force | Out-Null

    Run '宿主：dotnet publish' {
        dotnet publish (Join-Path $Root 'Responsible\Responsible.csproj') `
            -c Release -r win-x64 --self-contained true `
            -p:PublishReadyToRun=false -p:DebugType=none `
            -p:Version=$Version -p:AssemblyVersion=0.0.1.0 -p:FileVersion=0.0.1.0 `
            -o $out
    }
    Assert-True (Test-Path -LiteralPath (Join-Path $out 'Responsible.exe')) 'dotnet publish 没有产出 Responsible.exe'

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

    # 内核：物化成真文件树（不带任何 reparse point）
    Say '内核：物化发行树'
    $kernelStage = Join-Path $dist '_kernel-stage'
    Assert-True (Test-Path -LiteralPath (Join-Path $kernelStage 'node_modules')) '发行工作区还没有装依赖（先跑 kernel 阶段）'
    $coreDst = Join-Path $out 'RDshCore'
    if (Test-Path -LiteralPath $coreDst) { Remove-Tree $coreDst }
    New-Item -ItemType Directory -Path $coreDst -Force | Out-Null
    $materialize = Join-Path $Root 'scripts\Materialize-KernelTree.mjs'
    $stats = & node $materialize $kernelStage $coreDst 'node_modules,apps/cli/lib,apps/cli/package.json,package.json' ''
    if ($LASTEXITCODE -ne 0) { Die "内核物化失败（exit $LASTEXITCODE）：$stats" }
    Say "      $stats"

    $binJs = Join-Path $coreDst 'apps\cli\lib\bin.js'
    Assert-True (Test-Path -LiteralPath $binJs) '物化后缺少 apps\cli\lib\bin.js'
    Assert-UnderLimit '内核物化完成'

    # 冒烟：发行树里的内核必须能起来（这一步能抓住剪枝/物化搞坏依赖的情况）
    Say '冒烟：内核能否启动'
    Push-Location $coreDst
    try {
        $smoke = & node $binJs --help 2>&1 | Out-String
        $smokeCode = $LASTEXITCODE
    }
    finally { Pop-Location }
    if ($smokeCode -ne 0) {
        Write-Host $smoke
        Die "内核在发行树里启动失败（exit $smokeCode）"
    }
    Say ("      --help 正常（输出 {0} 行）" -f (($smoke -split "`n").Count))

    # 剪掉非 win32 平台的原生包
    Say '裁剪非 win32 平台的原生包'
    $platformPattern = '-(darwin|linux|freebsd|android|openbsd|netbsd|sunos|aix|arm64|armv7|ia32|ppc64|s390x|loong64|riscv64)-'
    $nm = Join-Path $coreDst 'node_modules'
    foreach ($base in @($nm, (Join-Path $nm '.pnpm'))) {
        if (-not (Test-Path -LiteralPath $base)) { continue }
        Get-ChildItem -LiteralPath $base -Directory -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match $platformPattern } |
            ForEach-Object { Remove-Tree $_.FullName }
    }
    Assert-UnderLimit '平台裁剪完成'

    Say '写版本信息'
    @(
        "Responsible $Version",
        "built: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "commit: $($env:GITHUB_SHA)",
        "runtime: .NET self-contained + bundled node $(node --version)",
        "kernel: DSH CLI production closure (hoisted, no symlinks)",
        "native: RVM/RTray built with MinGW-w64 (Release)"
    ) -join [Environment]::NewLine | Set-Content -LiteralPath (Join-Path $out 'version.txt') -Encoding utf8

    foreach ($doc in @('README.md', 'BUILD-GUIDE.md')) {
        $source = Join-Path $Root $doc
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $out -Force }
    }

    Assert-UnderLimit '组装完成'

    Say '生成 zip'
    $zip = Join-Path $dist "$packageName.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Run 'zip' { tar -a -cf $zip -C $dist $packageName }

    $zipMb = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB)
    $outMb = [math]::Round((Get-TreeBytes $out) / 1MB)
    Say ("完成：目录 {0:N0} MB / zip {1:N0} MB（上限 {2:N0} MB），用时 {3:N1} 分钟" -f
        $outMb, $zipMb, ($limit / 1MB), $script:clock.Elapsed.TotalMinutes)
}
