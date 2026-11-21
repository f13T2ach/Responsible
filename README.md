# Responsible

试着对模型的每一响应负责。

## 介绍

Responsible 的内核是 Deepseek Harness，通过载入自定插件来重新路由 AI 的指令请求到 Responsible Virtual Machine（RVM），同时支持基于 FUSE 文件系统的工具链穿透、独立工作区来提供安全的 Agent 辅助编程环境，期望任何人和 AI 都无法突破 Responsible 进行隐私读取和中间人攻击。

Responsible 支持安装专为 Responsible 定制的 DSH 插件（RDsh）来定义含本地配置文件的敏感工具的调用方式（如Git, SSH）

## 典型的工作方式

Responsible-CLI 跨平台版本正在开发，目前只支持 Windows 版本。

1. 克隆仓库（.Net 10），配置 Nuget 包， 构建编译。或者直接从 Release 下载 Github Workflow 的构建。
2. 选择“克隆工作区”来进行受保护的工作。
3. 配置 RVM，引入要穿透的工具链和环境变量。
4. 配置 RDsh 插件，引入 AI 可用的敏感工具。
5. 开始工作。当一轮工作完成后，可以把 RVM 中工作区的改动 Flush 到本地。

## RDsh 插件

RDsh 插件专为调用敏感工具而生，RDsh 插件的工作方式是接收 `rshell` 传入的 Session，进行审计后转发到宿主机，也就是实现一个凭证代理

由于敏感的工具比较复杂（例如 Git） ，需要专门编写 RDsh 插件来定义调用方式。不过，Responsible也保留了一个专门的用于在无 RDsh 插件可用时转发 Session 的插件：`runiverse`

请注意：使用 `runiverse` 是缓兵之计，因为在人工审计失效的情况下，AI 能利用 runiverse 来控制宿主机。

**要了解更多，前往 RDshPlugins/README.md**

## RVM 虚拟机

### RVM 介质
1. RVM Infrastructure
RVM 实现了 Windows 的虚拟机，与宿主同系统，提供基础 Powershell, CMD, Winget 等工具
2. RVM Workspace
RVM 实现了 VM 内的工作区，工作区是原工作区的克隆，用户可以在审计后进行 Rollback, Sync 操作。
3. RVM Toolchain
RVM 实现了工具链穿透，AI 能直接调用用户指定的工具但只对 VM 本身造成影响。

### RVM 实现
1. RVM Channel
RVM 内对外部工具链的调用和网络调用都会转发给 Channel。
4. RVM EnvHook
RVM 会记录 AI 对环境的修改，用户可以在审计后进行 Rollback, Apply 操作。
5.


**要了解更多，前往 RVM/README.md**

## RWebView

**要了解更多，前往 RWebView/README.md**