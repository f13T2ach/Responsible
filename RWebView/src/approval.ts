import { send } from "./bridge";
import { t } from "./i18n";

// 迁移自 DSH 的审批卡片：底部悬浮，等宽内容区，警告条 + 标题/说明 + 拒绝/允许一次。
// 交互照旧：Enter 允许、Esc 拒绝；输入框里不触发，IME 组合中也不触发。

export interface WireApproval {
  id: string;
  toolName: string;
  reason?: string;
  callId?: string;
  dangerous?: boolean;
}

export class ApprovalDock {
  readonly root: HTMLDivElement;
  private readonly card: HTMLDivElement;
  private readonly strip: HTMLDivElement;
  private readonly stripText: HTMLSpanElement;
  private readonly headline: HTMLDivElement;
  private readonly description: HTMLParagraphElement;
  private readonly command: HTMLDivElement;
  private readonly more: HTMLSpanElement;
  private readonly reject: HTMLButtonElement;
  private readonly allow: HTMLButtonElement;
  private queue: WireApproval[] = [];
  private answered = false;
  private waiting = false;
  private composing = false;
  private compositionEnded = false;

  constructor(mount: HTMLElement) {
    this.root = document.createElement("div");
    this.root.className = "approval-root";

    this.card = document.createElement("div");
    this.card.className = "approval-card";
    this.card.tabIndex = 0;
    this.card.setAttribute("role", "group");

    this.strip = document.createElement("div");
    this.strip.className = "approval-strip";
    const dot = document.createElement("span");
    dot.className = "approval-dot";
    this.stripText = document.createElement("span");
    this.strip.append(dot, this.stripText);

    const body = document.createElement("div");
    body.className = "approval-body";
    const heading = document.createElement("div");
    heading.className = "approval-heading";
    this.headline = document.createElement("div");
    this.headline.className = "approval-headline";
    this.more = document.createElement("span");
    this.more.className = "approval-more";
    heading.append(this.headline, this.more);
    this.description = document.createElement("p");
    this.description.className = "approval-description";
    this.command = document.createElement("div");
    this.command.className = "approval-command";
    body.append(heading, this.description, this.command);

    const actions = document.createElement("div");
    actions.className = "approval-actions";
    this.reject = this.button(t("approval.reject"), "approval-reject", () => this.answer("rejected"));
    this.allow = this.button(t("approval.allowOnce"), "approval-allow", () => this.answer("allowed-once"));
    actions.append(this.reject, this.allow);

    this.card.append(this.strip, body, actions);
    this.root.append(this.card);
    this.root.style.display = "none";
    mount.append(this.root);

    this.card.addEventListener("keydown", (event) => this.onKeyDown(event));
    this.card.addEventListener("keyup", () => { this.compositionEnded = false; });
    this.card.addEventListener("compositionstart", () => { this.composing = true; });
    this.card.addEventListener("compositionend", () => { this.composing = false; this.compositionEnded = true; });
  }

  /** 宿主推来最新一批待决定请求；空数组即收起。 */
  update(approvals: WireApproval[]): void {
    this.queue = approvals.slice();
    const head = this.queue[0];
    if (head === undefined) {
      this.root.style.display = "none";
      return;
    }
    const same = this.card.dataset.approvalId === head.id;
    if (!same) {
      this.answered = false;
      this.waiting = false;
      this.card.dataset.approvalId = head.id;
    }
    const dangerous = head.dangerous === true;
    this.card.classList.toggle("danger", dangerous);
    this.stripText.textContent = this.answered ? t("approval.deciding") : t("approval.waiting");
    this.headline.textContent = head.reason ?? t("approval.headline", head.toolName);
    this.description.textContent = dangerous ? t("approval.dangerHint") : "";
    this.description.style.display = dangerous ? "" : "none";
    this.command.textContent = head.callId === undefined ? "" : `${head.toolName} · ${head.callId}`;
    this.command.style.display = head.callId === undefined ? "none" : "";
    this.more.textContent = this.queue.length > 1 ? t("approval.more", this.queue.length - 1) : "";
    this.reject.disabled = this.answered;
    this.allow.disabled = this.answered;
    this.root.style.display = "flex";
  }

  private button(label: string, className: string, onClick: () => void): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "approval-button " + className;
    button.textContent = label;
    button.addEventListener("click", onClick);
    return button;
  }

  private answer(outcome: "allowed-once" | "rejected"): void {
    const head = this.queue[0];
    if (head === undefined || this.waiting) return;
    this.waiting = true;
    this.answered = true;
    this.stripText.textContent = t("approval.deciding");
    this.reject.disabled = true;
    this.allow.disabled = true;
    send({ type: "approval.answer", id: head.id, text: outcome });
  }

  private onKeyDown(event: KeyboardEvent): void {
    const target = event.target as Element | null;
    if (event.defaultPrevented) return;
    if (target?.closest('input, textarea, select, [contenteditable="true"]') !== null) return;
    if (event.key !== "Enter" && event.key !== "Escape") return;
    if (event.key === "Enter" && target?.closest('button, a[href], [role="button"]') !== null) return;
    if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
    event.preventDefault();
    event.stopPropagation();
    if (event.repeat || this.composing || this.compositionEnded || event.isComposing || event.keyCode === 229) return;
    this.answer(event.key === "Enter" ? "allowed-once" : "rejected");
  }
}
