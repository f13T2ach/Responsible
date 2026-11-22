# RTray

宿主的看护进程。Responsible.exe 被强杀（任务管理器结束、崩溃、被 kill）时，替它做两件收不回来的事：

1. **收尾**：清掉指向 RVM 存储的 `subst` 盘符、结束遗留的 `RvmEngine.exe` / `node.exe`；
2. **记录**：写一份崩溃报告 JSON，并往 `crash.log` 追加一行摘要。

它不参与任何业务逻辑，宿主正常运行期间只是阻塞在 `WaitForMultipleObjects` 上。

## 为什么需要它

`subst` 是系统级 DOS 设备映射，**进程退出不会自动消失**。正常关窗时引擎会自己 `subst X: /D`，但被强杀时没人执行这一步，盘符会一直指向已经被删掉的会话盘；同时 DSH 内核（node）和 RvmEngine 会变成孤儿进程。宿主下次启动虽然会清理遗留映射，但那是"下一次"——RTray 补的是"这一次"。

## 构建

与 RVM 完全相同的工具链（CMake + Ninja + MinGW-w64，C++20）：

```powershell
cmake -S RTray -B RTray\build -G Ninja -DCMAKE_BUILD_TYPE=Debug
cmake --build RTray\build
```

产物：`RTray\build\bin\RTray.exe`（MinGW 运行时 DLL 会一起复制到旁边）。

## 用法

```
RTray --watch <pid> [选项]

  --watch <pid>              要监视的宿主进程（必填）
  --bye-event <name>         宿主正常退出时置位的命名事件；收到就只记录、不清理
  --heartbeat <file>         宿主的心跳文件，报告里带最后一次心跳距今多久
  --cleanup-subst <root>     清理所有映射到 <root> 之下的 subst 盘符
  --no-cleanup-children      不结束遗留的 RvmEngine.exe / node.exe
  --report-dir <dir>         报告目录，默认 %LOCALAPPDATA%\Responsible\logs
  --grace <ms>               宿主退出后等子进程自己收尾的时间，默认 1500
  --version / --help
```

示例（宿主启动时拉起）：

```
RTray.exe --watch 12345 ^
          --bye-event Responsible-Bye-12345 ^
          --heartbeat C:\Users\me\AppData\Local\Responsible\logs\host-heartbeat ^
          --cleanup-subst C:\Users\me\AppData\Local\Responsible\RVM ^
          --report-dir C:\Users\me\AppData\Local\Responsible\logs
```

## 宿主契约

| 事项 | 约定 |
| --- | --- |
| 告别事件 | 宿主创建一个同名自动事件（`Responsible-Bye-<pid>`），**所有清理完成后** `Set()`；RTray 收到即退出，不碰盘符 |
| 心跳 | 宿主每 5 秒写一次心跳文件；报告里的 `heartbeat_age_ms` 说明"最后一次还活着是什么时候" |
| 清理范围 | 只移除 `--cleanup-subst` 指定根**之下**的映射，用户自己的 `subst` 不会被碰 |
| 子进程 | 只结束 `RvmEngine.exe`、`node.exe`，且要求 `th32ParentProcessID` 等于宿主 PID |

## 报告

`<report-dir>\crash-<YYYYMMDD-HHMMSS>-<pid>.json`：

```json
{
  "tool": "RTray 0.1.0-p0",
  "at": "2026-11-22T13:59:40Z",
  "host_pid": 12720,
  "graceful": false,
  "exit_code": -1,
  "exit_code_hex": "0xFFFFFFFF",
  "heartbeat_age_ms": 8123,
  "subst_root": "C:\\Users\\...\\Responsible\\RVM",
  "cleared_drives": ["R:"],
  "failed_drives": [],
  "terminated_processes": [{ "pid": 4321, "name": "RvmEngine.exe" }]
}
```

`exit_code_hex` 常见的几种：

| 值 | 含义 |
| --- | --- |
| `0xFFFFFFFF` | `TerminateProcess`（任务管理器结束进程） |
| `0xC000013A` | 控制台被 Ctrl-C / 关窗打断 |
| `0xC0000005` | 访问冲突（宿主自身崩溃） |
| `0xE0434352` | .NET 未处理异常 |

同时会在 `crash.log` 追加：

```
2026-11-22T13:59:40Z host=12720 abnormal exit=0xFFFFFFFF cleared=1 killed=0 report=...
```

## 自测

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File RTray\tests\smoke.ps1
```

脚本会：建一个 fixture 目录并 `subst` 成空闲盘符 → 起一个假宿主 → 拉起 RTray → `Stop-Process -Force` 强杀宿主 → 断言盘符被清掉、报告里 `graceful=false` 且 `cleared_drives` 含该盘符。

## 限制

- 只处理"宿主没了"这一种情况；宿主卡死（进程还在、无响应）不会被判定为崩溃，需要另外看 `heartbeat_age_ms`。
- 不捕获宿主自身的异常堆栈——那是 .NET 运行时写事件日志的职责；RTray 记的是"事后状态"。
- 宿主若把 RTray 放进同一个 Job Object 并整树结束，RTray 也会被杀；宿主侧不要这么做。
