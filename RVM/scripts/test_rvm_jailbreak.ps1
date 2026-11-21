# RVM 越狱自测：在工作区里尝试读取/写入工作区外的金丝雀文件，
# 并验证用户暴露的宿主 python 继承了隔离令牌。
# 只触碰本脚本/引擎创建的 rvm_jailbreak_* 金丝雀与测试工作区文件。
[CmdletBinding()]
param(
  [string]$Workspace,
  [string]$Engine,
  [string]$PythonDir
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Workspace) {
  $Workspace = Join-Path $repo 'Responsible\test_workspace_mock'
}
if (-not $Engine) {
  foreach ($candidate in @(
      (Join-Path $repo 'RVM\build\bin\RvmEngine.exe'),
      (Join-Path $repo 'RVM\cmake-build-debug\bin\RvmEngine.exe'))) {
    if (Test-Path -LiteralPath $candidate) { $Engine = $candidate; break }
  }
}
if (-not $Engine -or -not (Test-Path -LiteralPath $Engine)) {
  throw 'RvmEngine.exe 未找到：先构建 RVM，或用 -Engine 指定路径'
}

if (-not $PythonDir) {
  $python = Get-Command python.exe -ErrorAction SilentlyContinue
  if ($python) { $PythonDir = Split-Path $python.Source }
}
if (-not $PythonDir -or -not (Test-Path -LiteralPath (Join-Path $PythonDir 'python.exe'))) {
  throw '未找到宿主 python.exe：用 -PythonDir 指定目录'
}

$Workspace = [IO.Path]::GetFullPath($Workspace)
$PythonDir = (Resolve-Path -LiteralPath $PythonDir).Path
$Engine = (Resolve-Path -LiteralPath $Engine).Path
New-Item -ItemType Directory -Force -Path $Workspace | Out-Null

# 只清理由本自测命名的残留文件，避免上次异常退出留下金丝雀。
$staleDirs = @($env:TEMP, $env:USERPROFILE, (Split-Path $Workspace -Parent), $Workspace)
foreach ($dir in $staleDirs) {
  if (-not $dir -or -not (Test-Path -LiteralPath $dir)) { continue }
  Get-ChildItem -LiteralPath $dir -Filter 'rvm_jailbreak_*' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
}
Get-ChildItem -LiteralPath $Workspace -Filter '_rvm_jailbreak_*' -File -ErrorAction SilentlyContinue |
  Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "workspace=$Workspace"
Write-Host "engine=$Engine"
Write-Host "python_dir=$PythonDir"
Write-Host 'note=首次暴露较大的工具目录（如 Python）会传播目录 ACL，启动/停止可能耗时数分钟'

& $Engine --sandbox-jailbreak $Workspace --tool-path $PythonDir --python (Join-Path $PythonDir 'python.exe')
$exitCode = $LASTEXITCODE

if ($exitCode -eq 0) {
  Write-Host '自测通过：工作区外读写被拒绝，暴露的 python 继承隔离令牌且只能写入工作区。'
} else {
  Write-Host '自测失败：检查上方 FAIL 项。'
}
exit $exitCode