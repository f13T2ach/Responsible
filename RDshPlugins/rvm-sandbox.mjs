// RVM 沙箱提供者：替换 DSH 自带的 same-world sandbox 服务，把 shell 子进程包进
// `RvmEngine.exe --shell-bridge`，由引擎在自己的 AppContainer 会话里执行。
//
// 受保护工作区跑在会话盘上（引擎按 git 索引把源码选择性快照到 RVM 存储，再用 subst 映射成盘符），
// 宿主工作区目录不会授权给沙箱。shell 由引擎在会话盘里执行；fs_* 工具在宿主进程里运行，
// 本插件把对宿主工作区的解析改写到会话盘，并拒绝两者之外的路径。fs 侧仍是策略检查而非
// 内核边界，因此 enforcement 保持 partial，不伪造 full。
import { existsSync } from 'node:fs'
import { realpath } from 'node:fs/promises'
import { pathToFileURL } from 'node:url'
import { join, relative, resolve, sep } from 'node:path'
import { promptSections } from './prompts.mjs'

export const name = 'rvm-sandbox'

export const inject = ['systemPrompt', 'fs', 'sandboxPolicy']

const RUNNER_SIGNATURE = 'RVM_SANDBOX_UNAVAILABLE'

function normalizeRoot(root) {
  return resolve(root).replace(/[\\/]+$/, '').toLowerCase()
}

// The model-facing fs tools resolve every model path here before touching disk.
// Confining resolve confines reads, list/stat, image reads and mutations alike.
function isInsideRoot(root, hostPath) {
  if (typeof hostPath !== 'string' || hostPath === '') return false
  const value = resolve(hostPath).replace(/[\\/]+$/, '').toLowerCase()
  return value === root || value.startsWith(`${root}${sep}`)
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
  // 工作区要求了隔离但宿主没能建立会话：这时引擎参数必然缺失。用宿主给的理由
  // 直接拒绝，而不是让调用方去猜“是沙箱 ACL 坏了”。这仍然 fail closed，不降级。
  const unavailableReason = requiredString(config, 'unavailableReason')
  // workspaceRoot is the mounted session disk; hostWorkspaceRoot is the real
  // workspace the model must not reach.
  const workspaceRoot = requiredString(config, 'workspaceRoot')
  const hostWorkspaceRoot = requiredString(config, 'hostWorkspaceRoot') ?? workspaceRoot

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

      // 宿主的 RVM 会话没就绪：这是引擎/会话状态，不是文件权限问题。
      // 说清楚，避免把诊断花在 ACL 或 danger-full-access 上。
      if (unavailableReason !== undefined)
        throw failClosed(
          policy.mode,
          `the RVM session for this workspace is not established: ${unavailableReason}. `
          + 'This is not a file-permission problem: do not try to repair ACLs or ask for danger-full-access. '
          + 'Tell the user the RVM engine state and wait for the host to fix it.')

      if (enginePath === undefined || !existsSync(enginePath))
        throw failClosed(policy.mode, 'RvmEngine.exe was not found')
      if (pipeName === undefined || sessionId === undefined)
        throw failClosed(policy.mode, 'no live RVM session was configured for this workspace')
      if (workspaceRoot === undefined || hostWorkspaceRoot === undefined)
        throw failClosed(policy.mode, 'no RVM workspace root was configured')
      if (normalizeRoot(policy.workspaceRoot) !== normalizeRoot(hostWorkspaceRoot)
          && normalizeRoot(policy.workspaceRoot) !== normalizeRoot(workspaceRoot))
        throw failClosed(
          policy.mode,
          `workspace mismatch: the shell runs in ${policy.workspaceRoot} but the RVM session covers ${hostWorkspaceRoot}`)
      // The DSH shell seam spawns with a freshly built environment, so the
      // bridge cannot inherit this token; hand it over explicitly instead.
      const token = process.env.RESPONSIBLE_RVM_TOKEN
      if (!token)
        throw failClosed(policy.mode, 'RESPONSIBLE_RVM_TOKEN is missing from the kernel environment')

      return {
        argv: [
          enginePath,
          '--shell-bridge',
          '--pipe', pipeName,
          '--session', sessionId,
          '--token', token,
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

  // 只有会话真的建起来时才向模型描述沙箱边界；未就绪时不给错误的环境描述，
  // 让失败信息只出现在 confine() 那一条上。
  if (workspaceRoot !== undefined) {
    ctx.effect(() => {
      // 探针模式下换用允许主动越界的提示词；沙箱策略不变
  const section = config?.sandboxProbe === true ? promptSections.rvmProbe : promptSections.rvm
      if (ctx.systemPrompt !== undefined) {
        ctx.systemPrompt.section({
          name: section.name,
          order: section.order,
          interpolate: section.interpolate,
          text: `${section.text}\n- Workspace root (file tools): ${workspaceRoot}`,
        })
      }
      const provider = new RvmSandboxProvider(ctx)
      return () => {
        void provider
      }
    })
  } else {
    ctx.effect(() => {
      const provider = new RvmSandboxProvider(ctx)
      return () => {
        void provider
      }
    })
  }

  // One choke point: every fs tool target is resolved through ctx.fs.resolve.
  // A path inside the host workspace is rewritten to the same relative path on
  // the mounted session disk; anything outside both roots is refused.
  if (workspaceRoot !== undefined && ctx.fs !== undefined) {
    const fs = ctx.fs
    const guestRoot = normalizeRoot(workspaceRoot)
    const hostRoot = normalizeRoot(hostWorkspaceRoot)
    // fs-local derives targetKey from realpath, which is how a junction inside
    // either tree gets noticed; the drive spelling itself is not canonicalized.
    const guestRealRoot = await canonicalRoot(workspaceRoot)
    const hostRealRoot = await canonicalRoot(hostWorkspaceRoot)
    const originalResolve = fs.resolve.bind(fs)

    // fs 工具的写权限判定来自 sandboxPolicy.resolve().workspaceRoot（默认是宿主
    // 工作区）。不把它指到会话盘的话，模型写 R:\... 永远会被判成“越界”，
    // 报的却是 DSH 的 workspace-write 拒绝——正是之前 write/edit 一直失败的原因。
    const policy = ctx.sandboxPolicy ?? ctx.get?.('sandboxPolicy')
    if (policy !== undefined && typeof policy.resolve === 'function') {
      const originalPolicyResolve = policy.resolve.bind(policy)
      policy.resolve = (request) => {
        const resolved = originalPolicyResolve(request)
        if (resolved === undefined || resolved === null) return resolved
        return { ...resolved, workspaceRoot }
      }
    }

    async function canonicalRoot(root) {
      if (root === undefined) return undefined
      try {
        return normalizeRoot(await realpath(root))
      } catch {
        return undefined
      }
    }

    function mapTarget(target, requested) {
      const shown = typeof target?.displayPath === 'string' ? target.displayPath : ''
      const real = typeof target?.targetKey === 'string' ? target.targetKey : ''

      // The model addressed the session disk directly. A realpath outside the
      // disk means a junction was planted inside it; never follow host data.
      if (isInsideRoot(guestRoot, shown)) {
        if (real !== '' && normalizeRoot(real) !== normalizeRoot(shown)) {
          // An unresolved canonical root must fail closed: without it there is
          // no way to prove the link target still lives on the session disk.
          if (guestRealRoot === undefined || !isInsideRoot(guestRealRoot, real))
            throw new Error(
              `RVM sandbox: "${shown}" is a link that resolves outside the session disk`)
        }
        return target
      }

      // Host-tree aliases (pnpm junctions, symlinked workspace paths) resolve
      // to their real location; map that suffix so the clone's own copy is used.
      let rel
      if (real !== '') {
        if (hostRealRoot !== undefined && isInsideRoot(hostRealRoot, real))
          rel = relative(hostRealRoot, real)
        else if (isInsideRoot(hostRoot, real))
          rel = relative(hostWorkspaceRoot, real)
      }
      const lexical = isInsideRoot(hostRoot, shown)
      if (rel === undefined && lexical) rel = relative(hostWorkspaceRoot, shown)
      if (rel === undefined)
        throw new Error(
          `RVM sandbox: "${shown || requested}" is outside the workspace ${workspaceRoot}`)

      const mapped = rel === '' || rel === '.'
        ? resolve(workspaceRoot)
        : join(workspaceRoot, rel)
      const shownRel = lexical ? relative(hostWorkspaceRoot, shown) : ''
      const display = shownRel === ''
        ? mapped
        : join(workspaceRoot, shownRel)
      return { targetKey: mapped, displayPath: display }
    }

    fs.resolve = async (path, opts) => {
      const target = await originalResolve(path, opts)
      return mapTarget(target, path)
    }
  }
}