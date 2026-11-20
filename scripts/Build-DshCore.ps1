# 构建 RDshCore（DSH 内核 submodule），产出 apps/cli/lib/bin.js 供 WPF 宿主拉起。
# 依赖：Node.js >= 22、pnpm（corepack enable 即可）。首次构建需下载 monorepo 依赖，耗时较长。
$ErrorActionPreference = 'Stop'
$core = Join-Path $PSScriptRoot '..\RDshCore'
Push-Location $core
try {
    pnpm install
    pnpm run build
} finally {
    Pop-Location
}
Write-Host "DSH 内核构建完成：$core\apps\cli\lib\bin.js"