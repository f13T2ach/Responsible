// Responsible 预载插件：经 cordis patch 挂载进 dsh（--profile sdk --patch ...），
// 在不 fork 内核的前提下改写部署 persona，向宿主暴露插件清单（Windows 命名管道 NDJSON），
// 并在 loopback 上开放会话/模型查询接口（默认 1107），供 WPF 与 RWebView 读取持久化数据。
import { createServer } from 'node:net'
import { createServer as createHttpServer } from 'node:http'
import { createInterface } from 'node:readline'
import { randomUUID } from 'node:crypto'
import { promptSections } from './prompts.mjs'

export const name = 'responsible-host'

export const inject = ['systemPrompt']

const PREFERRED_LOCALES = ['zh-cn', 'zh-hans', 'zh']

function localized(value) {
  if (typeof value === 'string') return value
  if (value === null || typeof value !== 'object') return ''
  for (const locale of PREFERRED_LOCALES) {
    if (typeof value[locale] === 'string' && value[locale]) return value[locale]
  }
  return typeof value.en === 'string' ? value.en : ''
}

function toRow(row) {
  return {
    entryId: String(row.entryId),
    moduleName: String(row.moduleName ?? ''),
    title: localized(row.meta?.title),
    description: localized(row.meta?.description),
    enabled: row.enabled === true,
    fiberPhase: row.fiberPhase ?? null,
    diagnostic: typeof row.diagnostic === 'string' ? row.diagnostic : null,
    readOnlyReason: typeof row.readOnlyReason === 'string' ? row.readOnlyReason : null,
    patchId: typeof row.patchId === 'string' ? row.patchId : null,
  }
}

function blockText(content, type) {
  if (!Array.isArray(content)) return ''
  const parts = []
  for (const block of content) {
    if (block !== null && typeof block === 'object' && block.type === type && typeof block.text === 'string')
      parts.push(block.text)
  }
  return parts.join('')
}

// ── 工具调用：分类、摘要与结果文本，口径与 DSH ui-tool 的行模型一致 ──
const TOOL_VARIANTS = {
  bash: 'bash',
  pwsh: 'bash',
  read: 'read',
  read_image: 'read',
  web_fetch: 'read',
  web_search: 'search',
  grep: 'search',
  glob: 'search',
  write: 'write',
  edit: 'edit',
  apply_patch: 'edit',
  run_code: 'code',
  cua_run_code: 'code',
}

const TOOL_SUMMARY_KEYS = {
  bash: ['description', 'command'],
  read: ['path', 'file_path', 'url'],
  search: ['query', 'pattern', 'url'],
  write: ['path', 'file_path'],
  edit: ['path', 'file_path'],
  code: ['description'],
  mcp: ['description'],
  tools: [],
}

const TOOL_OUTPUT_LIMIT = 40000

function toolVariantOf(name) {
  if (typeof name !== 'string' || name === '') return 'tools'
  if (name.startsWith('mcp__')) return 'mcp'
  return TOOL_VARIANTS[name] ?? 'tools'
}

function firstLineOf(text) {
  const nl = text.indexOf('\n')
  return nl === -1 ? text : text.slice(0, nl)
}

function toolSummaryOf(variant, argsRaw) {
  let parsed
  try {
    parsed = typeof argsRaw === 'string' && argsRaw !== '' ? JSON.parse(argsRaw) : undefined
  } catch {
    parsed = undefined
  }
  if (parsed !== null && typeof parsed === 'object') {
    for (const key of TOOL_SUMMARY_KEYS[variant] ?? []) {
      const value = parsed[key]
      if (typeof value === 'string' && value !== '') return firstLineOf(value)
    }
    for (const value of Object.values(parsed)) {
      if (typeof value === 'string' && value !== '') return firstLineOf(value)
    }
  }
  return typeof argsRaw === 'string' && argsRaw !== '' ? firstLineOf(argsRaw) : ''
}

function toolResultText(message) {
  const parts = []
  for (const block of message?.content ?? []) {
    if (block?.type === 'text' && typeof block.text === 'string') parts.push(block.text)
    else if (block !== null && typeof block === 'object') parts.push(JSON.stringify(block, null, 2))
  }
  let text = parts.join('\n')
  if (text.length > TOOL_OUTPUT_LIMIT)
    text = `${text.slice(0, TOOL_OUTPUT_LIMIT)}\n… 已截断，共 ${text.length} 字符`
  return text
}

function toolErrorText(error) {
  if (error === null || typeof error !== 'object') return ''
  const name = typeof error.name === 'string' ? error.name : ''
  const code = typeof error.code === 'string' ? error.code : ''
  if (name && code) return `${name}: ${code}`
  return typeof error.reason === 'string' ? error.reason : name || code
}

function toolCallRecord(event) {
  const data = event?.data ?? {}
  const name = typeof data.name === 'string' ? data.name : ''
  const argsRaw = typeof data.arguments === 'string' ? data.arguments : ''
  const variant = toolVariantOf(name)
  return {
    callId: String(data.callId ?? ''),
    name,
    variant,
    summary: toolSummaryOf(variant, argsRaw),
    input: argsRaw,
    state: 'running',
    output: null,
    isError: false,
    ts: typeof event?.time === 'number' ? event.time : Date.now(),
  }
}

function toolResultRecord(event) {
  const message = event?.data?.message
  const callId = message?.source?.callId
  if (callId === undefined || callId === null) return undefined
  const isError = message?.isError === true
  const output = toolResultText(message) || (isError ? toolErrorText(event?.data?.error) : '')
  return {
    callId: String(callId),
    state: isError ? 'error' : 'ok',
    output: output === '' ? null : output,
    isError,
  }
}

function settleTool(tool, result) {
  tool.state = result.state
  tool.output = result.output
  tool.isError = result.isError
}

// ── 轮次用量：与 @deepseek-ai/dsh-token-meter 的 deriveTurnTokenUsage 同算法 ──
// 只汇总每次计费尝试的权威 usage；生命周期不完整或桶缺报即整轮不可披露。
function isCount(value) {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0
}

function safeSum(values) {
  let total = 0
  for (const value of values) {
    total += value
    if (!Number.isSafeInteger(total)) return undefined
  }
  return total
}

function messageRoute(message) {
  const provider = message?.source?.provider
  const model = message?.source?.model
  return typeof provider === 'string' && provider.length > 0
    && typeof model === 'string' && model.length > 0
    ? { provider, model }
    : undefined
}

function streamUsage(stream) {
  if (!Array.isArray(stream)) return undefined
  for (let index = stream.length - 1; index >= 0; index -= 1) {
    const record = stream[index]
    if (record?.type === 'chunk' && record.chunk?.type === 'usage') return record.chunk.usage
  }
  return undefined
}

function normalizeUsage(usage, route) {
  const { inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, reasoningTokens, totalTokens } = usage
  if (!isCount(inputTokens) || !isCount(outputTokens)) return undefined
  if (cacheReadTokens !== undefined && !isCount(cacheReadTokens)) return undefined
  if (cacheWriteTokens !== undefined && !isCount(cacheWriteTokens)) return undefined
  if (reasoningTokens !== undefined && (!isCount(reasoningTokens) || reasoningTokens > outputTokens))
    return undefined

  const knownPrompt = safeSum([
    inputTokens,
    ...cacheReadTokens === undefined ? [] : [cacheReadTokens],
    ...cacheWriteTokens === undefined ? [] : [cacheWriteTokens],
  ])
  if (knownPrompt === undefined) return undefined

  let exactTotal
  if (totalTokens !== undefined) {
    if (!isCount(totalTokens)) return undefined
    const exactPrompt = totalTokens - outputTokens
    if (!isCount(exactPrompt) || exactPrompt < knownPrompt) return undefined
    if (cacheReadTokens !== undefined && cacheWriteTokens !== undefined && exactPrompt !== knownPrompt)
      return undefined
    exactTotal = totalTokens
  } else {
    if (cacheReadTokens === undefined || cacheWriteTokens === undefined) return undefined
    const derivedTotal = safeSum([knownPrompt, outputTokens])
    if (derivedTotal === undefined) return undefined
    exactTotal = derivedTotal
  }

  return {
    inputTokens,
    outputTokens,
    totalTokens: exactTotal,
    ...cacheReadTokens === undefined ? {} : { cacheReadTokens },
    ...cacheWriteTokens === undefined ? {} : { cacheWriteTokens },
    ...reasoningTokens === undefined ? {} : { reasoningTokens },
    ...route === undefined ? {} : { route },
  }
}

function aggregateAttempts(attempts) {
  if (attempts.length === 0) return undefined
  const inputTokens = safeSum(attempts.map(attempt => attempt.inputTokens))
  const outputTokens = safeSum(attempts.map(attempt => attempt.outputTokens))
  const totalTokens = safeSum(attempts.map(attempt => attempt.totalTokens))
  if (inputTokens === undefined || outputTokens === undefined || totalTokens === undefined)
    return undefined

  const cacheRead = attempts.map(attempt => attempt.cacheReadTokens)
  const cacheWrite = attempts.map(attempt => attempt.cacheWriteTokens)
  const reasoning = attempts.map(attempt => attempt.reasoningTokens)
  const cacheReadTokens = cacheRead.every(isCount) ? safeSum(cacheRead) : undefined
  const cacheWriteTokens = cacheWrite.every(isCount) ? safeSum(cacheWrite) : undefined
  const reasoningTokens = reasoning.every(isCount) ? safeSum(reasoning) : undefined

  let routes
  const attributed = attempts.map(attempt => attempt.route)
  if (attributed.every(route => route !== undefined)) {
    const unique = new Map()
    for (const route of attributed) unique.set(`${route.provider}\0${route.model}`, route)
    routes = [...unique.values()]
  }

  return {
    uncachedInputTokens: inputTokens,
    outputTokens,
    totalTokens,
    ...cacheReadTokens === undefined ? {} : { cacheReadTokens },
    ...cacheWriteTokens === undefined ? {} : { cacheWriteTokens },
    ...reasoningTokens === undefined ? {} : { reasoningTokens },
    ...routes === undefined ? {} : { routes },
  }
}

function deriveTurnUsage(events) {
  let state = { kind: 'idle' }
  const attempts = []
  let turn
  let sawEnd = false
  let invalid = false

  const closeOpen = (route) => {
    if (state.kind !== 'open' || state.sample === undefined) return false
    const normalized = normalizeUsage(state.sample, route)
    if (normalized === undefined) return false
    attempts.push(normalized)
    return true
  }

  for (const event of events) {
    if (invalid) break
    if (event.type === 'turn/start') {
      if (turn !== undefined || state.kind !== 'idle') invalid = true
      else turn = event.data?.turn
      continue
    }
    if (turn === undefined) {
      invalid = true
      break
    }
    if (event.type === 'turn/end') {
      if (event.data?.turn !== turn || state.kind !== 'idle' || sawEnd) invalid = true
      else sawEnd = true
      continue
    }
    if (sawEnd) {
      invalid = true
      break
    }
    if (event.type === 'step/start') {
      if (event.data?.turn !== turn || state.kind !== 'idle') invalid = true
      else state = { kind: 'open', turn, step: event.data.step }
      continue
    }
    if (event.type === 'llm/retry-started') {
      if (event.data?.turn !== turn
        || state.kind !== 'settled'
        || state.by !== 'retry'
        || state.turn !== event.data.turn
        || state.step !== event.data.step) invalid = true
      else state = { kind: 'open', turn, step: event.data.step }
      continue
    }
    if (event.type === 'assistant/attempt') {
      if (event.data?.turn !== turn
        || state.kind !== 'open'
        || state.turn !== event.data.turn
        || state.step !== event.data.step) {
        invalid = true
        continue
      }
      const sample = streamUsage(event.data.stream) ?? state.sample
      state = { kind: 'open', turn, step: event.data.step, ...sample === undefined ? {} : { sample } }
      if (!closeOpen()) invalid = true
      else state = { kind: 'finishClosed', turn, step: event.data.step }
      continue
    }
    if (event.type === 'assistant/message') {
      if (event.data?.turn !== turn
        || state.kind !== 'open'
        || state.turn !== event.data.turn
        || state.step !== event.data.step) {
        invalid = true
        continue
      }
      const sample = event.data.usage ?? streamUsage(event.data.stream)
      if (sample !== undefined) state = { ...state, sample }
      if (!closeOpen(messageRoute(event.data.message))) invalid = true
      else state = { kind: 'settled', turn, step: event.data.step, by: 'message' }
      continue
    }
    if (event.type === 'llm/retry') {
      if (event.data?.turn !== turn || state.kind === 'idle'
        || state.turn !== event.data.turn
        || state.step !== event.data.step) {
        invalid = true
        continue
      }
      if (state.kind === 'settled' || (state.kind === 'open' && !closeOpen())) invalid = true
      if (!invalid) state = { kind: 'settled', turn, step: event.data.step, by: 'retry' }
      continue
    }
    if (event.type === 'step/end') {
      if (event.data?.turn !== turn || state.kind === 'idle'
        || state.turn !== event.data.turn
        || state.step !== event.data.step) {
        invalid = true
        continue
      }
      if (state.kind === 'open' && !closeOpen()) invalid = true
      if (!invalid) state = { kind: 'idle' }
    }
  }

  return invalid || !sawEnd || state.kind !== 'idle' ? undefined : aggregateAttempts(attempts)
}

// 持久化事件 → 前端消息；只保留人类输入与有正文的模型回复，注入上下文不冒充对话。
// 一轮的推理/正文/工具按事件顺序折叠进同一条回复的 parts，与实时流的分段形态一致。
function historyMessages(events) {
  const messages = []
  const turns = []
  let turn = null

  const aggregateFor = (event, source) => {
    if (turn.aggregate === undefined) {
      turn.aggregate = {
        seq: event.seq,
        role: 'assistant',
        text: '',
        reasoning: null,
        ts: event.time,
        model: source?.model ?? null,
        parts: [],
      }
      messages.push(turn.aggregate)
    }
    return turn.aggregate
  }

  const placeTool = (aggregate, tool) => {
    if (tool.callId === '' || turn.placed.has(tool.callId)) return
    turn.placed.add(tool.callId)
    aggregate.parts.push({ kind: 'tool', tool })
  }

  // 工具调用块先于 tool/call 事件落库：先按块建记录，后到的 call/result 都更新同一对象
  const toolForBlock = (event, block) => {
    const callId = String(block?.id ?? '')
    if (callId === '') return undefined
    let tool = turn.toolIndex.get(callId)
    if (tool === undefined) {
      tool = toolCallRecord({
        data: { callId, name: block.name, arguments: block.arguments },
        time: event.time,
      })
      turn.tools.push(tool)
      turn.toolIndex.set(callId, tool)
    }
    return tool
  }

  const finishAggregate = (aggregate) => {
    aggregate.text = aggregate.parts
      .filter(part => part.kind === 'text')
      .map(part => part.text)
      .join('\n\n')
      .trim()
    const reasoning = aggregate.parts
      .filter(part => part.kind === 'reasoning')
      .map(part => part.text)
      .join('\n\n')
      .trim()
    aggregate.reasoning = reasoning === '' ? null : reasoning
    if (turn.tools.length > 0) aggregate.tools = turn.tools
    if (aggregate.parts.length === 0) {
      const at = messages.indexOf(aggregate)
      if (at >= 0) messages.splice(at, 1)
      return undefined
    }
    return aggregate
  }

  const closeTurn = (endEvent) => {
    if (turn === null) return
    const usage = deriveTurnUsage(turn.events)
    for (const tool of turn.tools) if (tool.state === 'running') tool.state = 'stopped'
    const aggregate = turn.aggregate === undefined ? undefined : finishAggregate(turn.aggregate)
    if (aggregate !== undefined) {
      if (usage !== undefined) aggregate.usage = usage
      aggregate.branchSeq = endEvent.seq
    }
    const closing = aggregate !== undefined && aggregate.text !== '' ? aggregate : undefined
    turns.push({ closing, closingSeq: turn.lastReplySeq, latestTranscriptSeq: turn.latestTranscriptSeq })
    turn = null
  }

  for (const event of events ?? []) {
    if (event?.type === 'turn/start') {
      turn = {
        events: [event],
        tools: [],
        toolIndex: new Map(),
        placed: new Set(),
        aggregate: undefined,
        lastReplySeq: undefined,
        latestTranscriptSeq: undefined,
      }
      continue
    }
    if (turn !== null) {
      turn.events.push(event)
      const seq = typeof event.seq === 'number' ? event.seq : undefined
      const later = event.type === 'tool/call'
        || (event.type === 'tool/result' && event.surfaceOp === 'append')
        || (event.type === 'turn/end' && event.data?.reason?.kind === 'error')
        || event.type === 'llm/retry'
      if (later && seq !== undefined
        && (turn.latestTranscriptSeq === undefined || seq > turn.latestTranscriptSeq))
        turn.latestTranscriptSeq = seq
    }

    if (event?.type === 'tool/call' && turn !== null) {
      const record = toolCallRecord(event)
      if (record.callId !== '') {
        // assistant/message 的工具块通常先到；重复的 tool/call 只复用记录，不重复挂到 parts/tools
        let tool = turn.toolIndex.get(record.callId)
        if (tool === undefined) {
          tool = record
          turn.tools.push(tool)
          turn.toolIndex.set(tool.callId, tool)
        }
        placeTool(aggregateFor(event), tool)
      }
    } else if (event?.type === 'tool/result' && event.surfaceOp === 'append' && turn !== null) {
      const result = toolResultRecord(event)
      const tool = result === undefined ? undefined : turn.toolIndex.get(result.callId)
      if (tool !== undefined && result !== undefined) settleTool(tool, result)
    }

    if (event?.type === 'user/message') {
      if (event.data?.source?.kind !== 'user') continue
      const text = blockText(event.data?.content, 'text').trim()
      if (!text) continue
      messages.push({ seq: event.seq, role: 'user', text, reasoning: null, ts: event.time, model: null })
    } else if (event?.type === 'assistant/message' && turn !== null) {
      const data = event.data ?? {}
      const content = Array.isArray(data.message?.content) ? data.message.content : []
      const blocks = content.filter(block => block !== null && typeof block === 'object')
      const visible = blocks.some(block => block.type === 'tool-call'
        || ((block.type === 'text' || block.type === 'reasoning')
          && typeof block.text === 'string' && block.text.trim() !== ''))
      if (!visible) continue
      const aggregate = aggregateFor(event, data.message?.source)
      if (typeof data.message?.source?.model === 'string') aggregate.model = data.message.source.model
      let hasText = false
      for (const block of blocks) {
        if (block.type === 'reasoning' && typeof block.text === 'string' && block.text.trim() !== '') {
          aggregate.parts.push({ kind: 'reasoning', text: block.text.trim() })
        } else if (block.type === 'text' && typeof block.text === 'string' && block.text.trim() !== '') {
          aggregate.parts.push({ kind: 'text', text: block.text.trim() })
          hasText = true
        } else if (block.type === 'tool-call') {
          const tool = toolForBlock(event, block)
          if (tool !== undefined) placeTool(aggregate, tool)
        }
      }
      // DSH turn-tail：latestTranscriptSeq 以最后一条 finalized assistant 为基线，再被工具/错误/重试事件抬高
      turn.latestTranscriptSeq = event.seq
      if (hasText) turn.lastReplySeq = event.seq
    }

    if (event?.type === 'turn/end') closeTurn(event)
  }

  // 未收盘的轮次（恢复 live 会话时可能仍在运行）：保留 running 工具，后续 result 帧按 callId 收尾。
  if (turn !== null && turn.aggregate !== undefined) finishAggregate(turn.aggregate)

  // 与 DSH turn-tail 同义：只有“本轮最后一条 finalized 消息且有正文”时可分支；
  // 其它轮次（包括更早的已完成轮次）同样可分支，边界就是各自的 turn/end。
  for (const entry of turns) {
    if (entry.closing === undefined) continue
    entry.closing.branchUnavailable = entry.latestTranscriptSeq !== entry.closingSeq
  }
  return messages
}

async function listSessions(ctx) {
  const query = ctx.get('sessionQuery')
  if (query === undefined) throw new Error('sessionQuery service is unavailable')
  const records = await query.listSessions()
  const visible = records
    .filter(record => record?.header?.origin !== 'subagent')
    .sort((left, right) => (right.header.createdAt ?? 0) - (left.header.createdAt ?? 0))
    .slice(0, 200)
  const sessions = await Promise.all(visible.map(async (record, index) => {
    const header = record.header
    let title = ''
    let updatedAt = header.createdAt ?? 0
    try {
      const snapshot = await query.readTitle(header.id)
      if (snapshot !== undefined) {
        title = snapshot.title
        updatedAt = Math.max(updatedAt, snapshot.updatedAt ?? 0)
      }
    } catch {
      // 损坏或不可迁移的历史不阻塞其它会话的列表
    }
    return {
      sessionId: String(header.id),
      title: title || `对话 ${index + 1}`,
      cwd: typeof header.cwd === 'string' ? header.cwd : null,
      createdAt: header.createdAt ?? 0,
      updatedAt,
      live: record.live === true,
    }
  }))
  sessions.sort((left, right) => right.updatedAt - left.updatedAt)
  return sessions
}

async function sessionHistory(ctx, sessionId) {
  const query = ctx.get('sessionQuery')
  if (query === undefined) throw new Error('sessionQuery service is unavailable')
  const snapshot = await query.readSession(sessionId)
  return {
    sessionId,
    cwd: typeof snapshot.session?.cwd === 'string' ? snapshot.session.cwd : null,
    messages: historyMessages(snapshot.events),
  }
}

// 从源会话继承分支：在 DSH 侧用 seed + parentSource 复制事件前缀，落盘后立刻释放句柄，
// 让宿主的 session/resume 能按普通持久化会话重新打开；子会话与父会话同 cwd、相邻展示。
function forkRoute(ctx, events) {
  const defaults = ctx.get('agentDefaultModel')
  const selection = defaults?.currentSelection?.()
  if (typeof selection?.provider === 'string' && typeof selection?.model === 'string')
    return { provider: selection.provider, model: selection.model }
  for (let index = events.length - 1; index >= 0; index -= 1) {
    const data = events[index]?.data
    const source = data?.message?.source ?? data?.selection ?? data
    if (typeof source?.provider === 'string' && typeof source?.model === 'string')
      return { provider: source.provider, model: source.model }
  }
  return undefined
}

async function forkSession(ctx, sessionId, atSeq) {
  const query = ctx.get('sessionQuery')
  const agents = ctx.get('agents')
  if (query === undefined) throw new Error('sessionQuery service is unavailable')
  if (agents === undefined) throw new Error('agents service is unavailable')
  if (!Number.isSafeInteger(atSeq) || atSeq < 0) throw new Error('atSeq 必须是非负整数')

  const observed = await query.observeSession(sessionId, { projectionMode: 'none' })
  try {
    const events = observed.events
    const boundary = events.findIndex(event => event.seq === atSeq)
    if (boundary < 0) throw new Error(`事件 ${atSeq} 不存在于会话 ${sessionId}`)
    const endEvent = events[boundary]
    if (endEvent.type !== 'turn/end') throw new Error('只能从已完成的轮次创建分支')

    const seed = events.slice(0, boundary + 1)
    seed.push({
      type: 'session/end-seed',
      seq: atSeq + 1,
      time: endEvent.time,
      data: { inherited: true },
    })
    const childId = randomUUID().replaceAll('-', '')
    const route = forkRoute(ctx, events)
    const handle = await agents.create({
      sessionId: childId,
      seed,
      inheritedEventCount: atSeq + 1,
      parentSource: {
        sessionId,
        endSeq: Math.min(atSeq + 1, observed.persistedEventCount ?? atSeq + 1),
      },
      meta: {
        ...(typeof observed.header.cwd === 'string' ? { cwd: observed.header.cwd } : {}),
        isSeeded: true,
      },
      ...route === undefined ? {} : { agentOptions: route },
    })
    try {
      await handle.dispose()
    } catch (error) {
      process.stderr.write(`[responsible-host] fork dispose failed: ${error?.message ?? error}\n`)
    }
    return { sessionId: childId, forkSeq: atSeq }
  } finally {
    observed[Symbol.dispose]?.()
  }
}

async function listModels(ctx) {
  const llm = ctx.get('llm')
  if (llm === undefined) throw new Error('llm service is unavailable')
  const providers = []
  const models = []
  for (const provider of llm.listProviders()) {
    providers.push({ id: String(provider.id), name: String(provider.name ?? provider.id) })
    try {
      const catalog = await llm.listModels(provider.id)
      for (const model of catalog ?? []) {
        models.push({
          providerId: String(provider.id),
          providerName: String(provider.name ?? provider.id),
          modelId: String(model.id),
          modelName: String(model.name ?? model.id),
        })
      }
    } catch {
      // 单个 provider 的发现失败不影响其它 provider
    }
  }
  return { providers, models }
}

// 统计全部来自 DSH 自身的投影单元：sessionStats（轮次/步数/decode 计时与 token）、
// tokenUsage（整段日志的输入/输出/缓存桶）、contextPressure（最近一次请求的上下文占用）。
const STAT_KEYS = ['sessionStats', 'tokenUsage', 'contextPressure']

// 当前会话的权限投影：沙箱模式 + 审批策略折叠出的 preset 当前值。
const PERMISSION_KEYS = ['permissions']

// 审批问询的中转：把 DSH 的 approval/request 挂起，等宿主通过 HTTP 回答。
// 超时、宿主没回答、答案不在词表里，一律按 unavailable 收场（fail-closed）。
// 迁移自 DSH 的 approval 语义：allowed-once 是一次性授权，没有“永远允许”。
const APPROVAL_OUTCOMES = ['allowed-once', 'rejected', 'cancelled', 'unavailable']
const APPROVAL_TIMEOUT_MS = 120_000

function createApprovalBroker() {
  const waiting = new Map()
  let seq = 0
  return {
    list() {
      return [...waiting.values()].map(item => ({ ...item.summary }))
    },
    answer(id, outcome) {
      const entry = waiting.get(id)
      if (entry === undefined || !APPROVAL_OUTCOMES.includes(outcome)) return false
      waiting.delete(id)
      entry.settle(outcome)
      return true
    },
    ask(request) {
      const id = `a${++seq}`
      process.stderr.write(`[approval] ask id=${id} tool=${request.toolName} reason=${request.reason ?? ""}\n`)
      const reason = typeof request.reason === 'string' && request.reason ? request.reason : undefined
      const summary = {
        id,
        toolName: String(request.toolName ?? ''),
        ...(request.callId === undefined ? {} : { callId: String(request.callId) }),
        ...(reason === undefined ? {} : { reason }),
        sessionId: String(request.agent?.session?.id ?? ''),
        // 只有要绕开沙箱的请求才算危险，普通越界写按提示处理
        dangerous: /danger|full-access|escalat/i.test(reason ?? ''),
      }
      return new Promise((resolve) => {
        let timer
        const finish = (outcome) => {
          clearTimeout(timer)
          try {
            request.signal?.removeEventListener?.('abort', onAbort)
          } catch {
            // signal 不一定实现 removeEventListener
          }
          resolve(outcome)
        }
        const onAbort = () => {
          if (waiting.delete(id)) finish('cancelled')
        }
        timer = setTimeout(() => {
          if (waiting.delete(id)) finish('unavailable')
        }, APPROVAL_TIMEOUT_MS)
        try {
          request.signal?.addEventListener?.('abort', onAbort, { once: true })
        } catch {
          // 同上：拿不到 signal 时只靠超时兜底
        }
        waiting.set(id, { summary, settle: finish })
      })
    },
  }
}

// 与 dsh-client-ui-stats 的 totalsFromProjections 同口径，只做展示前的换算，不做任何估算
function statsFrom(values) {
  const sessionStats = values?.sessionStats
  const usage = values?.tokenUsage
  const pressure = values?.contextPressure
  const contextTokens = usage === undefined
    ? 0
    : usage.uncachedInputTokens + usage.cacheReadTokens + usage.cacheWriteTokens
  const outputTokens = usage?.outputTokens ?? 0
  return {
    turns: sessionStats?.turns ?? 0,
    steps: sessionStats?.steps ?? 0,
    llmMs: sessionStats?.llmMs ?? 0,
    toolMs: sessionStats?.toolMs ?? 0,
    ttftMs: sessionStats?.ttftMs ?? 0,
    ttftSteps: sessionStats?.ttftSteps ?? 0,
    decodeMs: sessionStats?.decodeMs ?? 0,
    decodeTokens: sessionStats?.decodeTokens ?? 0,
    tokensPerSecond: sessionStats !== undefined && sessionStats.decodeMs > 0
      ? sessionStats.decodeTokens / (sessionStats.decodeMs / 1000)
      : 0,
    inputTokens: usage?.uncachedInputTokens ?? 0,
    outputTokens,
    cacheReadTokens: usage?.cacheReadTokens ?? 0,
    cacheWriteTokens: usage?.cacheWriteTokens ?? 0,
    totalTokens: contextTokens + outputTokens,
    cacheHitRate: contextTokens > 0 ? Math.min(1, usage.cacheReadTokens / contextTokens) : 0,
    pressureTokens: pressure?.pressureTokens ?? null,
    contextWindow: pressure?.contextWindow ?? null,
  }
}

function projectStats(ctx, session, keys) {
  const projections = ctx.get('sessionProjections')
  if (projections === undefined) return null
  const { values } = projections.snapshot(session, keys ?? STAT_KEYS)
  return statsFrom(values)
}

// 冷会话没有 live Session 可读：用投影缓存的 checkpoint，缺行时按 DSH 单元整段回放
async function sessionStatsOf(ctx, sessionId) {
  const query = ctx.get('sessionQuery')
  const cache = ctx.get('sessionProjectionCache')
  if (query === undefined || cache === undefined) return null
  const snapshot = await query.readSession(sessionId)
  try {
    return statsFrom(cache.coldSnapshot(snapshot.session, snapshot.inheritedEventCount, snapshot.events).values)
  } catch {
    const cached = cache.cachedSnapshot(snapshot.session, STAT_KEYS)
    return cached === undefined ? null : statsFrom(cached.values)
  }
}

// 进程级权限目录：preset 表与默认项全部来自 DSH 的 permission-presets 配置，宿主不另外造选项。
function permissionCatalogOf(ctx) {
  const presets = ctx.get('permissionPresets')
  if (presets === undefined) return null
  const catalog = presets.catalog()
  const optionOf = option => ({
    value: String(option.value),
    name: String(option.name ?? option.value),
    ...option.description === undefined ? {} : { description: String(option.description) },
  })
  return {
    options: (catalog.options ?? []).map(optionOf),
    defaultPreset: String(catalog.defaultPreset ?? ''),
  }
}

// 当前会话的权限值：live 会话读投影；冷会话按与统计相同的 checkpoint/回放路径折叠。
async function permissionValueOf(ctx, sessionId) {
  const projections = ctx.get('sessionProjections')
  if (projections === undefined) return null
  const sessions = ctx.get('sessions')
  const live = sessions?.get(sessionId)
  if (live !== undefined) {
    try {
      const { values } = projections.snapshot(live, PERMISSION_KEYS)
      return values.permissions?.currentValue ?? null
    } catch {
      return null
    }
  }
  const query = ctx.get('sessionQuery')
  const cache = ctx.get('sessionProjectionCache')
  if (query === undefined || cache === undefined) return null
  const snapshot = await query.readSession(sessionId)
  try {
    const { values } = cache.coldSnapshot(snapshot.session, snapshot.inheritedEventCount, snapshot.events)
    return values.permissions?.currentValue ?? null
  } catch {
    return null
  }
}

// 与 permissionPresets.set Remote 同一写入路径：只接受本进程持有的 live 会话。
function setPermission(ctx, sessionId, preset) {
  const presets = ctx.get('permissionPresets')
  if (presets === undefined) throw new Error('permissionPresets service is unavailable')
  if (typeof preset !== 'string' || preset.length === 0) throw new Error('preset is required')
  const sessions = ctx.get('sessions')
  const session = sessions?.get(sessionId)
  if (session === undefined) throw new Error(`permission: no live session "${sessionId}"`)
  presets.set(session, preset)
  return { sessionId, currentValue: presets.current(session) }
}

async function setCredential(ctx, reference, value) {
  const credentials = ctx.get('credentials')
  if (credentials === undefined) throw new Error('credentials service is unavailable')
  if (typeof reference !== 'string' || reference.length === 0) throw new Error('credential reference is required')
  if (typeof value !== 'string' || value.length === 0) throw new Error('credential value is required')
  await credentials.set(reference, value)
  return { stored: true }
}

async function handleOperation(ctx, op, payload = {}) {
  switch (op) {
    case 'list': {
      const manager = ctx.get('pluginManager')
      if (manager === undefined || typeof manager.listPlugins !== 'function')
        throw new Error('pluginManager service is unavailable')
      return (await manager.listPlugins()).map(toRow)
    }
    case 'sessions.list':
      return await listSessions(ctx)
    case 'sessions.history': {
      const sessionId = String(payload.sessionId ?? '')
      if (!sessionId) throw new Error('sessionId is required')
      return await sessionHistory(ctx, sessionId)
    }
    case 'sessions.fork': {
      const sessionId = String(payload.sessionId ?? '')
      if (!sessionId) throw new Error('sessionId is required')
      return await forkSession(ctx, sessionId, Number(payload.atSeq))
    }
    case 'models.list':
      return await listModels(ctx)
    case 'sessions.stats': {
      const sessionId = String(payload.sessionId ?? '')
      if (!sessionId) throw new Error('sessionId is required')
      return { sessionId, stats: await sessionStatsOf(ctx, sessionId) }
    }
    case 'permissions.catalog':
      return { catalog: permissionCatalogOf(ctx) }
    case 'permissions.get': {
      const sessionId = String(payload.sessionId ?? '')
      if (!sessionId) throw new Error('sessionId is required')
      return { sessionId, currentValue: await permissionValueOf(ctx, sessionId) }
    }
    case 'permissions.set': {
      const sessionId = String(payload.sessionId ?? '')
      if (!sessionId) throw new Error('sessionId is required')
      return setPermission(ctx, sessionId, payload.preset)
    }
    case 'credentials.set':
      return await setCredential(ctx, payload.ref, payload.value)
    default:
      throw new Error(`unsupported op: ${String(op)}`)
  }
}

function startBridge(ctx, pipePath, token) {
  const sockets = new Set()

  async function handle(socket, line) {
    let id = null
    let response
    try {
      const request = JSON.parse(line)
      id = request?.id ?? null
      if (request?.token !== token) {
        response = { id, ok: false, error: { code: 'unauthorized', message: 'invalid bridge token' } }
      } else {
        response = { id, ok: true, data: await handleOperation(ctx, request?.op, request) }
      }
    } catch (error) {
      response = { id, ok: false, error: { code: 'bridge-failure', message: String(error?.message ?? error) } }
    }
    if (!socket.destroyed) socket.write(`${JSON.stringify(response)}\n`)
  }

  const server = createServer((socket) => {
    sockets.add(socket)
    socket.setEncoding('utf8')
    socket.on('close', () => sockets.delete(socket))
    socket.on('error', () => sockets.delete(socket))
    const lines = createInterface({ input: socket, crlfDelay: Infinity })
    lines.on('line', (line) => { void handle(socket, line) })
  })
  server.on('error', (error) => {
    process.stderr.write(`[responsible-host] pipe bridge failed on ${pipePath}: ${error?.message ?? error}\n`)
  })

  server.listen(pipePath)
  ctx.effect(() => () => {
    server.close()
    for (const socket of sockets) sockets.destroy()
    sockets.clear()
  })
}

function sendJson(response, status, body) {
  const text = JSON.stringify(body)
  response.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(text),
    'cache-control': 'no-store',
  })
  response.end(text)
}

// 端口接口只服务本机宿主：loopback 绑定 + 启动时生成的 token，避免同机其它进程冒读会话。
import { readdir, stat } from 'node:fs/promises'
import { join } from 'node:path'

// 侧栏「工作区文件」：列会话工作区根目录（不递归），条目数封顶
async function listWorkspaceFiles(ctx, sessionId) {
  const query = ctx.get('sessionQuery')
  if (query === undefined) throw new Error('sessionQuery service is unavailable')
  const rows = await listSessions(ctx)
  const row = rows.find(item => item.sessionId === sessionId)
  const cwd = row?.cwd
  if (typeof cwd !== 'string' || !cwd) return { cwd: null, entries: [] }
  let names
  try {
    names = await readdir(cwd, { withFileTypes: true })
  } catch (error) {
    return { cwd, entries: [], error: String(error?.message ?? error) }
  }
  const entries = []
  for (const dirent of names.slice(0, 300)) {
    if (dirent.name === '.git' || dirent.name === '.rvm_temp') continue
    let size = 0
    try {
      const info = await stat(join(cwd, dirent.name))
      size = info.size
    } catch {
      // 读不到大小不影响列出名字
    }
    entries.push({ name: dirent.name, dir: dirent.isDirectory(), size })
  }
  entries.sort((left, right) => Number(right.dir) - Number(left.dir) || left.name.localeCompare(right.name))
  return { cwd, entries }
}
function startHttpBridge(ctx, port, token, approvals) {
  const server = createHttpServer((request, response) => {
    void (async () => {
      const authorized = request.headers['x-responsible-token'] === token
      if (!authorized) {
        sendJson(response, 401, { ok: false, error: 'unauthorized' })
        return
      }
      const url = new URL(request.url ?? '/', 'http://127.0.0.1')
      try {
        if (request.method === 'GET' && url.pathname === '/health') {
          sendJson(response, 200, { ok: true })
        } else if (request.method === 'GET' && url.pathname === '/sessions') {
          sendJson(response, 200, { ok: true, sessions: await listSessions(ctx) })
        } else if (request.method === 'GET' && /^\/sessions\/[^/]+\/history$/.test(url.pathname)) {
          const sessionId = decodeURIComponent(url.pathname.split('/')[2])
          sendJson(response, 200, { ok: true, ...(await sessionHistory(ctx, sessionId)) })
        } else if (request.method === 'GET' && /^\/sessions\/[^/]+\/stats$/.test(url.pathname)) {
          const sessionId = decodeURIComponent(url.pathname.split('/')[2])
          sendJson(response, 200, { ok: true, sessionId, stats: await sessionStatsOf(ctx, sessionId) })
        } else if (request.method === 'POST' && /^\/sessions\/[^/]+\/fork$/.test(url.pathname)) {
          const sessionId = decodeURIComponent(url.pathname.split('/')[2])
          let body = ''
          for await (const chunk of request) body += chunk
          const parsed = body ? JSON.parse(body) : {}
          sendJson(response, 200, { ok: true, ...(await forkSession(ctx, sessionId, Number(parsed.atSeq))) })
        } else if (request.method === 'GET' && url.pathname === '/models') {
          sendJson(response, 200, { ok: true, ...(await listModels(ctx)) })
        } else if (request.method === 'GET' && url.pathname === '/permissions') {
          sendJson(response, 200, { ok: true, catalog: permissionCatalogOf(ctx) })
        } else if (request.method === 'GET' && /^\/permissions\/[^/]+$/.test(url.pathname)) {
          const sessionId = decodeURIComponent(url.pathname.split('/')[2])
          sendJson(response, 200, { ok: true, sessionId, currentValue: await permissionValueOf(ctx, sessionId) })
        } else if (request.method === 'POST' && /^\/permissions\/[^/]+$/.test(url.pathname)) {
          const sessionId = decodeURIComponent(url.pathname.split('/')[2])
          let body = ''
          for await (const chunk of request) body += chunk
          const parsed = body ? JSON.parse(body) : {}
          sendJson(response, 200, { ok: true, ...setPermission(ctx, sessionId, parsed.preset) })
        } else if (request.method === 'GET' && /^\/files\/[^/]+$/.test(url.pathname)) {
          const sessionId = decodeURIComponent(url.pathname.split('/')[2])
          sendJson(response, 200, { ok: true, ...(await listWorkspaceFiles(ctx, sessionId)) })
        } else if (request.method === 'GET' && url.pathname === '/approvals') {
          sendJson(response, 200, { ok: true, approvals: approvals.list() })
        } else if (request.method === 'POST' && /^\/approvals\/[^/]+$/.test(url.pathname)) {
          const id = decodeURIComponent(url.pathname.split('/')[2])
          let body = ''
          for await (const chunk of request) body += chunk
          const parsed = body ? JSON.parse(body) : {}
          const accepted = approvals.answer(id, parsed.outcome)
          sendJson(response, accepted ? 200 : 409, { ok: accepted })
        } else if (request.method === 'PUT' && url.pathname === '/credentials') {
          let body = ''
          for await (const chunk of request) body += chunk
          const parsed = body ? JSON.parse(body) : {}
          sendJson(response, 200, { ok: true, ...(await setCredential(ctx, parsed.ref, parsed.value)) })
        } else {
          sendJson(response, 404, { ok: false, error: 'not-found' })
        }
      } catch (error) {
        sendJson(response, 500, { ok: false, error: String(error?.message ?? error) })
      }
    })()
  })
  server.on('error', (error) => {
    process.stderr.write(`[responsible-host] HTTP bridge failed on 127.0.0.1:${port}: ${error?.message ?? error}\n`)
  })

  server.listen(port, '127.0.0.1')
  ctx.effect(() => () => {
    server.close()
    server.closeAllConnections()
  })
}

// assistant 流帧只存在于进程内（agent/assistant-stream），SDK stdio 不发；单独开一条管道推送。
// 同一管道也承载 DSH 投影统计帧（type: stats）。
function startStreamBridge(ctx, pipePath) {
  const sockets = new Set()
  const server = createServer((socket) => {
    sockets.add(socket)
    socket.on('close', () => sockets.delete(socket))
    socket.on('error', () => sockets.delete(socket))
  })
  server.on('error', (error) => {
    process.stderr.write(`[responsible-host] stream bridge failed on ${pipePath}: ${error?.message ?? error}\n`)
  })
  server.listen(pipePath)
  ctx.effect(() => () => {
    server.close()
    for (const socket of sockets) socket.destroy()
    sockets.clear()
  })

  function write(payload) {
    if (sockets.size === 0) return
    const line = `${JSON.stringify(payload)}\n`
    for (const socket of sockets)
      if (!socket.destroyed) socket.write(line)
  }

  return {
    assistant(sessionId, frame) {
      if (frame?.type === 'start') {
        write({
          sessionId,
          type: 'start',
          attemptId: String(frame.attemptId),
          revision: frame.revision,
        })
      } else if (frame?.type === 'chunk'
        && (frame.chunk?.type === 'text-delta' || frame.chunk?.type === 'reasoning-delta')) {
        write({
          sessionId,
          type: 'chunk',
          attemptId: String(frame.attemptId),
          revision: frame.revision,
          index: frame.index,
          chunk: { type: frame.chunk.type, text: frame.chunk.text },
        })
      }
    },
    stats(sessionId, stats) {
      write({ sessionId, type: 'stats', stats })
    },
    permissions(sessionId, currentValue) {
      write({ sessionId, type: 'permissions', currentValue })
    },
    turn(sessionId, turn, seq, usage) {
      write({ sessionId, type: 'turn', turn, seq, usage })
    },
    tool(sessionId, frame) {
      write({ sessionId, type: 'tool', ...frame })
    },
  }
}

export function apply(ctx, config = {}) {
  const persona = promptSections.host
  ctx.systemPrompt.section({
    name: persona.name,
    order: persona.order,
    interpolate: persona.interpolate,
    text: config.text ?? persona.text,
  })

  const token = typeof config.bridgeToken === 'string' ? config.bridgeToken : ''

  if (typeof config.bridgePipe === 'string' && config.bridgePipe && token) {
    startBridge(ctx, config.bridgePipe, token)
    const stream = startStreamBridge(ctx, `${config.bridgePipe}-events`)
    ctx.on('agent/assistant-stream', ({ agent, frame }) => {
      stream.assistant(String(agent.session.id), frame)
    }, { global: true })
    // 实时轮次用量：turn/end 落定后用同一折叠算法算出本轮账单，推给宿主补到收盘回复上
    const openTurns = new Map()
    ctx.on('session/event', (session, event) => {
      const sessionId = String(session.id)
      if (event.type === 'tool/call') {
        const call = toolCallRecord(event)
        stream.tool(sessionId, { phase: 'call', ...call })
        return
      }
      if (event.type === 'tool/result' && event.surfaceOp === 'append') {
        const result = toolResultRecord(event)
        if (result !== undefined) stream.tool(sessionId, { phase: 'result', ...result })
        return
      }
      if (event.type === 'turn/start') {
        openTurns.set(sessionId, [event])
        return
      }
      const events = openTurns.get(sessionId)
      if (events === undefined) return
      events.push(event)
      if (event.type !== 'turn/end') return
      openTurns.delete(sessionId)
      const usage = deriveTurnUsage(events)
      if (usage !== undefined)
        stream.turn(sessionId, Number(event.data?.turn ?? 0), Number(event.seq), usage)
    }, { global: true })
    // 投影变更即推送整块统计：与 DSH Web 端读取的是同一份 sessionStats/tokenUsage/contextPressure
    ctx.inject(['sessionProjections'], (scope) => {
      scope.sessionProjections.onEventChanged((session, changes) => {
        if (changes.some(change => PERMISSION_KEYS.includes(change.key))) {
          try {
            const presets = scope.get('permissionPresets')
            if (presets !== undefined) stream.permissions(String(session.id), presets.current(session))
          } catch {
            // 投影/服务尚未就绪时跳过这一帧，宿主仍可拉取
          }
        }
        if (!changes.some(change => STAT_KEYS.includes(change.key))) return
        try {
          const stats = projectStats(scope, session)
          if (stats !== null) stream.stats(String(session.id), stats)
        } catch {
          // 单个会话的投影读取失败不阻塞流管道
        }
      })
    })
  }

  if (Number.isInteger(config.bridgePort) && config.bridgePort > 0 && config.bridgePort < 65536 && token) {
    const approvals = createApprovalBroker()
    // 有宿主可问时才接管；否则交回 DSH 自己的答案链（仍然 fail-closed）
    process.stderr.write("[approval] answerer registered\n")
    ctx.on('approval/request', (request, next) => approvals.ask(request), { global: true })
    startHttpBridge(ctx, config.bridgePort, token, approvals)
  }
}
