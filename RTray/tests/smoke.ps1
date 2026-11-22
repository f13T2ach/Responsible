# RTray 自测：模拟宿主被强杀，验证收尾与崩溃报告
param(
  [string]$Exe = (Join-Path $PSScriptRoot '..\build\bin\RTray.exe')
)
$ErrorActionPreference = 'Stop'

$exe = (Resolve-Path $Exe).Path
$fixture = Join-Path $env:TEMP ('rtray-fixture-' + [guid]::NewGuid().ToString('n'))
$volumes = Join-Path $fixture 'volumes'
$reports = Join-Path $fixture 'logs'
New-Item -ItemType Directory -Path $volumes -Force | Out-Null
New-Item -ItemType Directory -Path $reports -Force | Out-Null
Set-Content -Path (Join-Path $volumes 'marker.txt') -Value 'session disk' -Encoding UTF8

# 找一个空闲盘符建 subst，模拟 RVM 留下的映射
$drive = $null
foreach ($letter in @('Z', 'Y', 'X', 'W', 'V')) {
  $candidate = "${letter}:"
  if (-not (Test-Path $candidate)) { $drive = $candidate; break }
}
if (-not $drive) { throw '找不到空闲盘符' }
& subst $drive $volumes
Write-Output "fixture: $drive -> $volumes"

# 假宿主：一个会睡 60 秒的 PowerShell
$fakeHost = Start-Process -FilePath 'powershell.exe' -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 60' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 600
Write-Output "fake host pid: $($fakeHost.Id)"

$out = Join-Path $fixture 'rtray.out'
$err = Join-Path $fixture 'rtray.err'
$tray = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden `
  -ArgumentList '--watch', $fakeHost.Id, '--cleanup-subst', $volumes, '--report-dir', $reports, '--grace', '200' `
  -RedirectStandardOutput $out -RedirectStandardError $err
Start-Sleep -Milliseconds 900

# 强杀宿主（等价于任务管理器结束进程）
Stop-Process -Id $fakeHost.Id -Force
Write-Output 'host killed'

if (-not $tray.WaitForExit(20000)) { Stop-Process -Id $tray.Id -Force; throw 'RTray 没有在宿主退出后结束' }
Write-Output "rtray exit=$($tray.ExitCode)"
Write-Output ("rtray out: " + (Get-Content $out -Raw -Encoding UTF8).Trim())
$errorText = (Get-Content $err -Raw -Encoding UTF8)
if ($errorText) { Write-Output ("rtray err: " + $errorText.Trim()) }

$mappingGone = -not (Test-Path $drive)
Write-Output ("盘符已清理: $mappingGone")

$report = Get-ChildItem $reports -Filter 'crash-*.json' | Select-Object -First 1
if (-not $report) { throw '没有生成崩溃报告' }
Write-Output "报告: $($report.FullName)"
Get-Content $report.FullName -Raw | Write-Output

$json = Get-Content $report.FullName -Raw | ConvertFrom-Json
$ok = $mappingGone -and ($json.graceful -eq $false) -and ($json.cleared_drives -contains $drive)
Write-Output ("自测结果: " + $(if ($ok) { 'PASS' } else { 'FAIL' }))

if (Test-Path $drive) { & subst $drive /D }
Remove-Item $fixture -Recurse -Force -ErrorAction SilentlyContinue
if (-not $ok) { exit 1 }
