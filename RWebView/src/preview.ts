// 无 WebView2 宿主（浏览器直开 dist/index.html）时的预览驱动： seeded 会话 + 模拟流式回复 + 主题切换。
import { applyPalette, defaultPalette, type Palette, type WireMessage, type WireStats } from "./bridge";
import type { HistoryView } from "./history";
import type { StatsView } from "./stats";
import { onLocaleChange, t } from "./i18n";

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

> 通信走 postMessage 双向通道：Web 上报 ready / branch / stop / copyText，C# 下发 append / delta / update / done / stats。

这样滚动、流式光标、代码块复制都在 Web 内完成，C# 侧只维护会话状态这一份数据源。`;

interface Deps {
  history: HistoryView;
  stats: StatsView;
  setStats: (s: WireStats) => void;
  refresh: () => void;
}

let seq = 0;
const nid = (): string => `p${++seq}`;

export function installPreview(deps: Deps): (action: "stop", id: string) => void {
  const { history, stats } = deps;
  const now = Date.now();
  const base: WireStats = { rounds: 2, steps: 14, tokensPerSecond: 0, totalTokens: 3260, cacheHitRate: 96.4, contextPercent: 18 };
  let current = { ...base };
  const timers = new Map<string, number>();

  const previewUsage = {
    uncachedInputTokens: 1200,
    outputTokens: 640,
    totalTokens: 2940,
    cacheReadTokens: 1100,
    cacheWriteTokens: 0,
    reasoningTokens: 120,
    routes: [{ provider: "deepseek-account", model: "deepseek-flash" }],
  };
  const seed: WireMessage[] = [
    { id: nid(), role: "user", text: "帮我把 Moe 主页面的历史消息区做成可滚动的会话视图", state: "done", ts: now - 240000 },
    {
      id: nid(),
      role: "assistant",
      model: "Test",
      state: "done",
      ts: now - 238000,
      reasoning:
        "会话视图需要同时满足三点：消息模型要稳定、滚动容器要能吸附底部、流式更新不能重建整棵 DOM。\n先看现有 HistoryView 的边界，再把思考链和文件改动作为独立通道挂上去。",
      reasoningMs: 4200,
      usage: previewUsage,
      branchSeq: 41,
      text: `好的。会话视图需要三件事：**消息模型**、**滚动容器**、**流式更新通道**。\n\n1. 消息区分 user / assistant 两种角色，状态机为 streaming → done / error；\n2. 滚动容器默认吸附底部，用户上滚后出现"回到底部"；\n3. 增量文本以 delta 推送，避免整棵 DOM 重建。\n\n\`\`\`csharp\npublic sealed record ChatMessage(string Id, string Role, string Text, string State);\n\`\`\``,
    },
    { id: nid(), role: "user", text: "背景色要和 WPF 内容区完全一致，不能闪白", state: "done", ts: now - 120000 },
    {
      id: nid(),
      role: "assistant",
      model: "Test",
      state: "done",
      ts: now - 118000,
      usage: { ...previewUsage, totalTokens: 2488630, cacheReadTokens: 2412000, outputTokens: 41230, uncachedInputTokens: 35400 },
      branchSeq: 97,
      text: `明白：颜色单一来源在 WPF 资源字典，Web 侧只接收下发值。\n\n- 导航前设置 \`DefaultBackgroundColor\`；\n- \`init\` 载荷带完整调色板；\n- 主题切换时再发 \`theme\` 消息即可热更新。\n\n样式定义见 [Moe.xaml](D:/Responsible/Responsible/View/Moe.xaml)，仓库在 https://github.com/f13T2ach/Responsible 。`,
    },
    { id: nid(), role: "user", text: "表格和公式也一起看看，顺便确认一轮里可以多次思考", state: "done", ts: now - 62000 },
    {
      id: nid(),
      role: "assistant",
      model: "Test",
      state: "done",
      ts: now - 60000,
      reasoning: "先确认依赖与样式口径。\n工具核对之后，再给出表格和公式的渲染方案。",
      usage: previewUsage,
      branchSeq: 120,
      text: "先看一版渲染对比：\n\n| 方案 | 渲染位置 | 公式 | 表格 |\n| --- | :--- | :---: | :---: |\n| WPF 原生 | 宿主 | 需另配 | 需手写 |\n| Web + KaTeX | 渲染器 | 原生支持 | 原生支持 |\n\n渲染器方案在样式一致性上更稳。\n\n依赖已经就位，行内公式 $E = mc^2$ 与块级公式一起验证：\n\n$$\\sum_{i=1}^{n} i = \\frac{n(n+1)}{2}$$\n\n宽表格（列数多时横向滚动，不压缩正文）：\n\n| 能力 | 渲染器 | 宿主 | 备注 | 状态 | 入口 |\n| --- | --- | --- | --- | --- | --- |\n| 表格 | GFM | 无 | 对齐与滚动 | 已完成 | markdown.ts |\n| 公式 | KaTeX | 无 | 落定后解析 | 已完成 | tex.ts |\n| 工具链 | 时序分段 | 转发帧 | 可多次思考 | 已完成 | history.ts |",
      parts: [
        {
          kind: "reasoning",
          text: "先确认依赖与样式口径：表格沿用 GFM，公式在落定后才解析，避免流式期间闪出错标。",
          ms: 3100,
        },
        {
          kind: "text",
          text: "先看一版渲染对比：\n\n| 方案 | 渲染位置 | 公式 | 表格 |\n| --- | :--- | :---: | :---: |\n| WPF 原生 | 宿主 | 需另配 | 需手写 |\n| Web + KaTeX | 渲染器 | 原生支持 | 原生支持 |\n\n渲染器方案在样式一致性上更稳。",
        },
        {
          kind: "tool",
          tool: {
            callId: "preview-tool-1",
            name: "bash",
            variant: "bash",
            summary: "grep -n katex RWebView/package.json",
            state: "ok",
            ts: now - 60000,
            input: '{"command":"grep -n katex RWebView/package.json"}',
            output: 'package.json:  "katex": "^0.18.7"',
          },
        },
        {
          kind: "reasoning",
          text: "依赖已确认，继续补公式排版细节，再给出最终结论。",
          ms: 2400,
        },
        {
          kind: "text",
          text: "依赖已经就位，行内公式 $E = mc^2$ 与块级公式一起验证：\n\n$$\\sum_{i=1}^{n} i = \\frac{n(n+1)}{2}$$\n\n宽表格（列数多时横向滚动，不压缩正文）：\n\n| 能力 | 渲染器 | 宿主 | 备注 | 状态 | 入口 |\n| --- | --- | --- | --- | --- | --- |\n| 表格 | GFM | 无 | 对齐与滚动 | 已完成 | markdown.ts |\n| 公式 | KaTeX | 无 | 落定后解析 | 已完成 | tex.ts |\n| 工具链 | 时序分段 | 转发帧 | 可多次思考 | 已完成 | history.ts |",
        },
      ],
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
  themeBtn.textContent = t("preview.light");
  themeBtn.addEventListener("click", () => {
    dark = !dark;
    applyPalette(dark ? "dark" : "light", dark ? defaultPalette : lightPalette);
    themeBtn.textContent = dark ? t("preview.light") : t("preview.dark");
  });
  stats.bar.querySelector(".stats-spacer")!.before(themeBtn);
  onLocaleChange(() => {
    themeBtn.textContent = dark ? t("preview.light") : t("preview.dark");
  });

  const pushStats = (streaming: boolean): void => {
    deps.setStats(current);
    stats.update(current, streaming);
  };

  let changesShown = false;

  function stream(message: WireMessage): void {
    const id = message.id;
    const words = reply.split(/(?<=\s)/);
    let i = 0;
    let seen = "";
    let toolShown = false;
    const liveTool = { callId: "preview-live-tool", name: "bash", variant: "bash" as const, summary: "pnpm build", state: "running" as const, ts: Date.now() };
    // 不能直接展开 message：渲染器已把 parts 缓存到该对象上，带 parts 的 update 会走历史分支
    const updateLive = (state: "streaming" | "done", tools: (typeof liveTool)[]): void => {
      history.update({ id: message.id, role: "assistant", model: message.model, text: seen, state, ts: message.ts, tools });
    };
    const tick = (): void => {
      const chunk = words.slice(i, i + 3).join("");
      i += 3;
      seen += chunk;
      history.delta(id, chunk);
      // 中途插入一次工具调用，验证“正文 → 工具 → 正文”的时序拼接
      if (!toolShown && i >= 9) {
        toolShown = true;
        updateLive("streaming", [liveTool]);
      }
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
        history.update({
          id: message.id,
          role: "assistant",
          model: message.model,
          text: seen,
          state: "done",
          ts: message.ts,
          tools: [{ ...liveTool, state: "ok", output: "构建完成" }],
          usage: previewUsage,
          branchSeq: 140,
        });
        current = { ...current, tokensPerSecond: 0, rounds: current.rounds + 1 };
        pushStats(false);
        if (!changesShown) {
          changesShown = true;
          history.appendChanges({
            turn: 3,
            cwd: "D:\\Responsible",
            total: 3,
            added: 17,
            deleted: 1,
            files: [
              { path: "Responsible/View/Moe.xaml", display: "Responsible/View/Moe.xaml", added: 9, deleted: 1 },
              { path: "RWebView/src/history.ts", display: "RWebView/src/history.ts", added: 6, deleted: 0 },
              { path: "RWebView/src/styles.css", display: "RWebView/src/styles.css", added: 2, deleted: 0 },
            ],
          });
          deps.refresh();
        }
      }
    };
    const thoughts = [
      "先把现有链路过一遍：",
      "历史消息由 HistoryView 渲染，",
      "消息与统计都来自 ChatSessionService。",
      "思考链与文件改动各走独立通道，",
      "避免和正文增量互相干扰。",
    ];
    let ti = 0;
    const think = (): void => {
      if (ti < thoughts.length) {
        history.reasoningDelta(id, thoughts[ti++]);
        timers.set(id, window.setTimeout(think, 70));
        return;
      }
      tick();
    };
    timers.set(id, window.setTimeout(think, 60));
  }

  window.setTimeout(() => {
    const user: WireMessage = { id: nid(), role: "user", text: "那复杂样式呢？markdown、代码块这些也放 Web 里？", state: "done", ts: Date.now() };
    history.append(user);
    const assistant: WireMessage = { id: nid(), role: "assistant", model: "Test", text: "", state: "streaming", ts: Date.now() };
    history.append(assistant);
    deps.refresh();
    stream(assistant);
  }, 700);

  return (action, id) => {
    if (action !== "stop") return;
    const timer = timers.get(id);
    if (timer) window.clearTimeout(timer);
    timers.delete(id);
    history.done(id, "done");
    current = { ...current, tokensPerSecond: 0 };
    pushStats(false);
  };
}