# Responsible 发行构建指南（给接手的人/模型）

> 这份文档是为避免重复已经发生过的灾难而写的。**先读完再动手。**
> 上一轮构建把一个 100GB 可用空间的磁盘吃到 50GB 以下，删了 20 多分钟，原因全部在下面"陷阱"里。

---

## 零、现在怎么构建（现行流程）

本机**不再**跑整包构建，只在改到原生代码时构建 RVM / RTray：

| 在哪 | 做什么 |
| --- | --- |
| 本机 | `.\build-release.ps1` → 只构建 RVM、RTray（Release），产物落到 `natives\win-x64\` 并提交 |
| GitHub Actions | `.github/workflows/build-release.yml` → 前端、内核（pnpm）、WPF publish、组装、剪平台、2GB 体积锁、zip、上传 artifact |

理由：本地磁盘只有 130GB 可用，而 `pnpm install` 一次就要 8GB store + 5GB hoisted；
GitHub 的 Windows runner 是干净且随用随弃的，装坏了丢掉重跑即可。

下面的陷阱清单仍然有效——它们解释了为什么本机不要碰内核依赖树，也解释了
CI 组装脚本（`scripts/Build-ReleasePackage.ps1`）为什么用 tar 而不是 robocopy。

## 一、目标

产出一个 **Windows x64 自包含**发行包，版本 `0.0.1-beta`：

| 组件 | 来源 | 说明 |
| --- | --- | --- |
| `Responsible.exe` | `Responsible/`（WPF, net10.0-windows） | `dotnet publish -c Release -r win-x64 --self-contained true` |
| `node\node.exe` | 本机 node（v24+） | 便携运行时，宿主的 `ResolveNodeExecutable()` 会优先用它 |
| `RDshCore/` | `RDshCore/`（DSH 内核） | **只用构建产物 `lib/` + 运行期依赖**，不带 GUI 客户端/站点/基准 |
| `RDshPlugins/` | `RDshPlugins/` | 宿主注入内核的插件 |
| `RvmEngine.exe` + 3 个 MinGW DLL | `RVM/build/bin/` | `libgcc_s_seh-1.dll`、`libstdc++-6.dll`、`libwinpthread-1.dll` |
| `RTray.exe` | `RTray/build/bin/` | 看护进程 |
| `RWebView/` | `RWebView/dist/` | 对话区与右侧栏前端 |

**用户要求**

1. 最终包 **≤ 2GB**（硬上限，超了立刻中止并清理）
2. 每 **60 秒**检查一次进度，禁止长时间不管
3. 禁止长时间占用大量磁盘、禁止产生海量临时文件
4. **先读文档再构建**，不要凭猜测试错
5. **禁止**把其它平台（darwin/linux/arm64…）的原生包打进发行包

---

## 二、陷阱清单（每一条都真实发生过）

### 陷阱 1：`node_modules` 不是普通目录树

`RDshCore/node_modules` 是 pnpm 的**硬链接 + 符号链接**结构：

- 真实内容只在 `node_modules/.pnpm/<pkg>@<ver>/node_modules/<pkg>` 存一份
- `node_modules/<pkg>` 和各级依赖都是指向它的**硬链接**
- `node_modules/@deepseek-ai/*` 是指回 `packages/*` 的**符号链接**，而 `packages/*/node_modules` 又指回来 —— **成环**

**由此产生的三个灾难：**

| 操作 | 后果 |
| --- | --- |
| `robocopy /E` 复制 | 把每个硬链接展开成独立文件：2.5GB → **10GB+ / 100 万文件** |
| `robocopy /E` 或 `/MIR` 遍历 | 跟随符号链接环，**卡死 40 分钟以上** |
| `Remove-Item -Recurse` / `rd /s /q` | 同样跟随环，删除要 **20+ 分钟** |

**正确做法**：用 `tar` 传目录（tar 认硬链接，同一份内容只存一次）。

```powershell
tar -cf rdsh.tar -C RDshCore --exclude=./src --exclude=./tests .   # 归档 ≈ 700MB
tar -xf rdsh.tar -C <out>\RDshCore                                  # 解包同样 ≈ 700MB
```

### 陷阱 2：`pnpm install` 必然建 store（这是体积大头）

```
D:\.pnpm-store          ~8 GB      ← pnpm 的内容寻址存储，装一次必建
node_modules (hoisted)  ~5 GB      ← 平铺后是实体，不是链接
```

`--config.package-import-method=copy`（DSH 官方打包用的就是这个）只是让包复制进 `node_modules`，**不省 store**。删掉 store 再重装 = 重新下一遍。

**峰值 17GB 的构成**：store 8G + hoisted node_modules 5G + publish 0.2G + 复制 1.5G + pnpm 临时 2G。

**正确做法**：**不要跑 `pnpm install`**。`RDshCore/node_modules` 已经在你机器上了，用 tar 归档它。

### 陷阱 3：`job_kill` / `Stop-Job` 不杀子进程

这是最致命的一条：

```
pid=12956  robocopy.exe  CPU=5812s(97分钟)  启动 16:50:58
```

一条 `robocopy /MIR 空目录` 命令被 `job_kill` 之后，**robocopy.exe 仍在后台跑了 97 分钟**，持续遍历目录树、持续写盘。结果是：每次删完文件，它又制造写入，`D:` 可用空间**边删边降**（130GB → 101GB），看起来像"越删越少"。

当时我错误地怀疑了 VSS 卷影副本，用户查了：**D 盘没有卷影副本**，判断错误。

**正确做法**：

```powershell
# 每次启动后台任务后，都要能查到它的真实进程
Get-Process robocopy,xcopy -ErrorAction SilentlyContinue | Select-Object Id,CPU,StartTime
Get-Process robocopy,xcopy -ErrorAction SilentlyContinue | Stop-Process -Force
```

`Start-Process -PassThru` 会返回真实进程对象，用它判断存活；**不要只信 job 状态**。

### 陷阱 4：量体积不要递归扫目录

`Get-ChildItem -Recurse | Measure-Object Length -Sum` 在 90k 文件 + 链接环上要几分钟，而且会被环拖死。

**正确做法**：用驱动器空闲差值，瞬时完成。

```powershell
$baseline = (Get-PSDrive D).Free
$used = $baseline - (Get-PSDrive D).Free     # 构建增量
if ($used -gt 2GB) { 中止并清理 }
```

### 陷阱 5：删除慢 / 删不掉

- `Remove-Item -Recurse` 跟随符号链接，遇环卡死
- Python `shutil.rmtree(..., ignore_errors=True)` **会静默失败**（权限/环），你以为删了其实还在
- `rd /s /q` 不跟随链接，但**慢**（760MB / 90k 文件约 10–15 分钟），且**不要中途打断**——打断后空间不会马上回来

**正确做法**：`rd /s /q` 前台跑到底，用 `Start-Process -PassThru` 拿 PID 轮询，每 60 秒报一次空间。实测从 101GB 回到 130GB 用了约 12 分钟。

### 陷阱 6：`npx pnpm ...` 的参数会被 npm 吃掉

`npx --yes pnpm@11 install --os=win32 --cpu=x64` 里的 `--os` 会被 npm 当成自己的配置，报 `npm warn Unknown config options`，PowerShell 5.1 还会把它当**终止错误**导致脚本 `exit 1`。

**正确做法**：平台过滤不靠命令行参数，装完按目录名删：

```powershell
$platforms = "-(darwin|linux|freebsd|android|armv7|ia32|ppc64|s390x|loong64|riscv64)-"
Get-ChildItem node_modules -Directory | Where-Object { $_.Name -match $platforms } | Remove-Item -Recurse -Force
```

需要临时降级错误处理时：

```powershell
$prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
try { & npx ... } finally { $ErrorActionPreference = $prev }
```

### 陷阱 7：DSH 官方包 1GB 是怎么来的

`RDshCore/apps/desktop/README.md:104`：

> `electron-builder` copies only the manifest's `dependencies` into `app.asar/node_modules`

打包入口 `apps/desktop/scripts/package-target.ts`，准备阶段 `prepare-dsh.ts`，包集合白名单在 `apps/desktop/src/core-package-set.ts`。它 **1GB = Electron/Chromium + 按 dependencies 闭包装出来的 asar**，不是整棵仓库复制。

我们的宿主是 WPF、不走 Electron，所以**只装内核 CLI 的依赖闭包**才是对应做法：

```powershell
pnpm install --prod --frozen-lockfile --ignore-scripts --filter "@deepseek-ai/dsh..."
```

**已验证：这样装出来是 760MB。**

---

## 三、已验证的数据（可以直接用）

| 项 | 值 |
| --- | --- |
| 内核（`--filter @deepseek-ai/dsh...` 闭包 + `lib`） | **760 MB** |
| WPF self-contained publish | ~150 MB |
| node.exe | ~80 MB |
| RvmEngine.exe | 57.9 MB（+ 3 个 DLL 共 ~3.5MB） |
| RTray.exe | ~1 MB |
| RWebView dist | ~1 MB |
| **目标合计** | **≈ 1.05 GB** |

参照：pnpm 实测 `--filter` 闭包 = 414 个包 / 93912 文件 / 逻辑 1249MB。

---

## 四、推荐执行顺序

```powershell
# 0. 前置检查（缺一个就停）
Test-Path RDshCore\apps\cli\lib\bin.js      # 内核必须已构建
Test-Path RDshCore\node_modules             # 依赖必须在位
Test-Path RVM\build\bin\RvmEngine.exe
Test-Path RTray\build\bin\RTray.exe
Test-Path RWebView\dist\index.html

# 1. 前端（秒级）
cd RWebView; node build.mjs

# 2. 原生（已有产物可跳过）
cmake --build RVM\build
cmake --build RTray\build

# 3. WPF（1–2 分钟）
dotnet publish Responsible\Responsible.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishReadyToRun=false -p:DebugType=none `
  -p:Version=0.0.1-beta -o dist\Responsible-0.0.1-beta-win-x64

# 4. 内核：tar 归档 → 解包（不跑 install，不建 store）
tar -cf dist\rdsh.tar -C RDshCore --exclude=./src --exclude=./tests `
    --exclude=./website --exclude=./benchmarks --exclude=./.git .
tar -xf dist\rdsh.tar -C dist\Responsible-0.0.1-beta-win-x64\RDshCore

# 5. 剪掉非 Windows 平台包（见陷阱 6）

# 6. 复制 node / RWebView / RDshPlugins / RvmEngine + DLL / RTray

# 7. 校验 ≤2GB，然后 zip
```

**每完成一步，用 `Get-PSDrive D` 差值算一次增量，超过 2GB 立即 `rd /s /q dist` 并停止。**

---

## 五、绝对禁止

1. ❌ 对 `RDshCore`、`node_modules`、`dist` 用 `robocopy` / `Copy-Item -Recurse` / `Remove-Item -Recurse`
2. ❌ 在 `RDshCore` 里跑 `pnpm install`（会重建 8GB store）
3. ❌ 用 `Get-ChildItem -Recurse` 统计体积或文件数
4. ❌ 启动后台任务后就撒手不管（必须每 60s 查一次，且查**真实进程**）
5. ❌ 把 `.pnpm` 里所有平台的原生包装进包
6. ❌ 中途打断 `rd` 然后判断"空间没回来"——它会回来的，等它跑完
7. ❌ 在没算清峰值之前就开始跑

---

## 六、当前代码状态

`build-release.ps1` 已被改成 **tar 方案**（不跑 `pnpm install`、不建 store），并带 2GB 体积锁：

- `Step` 每进入一步先 `Assert-Size`，超限 `Clear-Dist` 后抛异常
- 体积用 `Get-PSDrive D` 差值计算
- `Clear-Dist` 先 `rd /s /q`，再 Python `rmtree` 兜底

**但它一次都没跑通过。** 不要当成品用。

---

## 七、给接手者的第一条建议

先只验证最小的一步，不要一上来跑整包：

```powershell
# 只做归档，不解包、不构建、不复制
$sw = [Diagnostics.Stopwatch]::StartNew()
tar -cf "$env:TEMP\rdsh-probe.tar" -C RDshCore --exclude=./src --exclude=./tests .
"tar 用时 $([int]$sw.Elapsed.TotalSeconds)s，大小 $([math]::Round((Get-Item "$env:TEMP\rdsh-probe.tar").Length/1MB)) MB"
Remove-Item "$env:TEMP\rdsh-probe.tar"
```

如果这个归档是 **700MB 左右、耗时 1–3 分钟**，说明硬链接被 tar 正确识别，方案成立，可以继续。如果是 2GB+ 或跑 10 分钟，停下重新分析，别硬推。

---

## 八、磁盘基线（供比对）

| 时间点 | D 盘可用 |
| --- | --- |
| 灾难前 | ~100 GB |
| 膨胀峰值 | ~84 GB |
| 清理后 | 130.3 GB |

以后每次构建前后各记一次，差值超过 2GB 就说明哪里不对。
