import type { WireMessage } from "./bridge";
import { renderMarkdown, type CodeBlock } from "./markdown";

export const icons = {
  sparkle:
    '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z"/><path d="M18.5 15.5l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8z"/></svg>',
  sparkleBig:
    '<svg width="36" height="36" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z"/><path d="M18.5 15.5l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8z"/></svg>',
  copy:
    '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/></svg>',
  retry:
    '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-2.6-6.4"/><path d="M21 3v6h-6"/></svg>',
  stop:
    '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"><rect x="6" y="6" width="12" height="12" rx="2"/></svg>',
  down:
    '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 4v16"/><path d="M6 14l6 6 6-6"/></svg>',
  check:
    '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12.5l5 5L20 6.5"/></svg>',
  gauge:
    '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M12 15l3.5-5.5"/><path d="M4 19a9 9 0 1 1 16 0"/></svg>',
};

const timeText = (ts: number): string =>
  new Date(ts).toLocaleTimeString("zh-CN", { hour12: false, hour: "2-digit", minute: "2-digit" });

export type HistoryAction = "copy" | "retry" | "stop";

export class HistoryView {
  readonly root: HTMLElement;
  readonly scroll: HTMLElement;
  private readonly thread: HTMLElement;
  private readonly jump: HTMLButtonElement;
  private readonly nodes = new Map<string, HTMLElement>();
  private readonly blocks = new Map<string, CodeBlock>();
  private readonly messages: WireMessage[] = [];
  private stick = true;
  private smoothing = false;

  onAction: (action: HistoryAction, id: string) => void = () => {};
  onCopyCode: (code: string) => void = () => {};

  constructor(mount: HTMLElement) {
    this.scroll = document.createElement("div");
    this.scroll.className = "scroll";
    this.thread = document.createElement("div");
    this.thread.className = "thread";
    this.jump = document.createElement("button");
    this.jump.className = "jump-bottom";
    this.jump.type = "button";
    this.jump.innerHTML = `${icons.down}<span>回到底部</span>`;
    // jump 必须留在滚动容器外：abspos 子元素会随内容一起滚走
    this.root = document.createElement("div");
    this.root.className = "scroll-wrap";
    this.scroll.append(this.thread);
    this.root.append(this.scroll, this.jump);
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
    btn.innerHTML = `${icons.check}<span>已复制</span>`;
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
    this.nodes.clear();
    this.thread.innerHTML = "";
    for (const msg of messages) this.append(msg, false);
    this.stick = true;
    this.smoothing = false;
    this.scroll.scrollTop = this.scroll.scrollHeight;
    this.jump.classList.remove("show");
    this.syncClock();
  }

  append(msg: WireMessage, animate = true): void {
    this.messages.push(msg);
    const node = this.renderMessage(msg);
    this.nodes.set(msg.id, node);
    this.thread.append(node);
    if (animate) {
      node.animate(
        [{ opacity: 0, transform: "translateY(6px)" }, { opacity: 1, transform: "none" }],
        { duration: 180, easing: "cubic-bezier(.2,.7,.3,1)" },
      );
    }
    this.syncScroll();
    this.syncClock();
  }

  delta(id: string, text: string): void {
    const msg = this.messages.find((m) => m.id === id);
    const node = this.nodes.get(id);
    if (!msg || !node) return;
    msg.text += text;
    this.paintBody(msg, node);
    this.syncScroll();
  }

  done(id: string, state: "done" | "error"): void {
    const msg = this.messages.find((m) => m.id === id);
    const node = this.nodes.get(id);
    if (!msg || !node) return;
    msg.state = state;
    this.paintBody(msg, node);
    this.paintHead(msg, node);
    this.paintActions(msg, node);
    this.syncScroll();
    this.syncClock();
  }

  restart(id: string): void {
    const msg = this.messages.find((m) => m.id === id);
    const node = this.nodes.get(id);
    if (!msg || !node) return;
    msg.text = "";
    msg.state = "streaming";
    msg.ts = Date.now();
    this.paintBody(msg, node);
    this.paintHead(msg, node);
    this.paintActions(msg, node);
    this.syncScroll();
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
    const body = document.createElement("div");
    body.className = "msg-body";
    const actions = document.createElement("div");
    actions.className = "msg-actions";
    node.append(head, body, actions);
    this.paintHead(msg, node);
    this.paintBody(msg, node);
    this.paintActions(msg, node);
    return node;
  }

  private paintHead(msg: WireMessage, node: HTMLElement): void {
    const head = node.querySelector<HTMLElement>(".msg-head")!;
    head.innerHTML =
      `${icons.sparkle}<span class="model"></span><time>${timeText(msg.ts)}</time>` +
      (msg.state === "streaming"
        ? `<span class="streaming"><span class="live-dot on"></span>生成中<span class="elapsed"></span></span>`
        : "");
    head.querySelector(".model")!.textContent = msg.model ?? "Test";
  }

  private paintBody(msg: WireMessage, node: HTMLElement): void {
    const body = node.querySelector<HTMLElement>(".msg-body")!;
    if (msg.state === "error") {
      body.innerHTML = `<div class="msg-error"></div>`;
      body.querySelector(".msg-error")!.textContent = msg.text || "生成中断";
      return;
    }
    body.innerHTML = renderMarkdown(msg.text, this.blocks);
    if (msg.state === "streaming") {
      const caret = document.createElement("span");
      caret.className = "caret";
      (body.lastElementChild ?? body).append(caret);
    }
  }

  private paintActions(msg: WireMessage, node: HTMLElement): void {
    const actions = node.querySelector<HTMLElement>(".msg-actions")!;
    node.classList.toggle("streaming", msg.state === "streaming");
    const btn = (action: HistoryAction, icon: string, label: string, danger = false) =>
      `<button type="button" data-action="${action}" data-id="${msg.id}" class="${danger ? "danger" : ""}">${icon}<span>${label}</span></button>`;
    actions.innerHTML =
      msg.state === "streaming"
        ? btn("stop", icons.stop, "停止", true)
        : btn("copy", icons.copy, "复制") + btn("retry", icons.retry, "重试");
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