import type { WireStats } from "./bridge";
import { icons } from "./history";
import { t } from "./i18n";
import type { WireTask } from "./bridge";

const formatTokens = (n: number): string =>
  n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1e3 ? `${(n / 1e3).toFixed(1)}K` : `${n}`;

// 部分命中不得四舍五入成 100%（harness token-format 规则）
const formatCache = (rate: number): string => {
  const fixed = rate.toFixed(1);
  return rate > 0 && rate < 100 && fixed === "100.0" ? "99.9%" : `${fixed}%`;
};

export class StatsView {
  readonly bar: HTMLElement;
  private tasks!: HTMLElement;
  private tasksLabel!: HTMLElement;
  private tasksList: WireTask[] = [];
  private tasksPanel: HTMLElement | null = null;
  private rounds!: HTMLElement;
  private steps!: HTMLElement;
  private tps!: HTMLElement;
  private tokens!: HTMLElement;
  private cache!: HTMLElement;
  private context!: HTMLElement;
  private meter!: HTMLElement;
  private spark!: SVGPolylineElement;
  private live!: HTMLElement;
  private liveLabel!: HTMLElement;
  private readonly samples: number[] = [];
  private last: WireStats | null = null;
  private lastStreaming = false;
  private lastRunning = false;

  constructor(mount: HTMLElement) {
    this.bar = document.createElement("div");
    this.bar.className = "stats-bar";
    this.build();
    mount.append(this.bar);
  }

  /** 顶栏的运行任务指示器：空闲显示“没有正在运行的任务”，有任务时点开列出详情。 */
  setTasks(tasks: WireTask[]): void {
    this.tasksList = tasks;
    // 没有任务时整块隐藏，别占着顶栏
    this.tasks.classList.toggle("empty", tasks.length === 0);
    this.tasksLabel.textContent = tasks.length === 0 ? "" : t("tasks.running", tasks.length);
    this.tasks.classList.toggle("active", tasks.length > 0);
    this.tasks.classList.toggle("open", tasks.length > 0 && this.tasks.classList.contains("open"));
    if (tasks.length === 0) this.closeTasks();
    else if (this.tasks.classList.contains("open")) this.paintTasks();
  }

  private toggleTasks(): void {
    if (this.tasksList.length === 0) return;
    if (this.tasksPanel !== null) { this.closeTasks(); return; }
    const panel = document.createElement("div");
    panel.className = "tasks-panel";
    const head = document.createElement("div");
    head.className = "tasks-head";
    head.textContent = t("tasks.title");
    const list = document.createElement("div");
    list.className = "tasks-list";
    panel.append(head, list);
    this.bar.append(panel);
    this.tasksPanel = panel;
    this.tasks.classList.add("open");
    this.paintTasks();
    // 点两次关闭：面板外点击也算
    panel.addEventListener("click", event => event.stopPropagation());
    document.addEventListener("click", () => this.closeTasks(), { once: true });
  }

  private paintTasks(): void {
    const list = this.tasksPanel?.querySelector(".tasks-list");
    if (!list) return;
    list.replaceChildren();
    for (const task of this.tasksList) {
      const row = document.createElement("div");
      row.className = "tasks-row";
      const name = document.createElement("span");
      name.className = "tasks-name";
      name.textContent = task.name;
      const summary = document.createElement("span");
      summary.className = "tasks-summary";
      summary.textContent = task.summary;
      row.append(name, summary);
      list.append(row);
    }
  }

  private closeTasks(): void {
    this.tasksPanel?.remove();
    this.tasksPanel = null;
    this.tasks.classList.remove("open");
  }

  bindTasksToggle(): void {
    this.tasks.addEventListener("click", event => {
      event.stopPropagation();
      this.toggleTasks();
    });
    this.tasks.addEventListener("keydown", event => {
      if (event.key !== "Enter" && event.key !== " ") return;
      event.preventDefault();
      this.toggleTasks();
    });
  }
  private build(): void {
    this.bar.innerHTML = `
      <span class="stats-title stats-tasks empty" data-f="tasks" role="button" tabindex="0">${icons.gauge}<span data-f="tasksLabel">${t("tasks.none")}</span></span>
      <span class="stat"><b data-f="rounds">0</b><span class="unit">${t("stats.rounds")}</span><b data-f="steps">0</b><span class="unit">${t("stats.steps")}</span></span>
      <span class="stat spark"><svg width="90" height="22" viewBox="0 0 90 22"><polyline points=""/></svg><b data-f="tps">0</b><span class="unit">tok/s</span></span>
      <span class="stat"><b data-f="tokens">0</b><span class="unit">tok</span></span>
      <span class="stat"><span>${t("stats.cacheHit")}</span><b data-f="cache">0%</b></span>
      <span class="stat meter"><span>${t("stats.context")}</span><span class="meter-track"><span class="meter-fill" data-f="meter"></span></span><b data-f="context">0%</b></span>
      <span class="stats-spacer"></span>
      <span class="stats-live"><span class="live-dot" data-f="live"></span><span data-f="liveLabel">${t("stats.idle")}</span></span>`;
    const q = <T extends Element>(f: string): T => this.bar.querySelector<T>(`[data-f="${f}"]`)!;
    this.rounds = q("rounds");
    this.steps = q("steps");
    this.tps = q("tps");
    this.tokens = q("tokens");
    this.cache = q("cache");
    this.context = q("context");
    this.meter = q("meter");
    this.spark = this.bar.querySelector<SVGPolylineElement>("polyline")!;
    this.tasks = q("tasks");
    this.tasksLabel = q("tasksLabel");
    this.live = q("live");
    this.liveLabel = q("liveLabel");
  }

  relocalize(): void {
    this.build();
    if (this.last !== null) this.update(this.last, this.lastStreaming, this.lastRunning);
  }

  update(stats: WireStats, streaming: boolean, running = false): void {
    this.last = stats;
    this.lastStreaming = streaming;
    this.lastRunning = running;
    this.rounds.textContent = `${stats.rounds}`;
    this.steps.textContent = `${stats.steps}`;
    this.tps.textContent = `${stats.tokensPerSecond}`;
    this.tokens.textContent = formatTokens(stats.totalTokens);
    this.cache.textContent = formatCache(stats.cacheHitRate);
    const context = Math.round(stats.contextPercent);
    this.context.textContent = `${context}%`;
    this.meter.style.width = `${Math.min(100, context)}%`;
    this.meter.classList.toggle("warn", stats.contextPercent > 80);
    this.live.classList.toggle("on", streaming || running);
    this.liveLabel.textContent = streaming ? t("stats.generating") : running ? t("stats.running") : t("stats.idle");

    if (streaming || running || stats.tokensPerSecond > 0) {
      this.samples.push(stats.tokensPerSecond);
      if (this.samples.length > 48) this.samples.shift();
    }
    const max = Math.max(1, ...this.samples);
    const points = this.samples
      .map((v, i) => `${((i / Math.max(1, this.samples.length - 1)) * 88 + 1).toFixed(1)},${(20 - (v / max) * 18).toFixed(1)}`)
      .join(" ");
    this.spark.setAttribute("points", points);
  }
}