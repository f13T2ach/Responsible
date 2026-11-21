// RVM 沙箱提供者：替换 DSH 自带的 same-world sandbox 服务，把 shell 子进程包进
// `RvmEngine.exe --shell-bridge`，由引擎在自己的 AppContainer 会话里执行。
//
// 真实边界（如实上报）：argv 与 cwd 经引擎转发到会话叠加盘，进程在引擎会话的容器
// 里运行；但本次改造尚未接管 fs_* 工具，宿主文件操作仍直连磁盘，因此 enforcement
// 固定为 partial，不伪造 full。
import { existsSync } from 'node:fs'
import { pathToFileURL } from 'node:url'
import { resolve } from 'node:path'
import { promptSections } from './prompts.mjs'

export const name = 'rvm-sandbox'

export const inject = ['systemPrompt']

const RUNNER_SIGNATURE = 'RVM_SANDBOX_UNAVAILABLE'

function normalizeRoot(root) {
  return resolve(root).replace(/[\\/]+$/, '').toLowerCase()
}

function requiredString(config, key) {
  const value = config?.[key]
  if (typeof value !== 'string' || value.trim() === '') return undefined
  return value
}

export async function apply(ctx, config = {}) {
  const modulePath = requiredString(config, 'sandboxModule')
  if (modulePath === undefined)
    throw new Error('rvm-sandbox: sandboxModule is required')
  const module = await import(pathToFileURL(modulePath).href)
  const SandboxProvider = module.SandboxProvider
  const SandboxUnavailableError = module.SandboxUnavailableError
  if (typeof SandboxProvider !== 'function' || typeof SandboxUnavailableError !== 'function')
    throw new Error(`rvm-sandbox: ${modulePath} does not export SandboxProvider/SandboxUnavailableError`)

  const enginePath = requiredString(config, 'enginePath')
  const pipeName = requiredString(config, 'pipeName')
  const sessionRaw = config?.sessionId
  const sessionId = typeof sessionRaw === 'string' && /^[1-9][0-9]{0,19}$/.test(sessionRaw)
    ? sessionRaw
    : undefined
  const workspaceRoot = requiredString(config, 'workspaceRoot')

  function failClosed(mode, detail) {
    return new SandboxUnavailableError(mode, detail)
  }

  class RvmSandboxProvider extends SandboxProvider {
    async confine(argv, policy, signal) {
      signal?.throwIfAborted()
      if (!Array.isArray(argv) || argv.length === 0)
        throw failClosed(policy.mode, 'confine was called without an argv')

      // danger-full-access 由调用方短路，不会走到这里；read-only 引擎尚不能保证拒绝写入
      if (policy.mode !== 'workspace-write')
        throw failClosed(policy.mode, `the RVM backend does not support "${policy.mode}" sessions yet`)

      if (enginePath === undefined || !existsSync(enginePath))
        throw failClosed(policy.mode, 'RvmEngine.exe was not found')
      if (pipeName === undefined || sessionId === undefined)
        throw failClosed(policy.mode, 'no live RVM session was configured for this workspace')
      if (workspaceRoot === undefined)
        throw failClosed(policy.mode, 'no RVM workspace root was configured')
      if (normalizeRoot(policy.workspaceRoot) !== normalizeRoot(workspaceRoot))
        throw failClosed(
          policy.mode,
          `workspace mismatch: the shell runs in ${policy.workspaceRoot} but the RVM session covers ${workspaceRoot}`)
      if (!process.env.RESPONSIBLE_RVM_TOKEN)
        throw failClosed(policy.mode, 'RESPONSIBLE_RVM_TOKEN is missing from the kernel environment')

      return {
        argv: [
          enginePath,
          '--shell-bridge',
          '--pipe', pipeName,
          '--session', sessionId,
          '--',
          ...argv,
        ],
        enforcement: 'partial',
        denialSignatures: [],
        runnerFailureRules: [{
          allowedExitCodes: [127],
          fatalSignatures: [RUNNER_SIGNATURE],
        }],
      }
    }
  }

  ctx.effect(() => {
    const section = promptSections.rvm
    if (ctx.systemPrompt !== undefined) {
      ctx.systemPrompt.section({
        name: section.name,
        order: section.order,
        interpolate: section.interpolate,
        text: workspaceRoot === undefined
          ? section.text
          : `${section.text}\n- Workspace root (file tools): ${workspaceRoot}`,
      })
    }
    const provider = new RvmSandboxProvider(ctx)
    return () => {
      void provider
    }
  })
}