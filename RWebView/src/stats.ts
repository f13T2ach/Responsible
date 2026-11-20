import type { WireStats } from "./bridge";
import { icons } from "./history";

const formatTokens = (n: number): string =>
  n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1e3 ? `${(n / 1e3).toFixed(1)}K` : `${n}`;

// 部分命中不得四舍五入成 100%（harness token-format 规则）
const formatCache = (rate: number): string => {
  const fixed = rate.toFixed(1);
  return rate > 0 && rate < 100 && fixed === "100.0" ? "99.9%" : `${fixed}%`;
};

export class StatsView {
  readonly bar: HTMLElement;
  private readonly rounds: HTMLElement;
  private readonly steps: HTMLElement;
  private readonly tps: HTMLElement;
  private readonly tokens: HTMLElement;
  private readonly cache: HTMLElement;
  private readonly context: HTMLElement;
  private readonly meter: HTMLElement;
  private readonly spark: SVGPolylineElement;
  private readonly live: HTMLElement;
  private readonly liveLabel: HTMLElement;
  private readonly samples: number[] = [];

  constructor(mount: HTMLElement) {
    this.bar = document.createElement("div");
    this.bar.className = "stats-bar";
    this.bar.innerHTML = `
      <span class="stats-title">${icons.gauge}<span>调用统计</span></span>
      <span class="stat"><b data-f="rounds">0</b><span class="unit">轮</span><b data-f="steps">0</b><span class="unit">步</span></span>
      <span class="stat spark"><svg width="90" height="22" viewBox="0 0 90 22"><polyline points=""/></svg><b data-f="tps">0</b><span class="unit">tok/s</span></span>
      <span class="stat"><b data-f="tokens">0</b><span class="unit">tok</span></span>
      <span class="stat"><span>缓存命中</span><b data-f="cache">0%</b></span>
      <span class="stat meter"><span>上下文</span><span class="meter-track"><span class="meter-fill" data-f="meter"></span></span><b data-f="context">0%</b></span>
      <span class="stats-spacer"></span>
      <span class="stats-live"><span class="live-dot" data-f="live"></span><span data-f="liveLabel">空闲</span></span>`;
    mount.append(this.bar);
    const q = <T extends Element>(f: string): T => this.bar.querySelector<T>(`[data-f="${f}"]`)!;
    this.rounds = q("rounds");
    this.steps = q("steps");
    this.tps = q("tps");
    this.tokens = q("tokens");
    this.cache = q("cache");
    this.context = q("context");
    this.meter = q("meter");
    this.spark = this.bar.querySelector<SVGPolylineElement>("polyline")!;
    this.live = q("live");
    this.liveLabel = q("liveLabel");
  }

  update(stats: WireStats, streaming: boolean, running = false): void {
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
    this.liveLabel.textContent = streaming ? "生成中" : running ? "运行中" : "空闲";

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