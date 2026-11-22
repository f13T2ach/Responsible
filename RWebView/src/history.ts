import type { WireChanges, WireMessage, WirePart, WireTool, WireUsage } from "./bridge";
import { renderMarkdown, type CodeBlock } from "./markdown";
import { getLocale, t } from "./i18n";
import { TurnRail, type TurnSource } from "./turn-rail";

export const icons = {
  sparkle:
    '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z"/><path d="M18.5 15.5l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8z"/></svg>',
  sparkleBig:
    '<svg width="36" height="36" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z"/><path d="M18.5 15.5l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8z"/></svg>',
  copy:
    '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/></svg>',
  branch:
    '<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.1" stroke-linecap="round" stroke-linejoin="round"><path d="M1.015 8h4.681c.723 0 1.084 0 1.425-.08.302-.072.591-.19.857-.35.3-.18.558-.433 1.075-.938l2.275-2.226"/><path d="M1.012 8h4.684c.723 0 1.084 0 1.425.08.302.072.591.19.857.35.3.181.558.433 1.075.939l2.275 2.225"/><circle cx="12.45" cy="3.308" r="1.57"/><circle cx="12.45" cy="12.692" r="1.57"/></svg>',
  database:
    '<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.1" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="8" cy="3.828" rx="6" ry="2.717"/><path d="M2 3.804v7.981"/><path d="M14 3.804v7.981"/><path d="M2 7.814c0 .791.632 1.55 1.757 2.11C4.883 10.483 6.409 10.797 8 10.797s3.117-.314 4.243-.874C13.368 9.364 14 8.605 14 7.814"/><path d="M2 11.785c0 .823.632 1.613 1.757 2.195C4.883 14.562 6.409 14.889 8 14.889s3.117-.327 4.243-.909C13.368 13.398 14 12.608 14 11.785"/></svg>',
  stop:
    '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"><rect x="6" y="6" width="12" height="12" rx="2"/></svg>',
  down:
    '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 4v16"/><path d="M6 14l6 6 6-6"/></svg>',
  check:
    '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12.5l5 5L20 6.5"/></svg>',
  gauge:
    '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M12 15l3.5-5.5"/><path d="M4 19a9 9 0 1 1 16 0"/></svg>',
  chevron:
    '<svg class="chev" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M9 5l7 7-7 7"/></svg>',
  file:
    '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/></svg>',
  terminal:
    '<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M2 4l3.5 4L2 12"/><path d="M7.5 12.5h6"/></svg>',
  fileRead:
    '<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 1.5H4a1 1 0 0 0-1 1v11a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1V5.5z"/><path d="M9 1.5v4h4"/><path d="M5 8.5h6"/><path d="M5 11h4"/></svg>',
  pencil:
    '<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"><path d="M2 14l.9-3.5L11 2.4a1.3 1.3 0 0 1 1.9 0l.7.7a1.3 1.3 0 0 1 0 1.9L5.5 13.1z"/><path d="M9.6 3.8l2.6 2.6"/></svg>',
  fileWrite:
    '<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 1.5H4a1 1 0 0 0-1 1v11a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1V5.5z"/><path d="M9 1.5v4h4"/><path d="M8 7.5v4"/><path d="M6.2 9.8L8 11.6l1.8-1.8"/></svg>',
  searchIcon:
    '<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><circle cx="7" cy="7" r="4.2"/><path d="M10.2 10.2L14 14"/></svg>',
  code:
    '<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5.5 4L1.8 8l3.7 4"/><path d="M10.5 4l3.7 4-3.7 4"/></svg>',
  plug:
    '<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"><path d="M5.5 1.5v3"/><path d="M10.5 1.5v3"/><path d="M3.5 4.5h9v3a4.5 4.5 0 0 1-9 0z"/><path d="M8 12v2.5"/></svg>',
  wrench:
    '<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"><path d="M10.3 2.2a3.5 3.5 0 0 0-3.4 5.1L2.5 11.7V13.5h1.8l4.4-4.4a3.5 3.5 0 1 0 1.6-6.9z"/><circle cx="10.3" cy="5.7" r="1"/></svg>',
  alert:
    '<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M8 2.5L14.5 14h-13z"/><path d="M8 6.5v3.2"/><path d="M8 11.6h.01"/></svg>',
};

const toolLabelKeys: Record<string, string> = {
  bash: "tool.bash",
  read: "tool.read",
  search: "tool.search",
  write: "tool.write",
  edit: "tool.edit",
  code: "tool.code",
  mcp: "tool.mcp",
  tools: "tool.tools",
};

const toolLabel = (variant: string): string => t(toolLabelKeys[variant] ?? "tool.tools");

const toolIcons: Record<string, string> = {
  bash: icons.terminal,
  read: icons.fileRead,
  search: icons.searchIcon,
  write: icons.fileWrite,
  edit: icons.pencil,
  code: icons.code,
  mcp: icons.plug,
  tools: icons.wrench,
};

const toolStatusKeys: Record<string, string> = {
  running: "tool.running",
  error: "tool.error",
  stopped: "tool.stopped",
};

const mcpDisplayName = (name: string): string =>
  name.replace(/^mcp__/, "").split("__").filter(Boolean).join(" · ");

const prettyInput = (raw: string): string => {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
};

const timeText = (ts: number): string =>
  new Date(ts).toLocaleTimeString(getLocale(), { hour12: false, hour: "2-digit", minute: "2-digit" });

const scaled = (value: number): string =>
  value >= 100 ? String(Math.round(value)) : String(Math.round(value * 10) / 10);

// 与 DSH token-format 同口径：517 / 12.2K / 517K / 1.2M
const compactTokens = (value: number): string => {
  if (value < 1_000) return String(value);
  if (value < 1_000_000) return `${scaled(value / 1_000)}K`;
  return `${scaled(value / 1_000_000)}M`;
};

const exactTokens = (value: number): string => {
  const digits = String(Math.max(0, Math.trunc(value)));
  let text = "";
  for (let end = digits.length, first = true; end > 0; end -= 3, first = false) {
    const group = digits.slice(Math.max(0, end - 3), end);
    text = first ? group : `${group},${text}`;
  }
  return text;
};

// 部分命中不得四舍五入成 100%：截断到 0.1%
const cacheHitPercent = (cacheRead: number, prompt: number): string | null => {
  if (prompt <= 0 || cacheRead < 0) return null;
  if (cacheRead >= prompt) return "100";
  const tenths = Math.floor((cacheRead * 1000) / prompt);
  if (tenths >= 1000) return "99.9";
  return (tenths / 10).toFixed(1);
};

export type HistoryAction = "copy" | "branch" | "stop";

interface TurnState extends TurnSource {
  user: WireMessage | null;
  assistants: WireMessage[];
}

export class HistoryView {
  readonly root: HTMLElement;
  readonly scroll: HTMLElement;
  private readonly thread: HTMLElement;
  private readonly jump: HTMLButtonElement;
  private readonly nodes = new Map<string, HTMLElement>();
  private readonly blocks = new Map<string, CodeBlock>();
  private readonly messages: WireMessage[] = [];
  private readonly turns: TurnState[] = [];
  private readonly rail: TurnRail;
  private readonly changeNodes: HTMLElement[] = [];
  private readonly changeList: WireChanges[] = [];
  private readonly partStart = new Map<string, number>();
  private readonly thinkOpen = new Map<string, boolean>();
  private readonly favCache = new Map<string, string | null>();
  private readonly favPending = new Set<string>();
  private stick = true;
  private smoothing = false;

  onAction: (action: HistoryAction, id: string) => void = () => {};
  onCopyCode: (code: string) => void = () => {};
  onOpenLink: (href: string) => void = () => {};

  constructor(mount: HTMLElement) {
    this.scroll = document.createElement("div");
    this.scroll.className = "scroll";
    this.thread = document.createElement("div");
    this.thread.className = "thread";
    this.jump = document.createElement("button");
    this.jump.className = "jump-bottom";
    this.jump.type = "button";
    this.jump.innerHTML = `${icons.down}<span>${t("backBottom")}</span>`;
    // jump 必须留在滚动容器外：abspos 子元素会随内容一起滚走
    this.root = document.createElement("div");
    this.root.className = "scroll-wrap";
    this.scroll.append(this.thread);
    this.rail = new TurnRail(this.scroll);
    this.root.append(this.scroll, this.rail.root, this.jump);
    mount.append(this.root);

    this.scroll.addEventListener("scroll", () => {
      const gap = this.scroll.scrollHeight - this.scroll.scrollTop - this.scroll.clientHeight;
      if (this.smoothing) {
        if (gap < 48) this.smoothing = false;
        else return;
      }
      this.stick = gap < 48;
      this.jump.classList.toggle("show", !this.stick && this.messages.length > 0);
    });
    this.scroll.addEventListener("wheel", () => {
      this.smoothing = false;
    }, { passive: true });
    this.jump.addEventListener("click", () => {
      this.stick = true;
      this.jump.classList.remove("show");
      const gap = this.scroll.scrollHeight - this.scroll.scrollTop - this.scroll.clientHeight;
      if (gap < 48) return;
      this.smoothing = true;
      this.scroll.scrollTo({ top: this.scroll.scrollHeight, behavior: "smooth" });
    });
    this.thread.addEventListener("click", (e) => {
      const target = e.target as HTMLElement;
      const link = target.closest<HTMLAnchorElement>("a[data-href]");
      if (link) {
        e.preventDefault();
        this.onOpenLink(link.dataset.href ?? "");
        return;
      }
      const thinkHead = target.closest<HTMLButtonElement>(".think-head");
      if (thinkHead) {
        this.toggleThink(thinkHead);
        return;
      }
      const changesHead = target.closest<HTMLButtonElement>(".changes-head");
      if (changesHead) {
        changesHead.closest(".msg-changes")?.classList.toggle("open");
        return;
      }
      const toolHead = target.closest<HTMLButtonElement>(".tool-head");
      if (toolHead) {
        toolHead.closest(".tool-row")?.classList.toggle("open");
        return;
      }
      const changeFile = target.closest<HTMLButtonElement>(".change-file");
      if (changeFile) {
        this.onOpenLink(changeFile.dataset.path ?? "");
        return;
      }
      const codeBtn = target.closest<HTMLButtonElement>(".code-copy");
      if (codeBtn) {
        const block = this.blocks.get(codeBtn.dataset.code ?? "");
        if (block) {
          this.flashCopied(codeBtn);
          this.onCopyCode(block.code);
        }
        return;
      }
      const btn = target.closest<HTMLButtonElement>("[data-action]");
      if (btn) this.onAction(btn.dataset.action as HistoryAction, btn.dataset.id ?? "");
    });
  }

  markCopied(id: string): void {
    const btn = this.nodes.get(id)?.querySelector<HTMLButtonElement>('[data-action="copy"]');
    if (btn) this.flashCopied(btn);
  }

  private flashCopied(btn: HTMLButtonElement): void {
    if (btn.classList.contains("copied")) return;
    const original = btn.innerHTML;
    btn.classList.add("copied");
    btn.innerHTML = `${icons.check}<span>${t("copied")}</span>`;
    window.setTimeout(() => {
      btn.classList.remove("copied");
      btn.innerHTML = original;
    }, 1600);
  }

  private clockTimer: number | null = null;

  private syncClock(): void {
    const streaming = this.messages.filter((m) => m.role === "assistant" && m.state === "streaming");
    if (streaming.length === 0) {
      if (this.clockTimer !== null) window.clearInterval(this.clockTimer);
      this.clockTimer = null;
      return;
    }
    if (this.clockTimer !== null) return;
    const tick = (): void => {
      for (const msg of this.messages) {
        if (msg.role !== "assistant" || msg.state !== "streaming") continue;
        const el = this.nodes.get(msg.id)?.querySelector(".elapsed");
        if (el) el.textContent = `${((Date.now() - msg.ts) / 1000).toFixed(1)}s`;
      }
    };
    tick();
    this.clockTimer = window.setInterval(tick, 100);
  }

  get count(): number {
    return this.messages.length;
  }

  reset(messages: WireMessage[]): void {
    this.messages.length = 0;
    this.turns.length = 0;
    this.nodes.clear();
    this.changeNodes.length = 0;
    this.changeList.length = 0;
    this.partStart.clear();
    this.thinkOpen.clear();
    this.thread.innerHTML = "";
    for (const msg of messages) this.append(msg, false, false);
    this.stick = true;
    this.smoothing = false;
    this.scroll.scrollTop = this.scroll.scrollHeight;
    this.jump.classList.remove("show");
    this.rail.sync(this.turns);
    this.syncClock();
  }

  append(msg: WireMessage, animate = true, syncRail = true): void {
    this.messages.push(msg);
    const node = this.renderMessage(msg);
    this.nodes.set(msg.id, node);
    this.thread.append(node);
    if (msg.role === "user") {
      this.turns.push({ user: msg, assistants: [], element: node });
    } else {
      let turn = this.turns[this.turns.length - 1];
      if (!turn) {
        turn = { user: null, assistants: [], element: node };
        this.turns.push(turn);
      }
      turn.assistants.push(msg);
    }
    if (animate) {
      node.animate(
        [{ opacity: 0, transform: "translateY(6px)" }, { opacity: 1, transform: "none" }],
        { duration: 180, easing: "cubic-bezier(.2,.7,.3,1)" },
      );
    }
    this.syncScroll();
    if (syncRail) this.rail.sync(this.turns);
    this.syncClock();
  }

  // 一轮内的分段按实际发生顺序排列：推理 → 正文 → 工具 → 推理 → 正文……
  private partKey(id: string, index: number): string {
    return `${id}:${index}`;
  }

  private ensureParts(msg: WireMessage): WirePart[] {
    if (msg.parts) return msg.parts;
    const parts: WirePart[] = [];
    if (msg.reasoning) parts.push({ kind: "reasoning", text: msg.reasoning });
    if (msg.text) parts.push({ kind: "text", text: msg.text });
    for (const tool of msg.tools ?? []) parts.push({ kind: "tool", tool });
    msg.parts = parts;
    return parts;
  }

  private settleReasoning(msg: WireMessage, index: number): void {
    const part = msg.parts?.[index];
    if (part?.kind !== "reasoning") return;
    const key = this.partKey(msg.id, index);
    const started = this.partStart.get(key);
    if (started === undefined) return;
    if (part.ms === undefined) part.ms = Date.now() - started;
    this.partStart.delete(key);
  }

  private mergeTools(msg: WireMessage, tools: WireTool[]): void {
    const parts = this.ensureParts(msg);
    for (const tool of tools) {
      const part = parts.find((candidate) => candidate.kind === "tool" && candidate.tool.callId === tool.callId);
      if (part?.kind === "tool") part.tool = tool;
      else parts.push({ kind: "tool", tool });
    }
  }

  delta(id: string, text: string): void {
    const msg = this.messages.find((m) => m.id === id);
    const node = this.nodes.get(id);
    if (!msg || !node || !text) return;
    msg.text += text;
    const parts = this.ensureParts(msg);
    const last = parts[parts.length - 1];
    if (last?.kind === "text") {
      last.text += text;
      this.patchTail(msg, node);
    } else {
      // 多 step 之间宿主先补的分隔符不能变成独立段落
      const segment = text.replace(/^\n+/, "");
      if (segment === "") return;
      this.settleReasoning(msg, parts.length - 1);
      parts.push({ kind: "text", text: segment });
      this.paintProcess(msg, node, true);
    }
    this.syncScroll();
    this.rail.refreshPreview();
  }

  reasoningDelta(id: string, text: string): void {
    const msg = this.messages.find((m) => m.id === id);
    const node = this.nodes.get(id);
    if (!msg || !node || !text) return;
    msg.reasoning = (msg.reasoning ?? "") + text;
    const parts = this.ensureParts(msg);
    const last = parts[parts.length - 1];
    if (last?.kind === "reasoning") {
      last.text += text;
      this.patchTail(msg, node);
    } else {
      const segment = text.replace(/^\n+/, "");
      if (segment === "") return;
      parts.push({ kind: "reasoning", text: segment });
      this.partStart.set(this.partKey(id, parts.length - 1), Date.now());
      this.paintProcess(msg, node, true);
    }
    this.syncScroll();
    this.rail.refreshPreview();
  }

  done(id: string, state: "done" | "error"): void {
    const msg = this.messages.find((m) => m.id === id);
    const node = this.nodes.get(id);
    if (!msg || !node) return;
    msg.state = state;
    const parts = this.ensureParts(msg);
    parts.forEach((part, index) => {
      if (part.kind === "reasoning") this.settleReasoning(msg, index);
    });
    this.paintHead(msg, node);
    this.paintProcess(msg, node, true);
    this.paintActions(msg, node);
    this.syncScroll();
    this.rail.refreshPreview();
    this.syncClock();
  }

  update(message: WireMessage): void {
    const msg = this.messages.find((m) => m.id === message.id);
    const node = this.nodes.get(message.id);
    if (!msg || !node) return;
    const incoming = message.parts;
    // 原地合并，轮次梯子与节点都持有同一个消息对象
    Object.assign(msg, message);
    if (incoming) msg.parts = incoming;
    else if (message.tools) this.mergeTools(msg, message.tools);
    this.paintHead(msg, node);
    this.paintProcess(msg, node);
    this.paintActions(msg, node);
    this.syncScroll();
    this.rail.refreshPreview();
  }

  restart(id: string): void {
    const msg = this.messages.find((m) => m.id === id);
    const node = this.nodes.get(id);
    if (!msg || !node) return;
    msg.text = "";
    msg.reasoning = undefined;
    msg.reasoningMs = undefined;
    msg.parts = [];
    msg.tools = undefined;
    msg.state = "streaming";
    msg.ts = Date.now();
    for (const key of [...this.partStart.keys()]) {
      if (key.startsWith(`${id}:`)) this.partStart.delete(key);
    }
    for (const key of [...this.thinkOpen.keys()]) {
      if (key.startsWith(`${id}:`)) this.thinkOpen.delete(key);
    }
    this.paintHead(msg, node);
    this.paintProcess(msg, node, true);
    this.paintActions(msg, node);
    this.syncScroll();
    this.rail.refreshPreview();
    this.syncClock();
  }

  private renderMessage(msg: WireMessage): HTMLElement {
    const node = document.createElement("div");
    if (msg.role === "user") {
      node.className = "msg-user";
      const bubble = document.createElement("div");
      bubble.className = "bubble";
      bubble.textContent = msg.text;
      const time = document.createElement("time");
      time.textContent = timeText(msg.ts);
      node.append(bubble, time);
      return node;
    }
    node.className = "msg-assistant";
    node.dataset.id = msg.id;
    const head = document.createElement("div");
    head.className = "msg-head";
    const process = document.createElement("div");
    process.className = "msg-process";
    const actions = document.createElement("div");
    actions.className = "msg-actions";
    node.append(head, process, actions);
    this.paintHead(msg, node);
    this.paintProcess(msg, node, true);
    this.paintActions(msg, node);
    return node;
  }

  private processKey(parts: WirePart[]): string {
    return parts
      .map((part) => (part.kind === "tool" ? `tool:${part.tool.callId}` : part.kind))
      .join("|");
  }

  // 顺序渲染段落；结构变化（新增工具/新增段）才重建，token 增量走局部更新
  private paintProcess(msg: WireMessage, node: HTMLElement, force = false): void {
    const host = node.querySelector<HTMLElement>(".msg-process");
    if (!host) return;
    const parts = this.ensureParts(msg);
    const error = msg.state === "error";
    const key = this.processKey(parts);
    if (force || host.dataset.key !== key) {
      const openTools = new Set(
        Array.from(host.querySelectorAll<HTMLElement>(".tool-row.open"))
          .map((row) => row.dataset.call ?? ""),
      );
      host.replaceChildren(...parts.map((part, index) => {
        if (part.kind === "reasoning") return this.thinkPart(msg, index, part);
        if (part.kind === "text") return this.textPart(msg, index, part);
        const row = this.toolRow(part.tool);
        if (openTools.has(part.tool.callId)) row.classList.add("open");
        return row;
      }));
      if (error) host.append(this.errorPart(msg));
      host.dataset.key = key;
    } else {
      parts.forEach((part, index) => {
        const element = host.children[index] as HTMLElement | undefined;
        if (!element) return;
        if (part.kind === "reasoning") {
          this.patchThink(element, msg, index, part);
        } else if (part.kind === "text") {
          this.patchText(element, msg, index, part);
        } else {
          const open = element.classList.contains("open");
          const row = this.toolRow(part.tool);
          if (open) row.classList.add("open");
          element.replaceWith(row);
        }
      });
      const trailing = host.querySelector(":scope > .msg-error");
      if (error && !trailing) host.append(this.errorPart(msg));
      else if (!error && trailing) trailing.remove();
    }
    this.hydrateFavicons();
  }

  private patchTail(msg: WireMessage, node: HTMLElement): void {
    const host = node.querySelector<HTMLElement>(".msg-process");
    const parts = msg.parts ?? [];
    const index = parts.length - 1;
    const part = parts[index];
    const element = host?.children[index] as HTMLElement | undefined;
    if (!part || !element) return;
    if (part.kind === "reasoning") this.patchThink(element, msg, index, part);
    else if (part.kind === "text") {
      this.patchText(element, msg, index, part);
      this.hydrateFavicons();
    }
  }

  private textPart(msg: WireMessage, index: number, part: WirePart & { kind: "text" }): HTMLElement {
    const body = document.createElement("div");
    body.className = "msg-body msg-text";
    body.dataset.part = String(index);
    this.patchText(body, msg, index, part);
    return body;
  }

  private patchText(element: HTMLElement, msg: WireMessage, index: number, part: WirePart & { kind: "text" }): void {
    const settled = msg.state !== "streaming";
    // 流式期间不解析 TeX：半截公式会闪出错标，落定后按 DSH settled 语法重绘
    element.innerHTML = renderMarkdown(part.text, this.blocks, { math: settled });
    if (settled || index !== (msg.parts?.length ?? 0) - 1) return;
    const caret = document.createElement("span");
    caret.className = "caret";
    (element.lastElementChild ?? element).append(caret);
  }

  private thinkPart(msg: WireMessage, index: number, part: WirePart & { kind: "reasoning" }): HTMLElement {
    const think = document.createElement("div");
    think.className = "msg-think";
    think.dataset.part = String(index);
    think.innerHTML =
      `<button type="button" class="think-head">` +
        `<span class="think-chev">${icons.chevron}</span>` +
        `<span class="think-label"></span>` +
        `<span class="think-live"><span class="live-dot on"></span></span>` +
      `</button>` +
      `<div class="think-body"><div class="think-text"></div></div>`;
    this.patchThink(think, msg, index, part);
    return think;
  }

  private patchThink(element: HTMLElement, msg: WireMessage, index: number, part: WirePart & { kind: "reasoning" }): void {
    const running = msg.state === "streaming" && index === (msg.parts?.length ?? 0) - 1;
    const key = this.partKey(msg.id, index);
    element.classList.toggle("thinking", running);
    element.classList.toggle("open", running || (this.thinkOpen.get(key) ?? false));
    const seconds = part.ms !== undefined ? Math.max(1, Math.round(part.ms / 1000)) : null;
    element.querySelector(".think-label")!.textContent =
      running ? t("thinking") : seconds !== null ? t("thoughtSeconds", seconds) : t("thought");
    element.querySelector(".think-text")!.textContent = part.text;
  }

  private errorPart(msg: WireMessage): HTMLElement {
    const error = document.createElement("div");
    error.className = "msg-error";
    error.textContent = msg.text || t("interrupted");
    return error;
  }

  private toolRow(tool: WireTool): HTMLElement {
    const row = document.createElement("div");
    row.className = "tool-row";
    row.dataset.state = tool.state;
    row.dataset.call = tool.callId;

    const head = document.createElement("button");
    head.type = "button";
    head.className = "tool-head";

    const status = document.createElement("span");
    status.className = "tool-status-ico";
    status.innerHTML = tool.state === "running"
      ? '<span class="live-dot on"></span>'
      : tool.state === "ok" ? icons.check : icons.alert;

    const icon = document.createElement("span");
    icon.className = "tool-ico";
    icon.innerHTML = toolIcons[tool.variant] ?? icons.wrench;

    const title = document.createElement("span");
    title.className = "tool-title";
    title.textContent = toolLabel(tool.variant);

    const summary = document.createElement("span");
    summary.className = "tool-summary";
    const detailName = tool.variant === "mcp"
      ? mcpDisplayName(tool.name)
      : tool.variant === "tools" ? tool.name : "";
    summary.textContent = [detailName, tool.summary].filter(Boolean).join(" · ");

    const state = document.createElement("span");
    state.className = "tool-state";
    const statusKey = toolStatusKeys[tool.state];
    state.textContent = statusKey ? t(statusKey) : "";

    head.append(status, icon, title, summary, state);

    const hasDetail = (tool.input ?? "") !== "" || (tool.output ?? "") !== "";
    if (hasDetail) {
      const chev = document.createElement("span");
      chev.className = "tool-chev";
      chev.innerHTML = icons.chevron;
      head.append(chev);
    } else {
      head.classList.add("no-detail");
    }
    row.append(head);

    if (hasDetail) {
      const detail = document.createElement("div");
      detail.className = "tool-detail";
      if ((tool.input ?? "") !== "") detail.append(this.toolBlock(t("print.input"), prettyInput(tool.input!)));
      if ((tool.output ?? "") !== "") detail.append(this.toolBlock(t("print.output"), tool.output!));
      row.append(detail);
    }
    return row;
  }

  private toolBlock(label: string, text: string): HTMLElement {
    const block = document.createElement("div");
    block.className = "tool-block";
    const caption = document.createElement("span");
    caption.className = "tool-block-label";
    caption.textContent = label;
    const pre = document.createElement("pre");
    pre.className = "tool-pre";
    pre.textContent = text;
    block.append(caption, pre);
    return block;
  }

  private paintHead(msg: WireMessage, node: HTMLElement): void {
    const head = node.querySelector<HTMLElement>(".msg-head")!;
    head.innerHTML =
      `${icons.sparkle}<span class="model"></span><time>${timeText(msg.ts)}</time>` +
      (msg.state === "streaming"
        ? `<span class="streaming"><span class="live-dot on"></span>${t("generating")}<span class="elapsed"></span></span>`
        : "");
    head.querySelector(".model")!.textContent = msg.model ?? "Test";
  }

  private toggleThink(head: HTMLElement): void {
    const think = head.closest<HTMLElement>(".msg-think");
    const id = think?.closest<HTMLElement>(".msg-assistant")?.dataset.id;
    if (!think || !id) return;
    const open = !think.classList.contains("open");
    think.classList.toggle("open", open);
    this.thinkOpen.set(this.partKey(id, Number(think.dataset.part ?? "0")), open);
  }

  /** 复制按钮只取正文，不带上推理与工具输出。 */
  textOf(id: string): string {
    return this.messages.find((m) => m.id === id)?.text ?? "";
  }

  // favicon 探测有缓存：流式重绘不会重复请求，加载失败的域名回落到地球图标
  private hydrateFavicons(): void {
    this.thread.querySelectorAll<HTMLImageElement>("img.fav-img[data-host]").forEach((img) => {
      if (img.dataset.bound === "1") return;
      img.dataset.bound = "1";
      const host = img.dataset.host ?? "";
      if (!host) return;
      if (this.favCache.has(host)) {
        this.applyFav(host);
        return;
      }
      if (this.favPending.has(host)) return;
      this.favPending.add(host);
      const probe = new Image();
      const url = `https://${host}/favicon.ico`;
      probe.onload = () => {
        this.favCache.set(host, url);
        this.favPending.delete(host);
        this.applyFav(host);
      };
      probe.onerror = () => {
        this.favCache.set(host, null);
        this.favPending.delete(host);
        this.applyFav(host);
      };
      probe.src = url;
    });
  }

  private applyFav(host: string): void {
    const url = this.favCache.get(host) ?? null;
    this.thread.querySelectorAll<HTMLImageElement>(`img.fav-img[data-host="${CSS.escape(host)}"]`).forEach((img) => {
      if (url) img.src = url;
      else img.classList.add("failed");
    });
  }

  private paintActions(msg: WireMessage, node: HTMLElement): void {
    const actions = node.querySelector<HTMLElement>(".msg-actions")!;
    node.classList.toggle("streaming", msg.state === "streaming");
    // 有本轮用量的消息常驻操作行，让“本轮用量”无需悬停消息即可看到
    node.classList.toggle("has-usage", msg.usage !== undefined);
    actions.replaceChildren();

    if (msg.state === "streaming") {
      actions.append(this.actionButton(msg, "stop", icons.stop, t("stop"), true));
      return;
    }

    actions.append(this.actionButton(msg, "copy", icons.copy, t("copy")));
    if (msg.branchSeq !== undefined) {
      const branch = this.actionButton(msg, "branch", icons.branch, t("branch"));
      branch.title = msg.branchUnavailable ? t("branchUnavailable") : t("branchNew");
      if (msg.branchUnavailable) {
        branch.disabled = true;
        branch.classList.add("unavailable");
      }
      actions.append(branch);
    }
    if (msg.usage !== undefined) actions.append(this.usagePill(msg.usage));
  }

  private actionButton(
    msg: WireMessage,
    action: HistoryAction,
    icon: string,
    label: string,
    danger = false,
  ): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.dataset.action = action;
    button.dataset.id = msg.id;
    if (danger) button.classList.add("danger");
    button.innerHTML = icon;
    const text = document.createElement("span");
    text.textContent = label;
    button.append(text);
    return button;
  }

  private usagePill(usage: WireUsage): HTMLElement {
    const pill = document.createElement("span");
    pill.className = "usage-pill";

    const trigger = document.createElement("button");
    trigger.type = "button";
    trigger.className = "usage-trigger";
    trigger.setAttribute("aria-label", t("usage.title"));
    trigger.innerHTML = icons.database;
    const label = document.createElement("span");
    label.className = "usage-label";
    label.textContent = t("usage.label", compactTokens(usage.totalTokens));
    trigger.append(label);

    const panel = document.createElement("div");
    panel.className = "usage-panel";
    panel.setAttribute("role", "dialog");
    panel.setAttribute("aria-label", t("usage.title"));

    const head = document.createElement("div");
    head.className = "usage-head";
    const title = document.createElement("span");
    title.className = "usage-title";
    title.innerHTML = `${icons.database}<span>${t("usage.title")}</span>`;
    const total = document.createElement("span");
    total.className = "usage-total";
    total.textContent = `${exactTokens(usage.totalTokens)} tok`;
    head.append(title, total);

    const rule = document.createElement("div");
    rule.className = "usage-rule";

    const rows = document.createElement("dl");
    rows.className = "usage-rows";
    const row = (name: string, value: string): void => {
      const dt = document.createElement("dt");
      dt.textContent = name;
      const dd = document.createElement("dd");
      dd.textContent = value;
      rows.append(dt, dd);
    };

    const routes = usage.routes?.map((route) => `${route.provider}/${route.model}`).join(", ") ?? "";
    if (routes !== "") row(t("usage.providerModel"), routes);

    const prompt = usage.totalTokens - usage.outputTokens;
    const cacheHit = usage.cacheReadTokens === undefined ? null : cacheHitPercent(usage.cacheReadTokens, prompt);
    if (cacheHit !== null) row(t("usage.cacheHit"), `${cacheHit}%`);

    row(t("usage.uncachedInput"), `${exactTokens(usage.uncachedInputTokens)} tok`);
    if (usage.cacheReadTokens !== undefined) row(t("usage.cacheRead"), `${exactTokens(usage.cacheReadTokens)} tok`);
    if (usage.cacheWriteTokens !== undefined) row(t("usage.cacheWrite"), `${exactTokens(usage.cacheWriteTokens)} tok`);

    const dt = document.createElement("dt");
    dt.textContent = t("usage.output");
    const dd = document.createElement("dd");
    dd.textContent = `${exactTokens(usage.outputTokens)} tok`;
    if (usage.reasoningTokens !== undefined) {
      const reasoning = document.createElement("span");
      reasoning.className = "usage-reasoning";
      reasoning.textContent = t("usage.reasoning", exactTokens(usage.reasoningTokens));
      dd.append(reasoning);
    }
    rows.append(dt, dd);

    panel.append(head, rule, rows);
    pill.append(trigger, panel);
    return pill;
  }

  appendChanges(changes: WireChanges): void {
    if (changes.total <= 0) return;
    this.changeList.push(changes);
    const card = document.createElement("div");
    card.className = "msg-changes open";
    const head = document.createElement("button");
    head.type = "button";
    head.className = "changes-head";
    head.innerHTML =
      `<span class="changes-chev">${icons.chevron}</span>` +
      `<span class="changes-ico">${icons.file}</span>` +
      `<span class="changes-title"></span>` +
      `<span class="changes-stat"><span class="change-add"></span><span class="change-del"></span></span>`;
    head.querySelector(".changes-title")!.textContent = t("changes.edited", changes.total);
    head.querySelector(".change-add")!.textContent = `+${changes.added}`;
    head.querySelector(".change-del")!.textContent = `-${changes.deleted}`;

    const list = document.createElement("div");
    list.className = "changes-list";
    const files = changes.files.slice(0, 50);
    const cwd = changes.cwd.replace(/[\\/]+$/, "");
    for (const f of files) {
      const row = document.createElement("button");
      row.type = "button";
      row.className = "change-file";
      row.dataset.path = /^[A-Za-z]:[\\/]|^\\\\|^\//.test(f.path) ? f.path : `${cwd}/${f.path}`;
      row.title = row.dataset.path;
      row.innerHTML =
        `${icons.file}<span class="change-name"></span>` +
        (f.binary || f.oversized
          ? `<span class="change-size">${f.binary ? t("changes.binary") : t("changes.oversized")}</span>`
          : `<span class="change-add">+${f.added}</span><span class="change-del">-${f.deleted}</span>`);
      row.querySelector(".change-name")!.textContent = f.display;
      list.append(row);
    }
    if (changes.total > files.length) {
      const more = document.createElement("div");
      more.className = "changes-more";
      more.textContent = t("changes.more", changes.total - files.length);
      list.append(more);
    }
    card.append(head, list);
    this.changeNodes.push(card);
    this.thread.append(card);
    card.animate(
      [{ opacity: 0, transform: "translateY(6px)" }, { opacity: 1, transform: "none" }],
      { duration: 180, easing: "cubic-bezier(.2,.7,.3,1)" },
    );
    this.syncScroll();
  }

  // 语言切换后按新文案重放：消息、思考展开状态、文件改动与滚动位置都保留
  relocalize(): void {
    const messages = this.messages.slice();
    const changes = this.changeList.slice();
    const open = new Map(this.thinkOpen);
    const stick = this.stick;
    const top = this.scroll.scrollTop;
    this.reset(messages);
    this.thinkOpen.clear();
    for (const [key, value] of open) this.thinkOpen.set(key, value);
    for (const msg of this.messages) {
      const node = this.nodes.get(msg.id);
      if (!node) continue;
      this.paintHead(msg, node);
      this.paintProcess(msg, node, true);
      this.paintActions(msg, node);
    }
    this.jump.innerHTML = `${icons.down}<span>${t("backBottom")}</span>`;
    this.rail.sync(this.turns);
    for (const change of changes) this.appendChanges(change);
    if (!stick) {
      this.stick = false;
      this.scroll.scrollTop = top;
      this.jump.classList.toggle("show", this.messages.length > 0);
    }
  }

  snapBottom(): void {
    this.stick = true;
    this.syncScroll();
  }

  private syncScroll(): void {
    if (!this.stick) return;
    this.jump.classList.remove("show");
    if (this.smoothing) this.scroll.scrollTo({ top: this.scroll.scrollHeight, behavior: "smooth" });
    else this.scroll.scrollTop = this.scroll.scrollHeight;
  }
}