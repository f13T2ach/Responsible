import type { WireMessage } from "./bridge";

export interface TurnSource {
  readonly user: WireMessage | null;
  readonly assistants: readonly WireMessage[];
  readonly element: HTMLElement;
}

const PREVIEW_HEIGHT = 100;

function reducedMotion(): boolean {
  return typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;
}

/** Flatten markdown to one preview paragraph; code fences collapse to a placeholder. */
function previewText(text: string, codeLabel: string): string {
  return text
    .replace(/```[\s\S]*?```/g, ` ${codeLabel} `)
    .replace(/`([^`]*)`/g, "$1")
    .replace(/\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/[#>*]/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

/** Fixed-pitch ladder of Turns: one tick per turn, hover preview, click to jump. */
export class TurnRail {
  readonly root: HTMLElement;
  private readonly ladder: HTMLElement;
  private readonly list: HTMLElement;
  private readonly preview: HTMLElement;
  private readonly promptEl: HTMLElement;
  private readonly responseEl: HTMLElement;
  private turns: readonly TurnSource[] = [];
  private marks: HTMLButtonElement[] = [];
  private hovered = -1;
  private active = -1;
  private pointerInside = false;
  private spyFrame: number | null = null;

  constructor(private readonly scroll: HTMLElement) {
    this.root = document.createElement("div");
    this.root.className = "ladder-slot";
    this.ladder = document.createElement("div");
    this.ladder.className = "ladder";
    this.list = document.createElement("div");
    this.list.className = "ladder-list";
    this.preview = document.createElement("div");
    this.preview.className = "turn-preview";
    this.promptEl = document.createElement("div");
    this.promptEl.className = "turn-preview-prompt";
    this.responseEl = document.createElement("div");
    this.responseEl.className = "turn-preview-response";
    this.preview.append(this.promptEl, this.responseEl);
    this.ladder.append(this.list, this.preview);
    this.root.append(this.ladder);

    this.root.addEventListener("pointerenter", () => {
      this.pointerInside = true;
    });
    this.root.addEventListener("pointerleave", () => {
      this.pointerInside = false;
      this.preview.classList.remove("show");
      if (this.hovered >= 0) this.marks[this.hovered]?.classList.remove("hover");
      this.hovered = -1;
      this.revealActive();
    });
    this.list.addEventListener("scroll", () => this.updateFades(), { passive: true });
    this.list.addEventListener("wheel", (event) => this.onWheel(event), { passive: false });
    this.scroll.addEventListener("scroll", () => this.scheduleSpy(), { passive: true });
    window.addEventListener("resize", () => {
      this.scheduleSpy();
      this.revealActive();
    });
  }

  sync(turns: readonly TurnSource[]): void {
    this.turns = turns;
    const show = turns.length >= 2;
    this.root.classList.toggle("show", show);
    if (!show) {
      this.preview.classList.remove("show");
      this.hovered = -1;
    }
    if (turns.length !== this.marks.length) {
      const count = turns.length;
      this.list.replaceChildren();
      this.marks = [];
      for (let index = 0; index < count; index++) this.marks.push(this.createMark(index));
      this.list.append(...this.marks);
      this.active = -1;
      this.hovered = -1;
      this.preview.classList.remove("show");
    }
    this.updateFades();
    this.updateActive();
  }

  /** Refresh preview text after streaming deltas; no-op while hidden. */
  refreshPreview(): void {
    if (!this.preview.classList.contains("show")) return;
    this.paintPreview();
    this.positionPreview();
  }

  private createMark(index: number): HTMLButtonElement {
    const mark = document.createElement("button");
    mark.type = "button";
    mark.className = "turn-mark";
    mark.dataset.index = String(index);
    mark.setAttribute("aria-label", `跳转到第 ${index + 1} 轮`);
    mark.addEventListener("pointermove", () => this.hover(index));
    mark.addEventListener("click", () => this.jump(index));
    mark.addEventListener("focus", () => this.hover(index));
    mark.addEventListener("blur", () => {
      if (this.pointerInside) return;
      mark.classList.remove("hover");
      if (this.hovered === index) this.hovered = -1;
      this.preview.classList.remove("show");
      this.revealActive();
    });
    return mark;
  }

  private hover(index: number): void {
    if (this.hovered === index) {
      this.refreshPreview();
      return;
    }
    if (this.hovered >= 0) this.marks[this.hovered]?.classList.remove("hover");
    this.hovered = index;
    const mark = this.marks[index];
    if (!mark) return;
    mark.classList.add("hover");
    this.paintPreview();
    this.positionPreview();
    // 先落位再显示，避免首帧从 top:0 过渡过来
    if (!this.preview.classList.contains("show")) void this.preview.offsetWidth;
    this.preview.classList.add("show");
  }

  private paintPreview(): void {
    const turn = this.turns[this.hovered];
    if (!turn) return;
    const prompt = previewText(turn.user?.text ?? "", "代码");
    const response = previewText(turn.assistants.map((m) => m.text).join("\n"), "代码块");
    this.promptEl.textContent = prompt || `第 ${this.hovered + 1} 轮`;
    this.responseEl.textContent = response;
    this.responseEl.style.display = response === "" ? "none" : "";
  }

  private positionPreview(): void {
    const mark = this.marks[this.hovered];
    if (!mark) return;
    const height = this.preview.offsetHeight || PREVIEW_HEIGHT;
    const center = mark.offsetTop + mark.offsetHeight / 2 - this.list.scrollTop;
    const maxTop = Math.max(0, this.ladder.clientHeight - height);
    this.preview.style.top = `${Math.min(maxTop, Math.max(0, center - height / 2))}px`;
  }

  private jump(index: number): void {
    const turn = this.turns[index];
    if (!turn) return;
    const viewport = this.scroll.getBoundingClientRect();
    const rect = turn.element.getBoundingClientRect();
    const top = Math.max(0, this.scroll.scrollTop + rect.top - viewport.top - 14);
    this.setActive(index);
    this.revealActive();
    this.scroll.scrollTo({ top, behavior: reducedMotion() ? "auto" : "smooth" });
  }

  private onWheel(event: WheelEvent): void {
    const max = this.list.scrollHeight - this.list.clientHeight;
    const atTop = this.list.scrollTop <= 0 && event.deltaY < 0;
    const atBottom = this.list.scrollTop >= max - 1 && event.deltaY > 0;
    if (max <= 0 || atTop || atBottom) {
      this.scroll.scrollTop += event.deltaY;
      event.preventDefault();
    }
  }

  private updateFades(): void {
    const max = this.list.scrollHeight - this.list.clientHeight;
    this.list.classList.toggle("fade-top", this.list.scrollTop > 1);
    this.list.classList.toggle("fade-bottom", max > 1 && this.list.scrollTop < max - 1);
  }

  private scheduleSpy(): void {
    if (this.spyFrame !== null) return;
    this.spyFrame = requestAnimationFrame(() => {
      this.spyFrame = null;
      this.updateActive();
    });
  }

  private updateActive(): void {
    if (this.turns.length < 2) return;
    const viewport = this.scroll.getBoundingClientRect();
    const probe = viewport.top + Math.min(160, this.scroll.clientHeight * 0.4);
    let index = 0;
    for (let i = 0; i < this.turns.length; i++) {
      if (this.turns[i].element.getBoundingClientRect().top <= probe) index = i;
      else break;
    }
    if (this.scroll.scrollHeight - this.scroll.scrollTop - this.scroll.clientHeight < 8) {
      index = this.turns.length - 1;
    }
    this.setActive(index);
  }

  private setActive(index: number): void {
    if (index < 0 || index >= this.marks.length || index === this.active) return;
    if (this.active >= 0) this.marks[this.active]?.classList.remove("active");
    this.active = index;
    this.marks[index]?.classList.add("active");
    this.revealActive();
  }

  private revealActive(): void {
    if (this.pointerInside) return;
    const mark = this.marks[this.active];
    if (!mark) return;
    const pad = 24;
    const top = mark.offsetTop;
    const bottom = top + mark.offsetHeight;
    const viewTop = this.list.scrollTop;
    const viewBottom = viewTop + this.list.clientHeight;
    if (top < viewTop + pad) this.list.scrollTop = Math.max(0, top - pad);
    else if (bottom > viewBottom - pad) this.list.scrollTop = bottom - this.list.clientHeight + pad;
    this.updateFades();
  }
}