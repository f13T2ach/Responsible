// 无 WebView2 宿主（浏览器直开 dist/index.html）时的预览驱动： seeded 会话 + 模拟流式回复 + 主题切换。
import { applyPalette, defaultPalette, type Palette, type WireMessage, type WireStats } from "./bridge";
import type { HistoryView } from "./history";
import type { StatsView } from "./stats";

const lightPalette: Palette = {
  bg: "#f3f3f3",
  surface: "#fbfbfb",
  surface2: "#ffffff",
  border: "#e0e0e0",
  text: "#1a1a1a",
  text2: "#5c5c5c",
  text3: "#8a8a8a",
  accent: "#0067c0",
  success: "#0f7b0f",
  caution: "#9d5d00",
  critical: "#c42b1c",
  codeBg: "#f5f5f5",
};

const reply = `换 WebView2 渲染历史是合理的：消息排版、markdown、代码高亮这些复杂样式交给 Web 层，WPF 只负责宿主与通信。

### 背景无感的关键
- 宿主面板与 Web \`body\` 使用**同一组主题色**，由 C# 解析资源字典后下发；
- WebView2 的 \`DefaultBackgroundColor\` 在导航前就设成面板色，避免白闪；
- 圆角裁切留在 WPF 侧，Web 只画纯色背景。

\`\`\`ts
// C# → Web：初始化载荷
const init = {
  type: "init",
  theme: "dark",
  colors: { bg: "#202020", text: "#ffffff" },
  messages: session.snapshot(),
  stats: session.stats(),
};
await webview.ExecuteScriptAsync(\`window.rwebReceive(\${JSON.stringify(JSON.stringify(init))})\`);
\`\`\`

> 通信走 postMessage 双向通道：Web 上报 ready / retry / stop / copyText，C# 下发 append / delta / done / stats。

这样滚动、流式光标、代码块复制都在 Web 内完成，C# 侧只维护会话状态这一份数据源。`;

interface Deps {
  history: HistoryView;
  stats: StatsView;
  setStats: (s: WireStats) => void;
  refresh: () => void;
}

let seq = 0;
const nid = (): string => `p${++seq}`;

export function installPreview(deps: Deps): (action: "retry" | "stop", id: string) => void {
  const { history, stats } = deps;
  const now = Date.now();
  const base: WireStats = { rounds: 2, steps: 14, tokensPerSecond: 0, totalTokens: 3260, cacheHitRate: 96.4, contextPercent: 18 };
  let current = { ...base };
  const timers = new Map<string, number>();

  const seed: WireMessage[] = [
    { id: nid(), role: "user", text: "帮我把 Moe 主页面的历史消息区做成可滚动的会话视图", state: "done", ts: now - 240000 },
    {
      id: nid(),
      role: "assistant",
      model: "Test",
      state: "done",
      ts: now - 238000,
      text: `好的。会话视图需要三件事：**消息模型**、**滚动容器**、**流式更新通道**。\n\n1. 消息区分 user / assistant 两种角色，状态机为 streaming → done / error；\n2. 滚动容器默认吸附底部，用户上滚后出现"回到底部"；\n3. 增量文本以 delta 推送，避免整棵 DOM 重建。\n\n\`\`\`csharp\npublic sealed record ChatMessage(string Id, string Role, string Text, string State);\n\`\`\``,
    },
    { id: nid(), role: "user", text: "背景色要和 WPF 内容区完全一致，不能闪白", state: "done", ts: now - 120000 },
    {
      id: nid(),
      role: "assistant",
      model: "Test",
      state: "done",
      ts: now - 118000,
      text: `明白：颜色单一来源在 WPF 资源字典，Web 侧只接收下发值。\n\n- 导航前设置 \`DefaultBackgroundColor\`；\n- \`init\` 载荷带完整调色板；\n- 主题切换时再发 \`theme\` 消息即可热更新。`,
    },
  ];
  history.reset(seed);
  deps.setStats(current);
  stats.update(current, false);
  deps.refresh();

  let dark = true;
  const themeBtn = document.createElement("button");
  themeBtn.className = "preview-theme";
  themeBtn.type = "button";
  themeBtn.textContent = "浅色";
  themeBtn.addEventListener("click", () => {
    dark = !dark;
    applyPalette(dark ? "dark" : "light", dark ? defaultPalette : lightPalette);
    themeBtn.textContent = dark ? "浅色" : "深色";
  });
  stats.bar.querySelector(".stats-spacer")!.before(themeBtn);

  const pushStats = (streaming: boolean): void => {
    deps.setStats(current);
    stats.update(current, streaming);
  };

  function stream(id: string): void {
    const words = reply.split(/(?<=\s)/);
    let i = 0;
    const tick = (): void => {
      const chunk = words.slice(i, i + 3).join("");
      i += 3;
      history.delta(id, chunk);
      current = {
        ...current,
        steps: current.steps + 1,
        tokensPerSecond: 150 + Math.round(Math.random() * 70),
        totalTokens: current.totalTokens + chunk.length,
        contextPercent: Math.min(97, current.contextPercent + 0.2),
      };
      pushStats(true);
      if (i < words.length) {
        timers.set(id, window.setTimeout(tick, 42));
      } else {
        timers.delete(id);
        history.done(id, "done");
        current = { ...current, tokensPerSecond: 0, rounds: current.rounds + 1 };
        pushStats(false);
      }
    };
    timers.set(id, window.setTimeout(tick, 42));
  }

  window.setTimeout(() => {
    const user: WireMessage = { id: nid(), role: "user", text: "那复杂样式呢？markdown、代码块这些也放 Web 里？", state: "done", ts: Date.now() };
    history.append(user);
    const assistant: WireMessage = { id: nid(), role: "assistant", model: "Test", text: "", state: "streaming", ts: Date.now() };
    history.append(assistant);
    deps.refresh();
    stream(assistant.id);
  }, 700);

  return (action, id) => {
    if (action === "stop") {
      const timer = timers.get(id);
      if (timer) window.clearTimeout(timer);
      timers.delete(id);
      history.done(id, "done");
      current = { ...current, tokensPerSecond: 0 };
      pushStats(false);
      return;
    }
    const node = history.scroll.querySelector<HTMLElement>(`.msg-assistant[data-id="${id}"]`);
    if (!node) return;
    history.restart(id);
    stream(id);
  };
}