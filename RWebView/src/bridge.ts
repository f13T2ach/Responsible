// C# ↔ Web 通信契约。C# 侧镜像见 Responsible/Infrastructure/WebBridge.cs。
// Web → C#: window.chrome.webview.postMessage(JSON.stringify(WebMessage))
// C# → Web: ExecuteScriptAsync("window.rwebReceive(<json string literal>)")

export interface Palette {
  bg: string;
  surface: string;
  surface2: string;
  border: string;
  text: string;
  text2: string;
  text3: string;
  accent: string;
  success: string;
  caution: string;
  critical: string;
  codeBg: string;
}

export interface WireUsageRoute {
  provider: string;
  model: string;
}

// DSH token-meter 的精确轮次用量：所有计费尝试求和，可选桶只在每次尝试都报告时出现
export interface WireUsage {
  uncachedInputTokens: number;
  outputTokens: number;
  totalTokens: number;
  cacheReadTokens?: number;
  cacheWriteTokens?: number;
  reasoningTokens?: number;
  routes?: WireUsageRoute[];
}

export interface WireTool {
  callId: string;
  name: string;
  variant: "bash" | "read" | "search" | "write" | "edit" | "code" | "mcp" | "tools";
  summary: string;
  state: "running" | "ok" | "error" | "stopped";
  ts: number;
  output?: string;
  input?: string;
  isError?: boolean;
}

// 一轮内的时序分段：推理、正文、工具调用按发生顺序排列，流式时由前端增量维护
export type WirePart =
  | { kind: "reasoning"; text: string; ms?: number }
  | { kind: "text"; text: string }
  | { kind: "tool"; tool: WireTool };

export interface WireMessage {
  id: string;
  role: "user" | "assistant";
  text: string;
  state: "streaming" | "done" | "error";
  model?: string;
  ts: number;
  reasoning?: string;
  reasoningMs?: number;
  usage?: WireUsage;
  branchSeq?: number;
  branchUnavailable?: boolean;
  tools?: WireTool[];
  parts?: WirePart[];
}

export interface WireChangedFile {
  path: string;
  display: string;
  added: number;
  deleted: number;
  binary?: boolean;
  oversized?: boolean;
}

export interface WireFileEntry {
  name: string;
  dir?: boolean | null;
  size?: number | null;
}

export interface WireApproval {
  id: string;
  toolName: string;
  reason?: string;
  callId?: string;
  dangerous?: boolean;
}

export interface WireChanges {
  turn: number;
  cwd: string;
  total: number;
  added: number;
  deleted: number;
  files: WireChangedFile[];
  /// 文本文件的 unified diff（二进制/超限文件没有条目），供右侧面板展示
  diffs?: Record<string, string>;
}

export interface WireStats {
  rounds: number;
  steps: number;
  tokensPerSecond: number;
  totalTokens: number;
  cacheHitRate: number;
  contextPercent: number;
}

export type HostMessage =
  | { type: "init"; protocol: 1; theme: "dark" | "light"; colors: Palette; locale?: string; messages: WireMessage[]; stats: WireStats; changes?: WireChanges[]; loading?: boolean }
  | { type: "theme"; theme: "dark" | "light"; colors: Palette }
  | { type: "locale"; locale: string }
  | { type: "history.reset"; messages: WireMessage[] }
  | { type: "history.loading"; loading: boolean }
  | { type: "message.append"; message: WireMessage }
  | { type: "message.delta"; id: string; text: string }
  | { type: "reasoning.delta"; id: string; text: string }
  | { type: "message.restart"; id: string }
  | { type: "message.update"; message: WireMessage }
  | { type: "message.done"; id: string; state: "done" | "error" }
  | { type: "changes.append"; changes: WireChanges }
  | { type: "changes.show"; changes: WireChanges; path?: string }
  | { type: "approval.request"; approvals: WireApproval[] }
  | { type: "workspace.files"; entries: WireFileEntry[] }
  | { type: "changes.reset"; changes?: WireChanges[] }
  | { type: "stats"; stats: WireStats }
  | { type: "toast"; text: string }
  | { type: "agent.state"; state: "running" | "idle" };

export type WebMessage =
  | { type: "ready"; protocol: 1 }
  | { type: "copyText"; text: string }
  | { type: "branch"; id: string }
  | { type: "stop"; id: string }
  | { type: "openLink"; href: string }
  | { type: "showDiff"; turn: number; path?: string; index?: number }
  | { type: "approval.answer"; id: string; text: "allowed-once" | "rejected" }
  | { type: "files.request" }
  | { type: "clientError"; message: string };

export const defaultPalette: Palette = {
  bg: "#202020",
  surface: "#2b2b2b",
  surface2: "#323232",
  border: "#3a3a3a",
  text: "#ffffff",
  text2: "#c8c8c8",
  text3: "#8a8a8a",
  accent: "#60cdff",
  success: "#6ccb5f",
  caution: "#fce100",
  critical: "#ff99a4",
  codeBg: "#1a1a1a",
};

type Listener = (msg: HostMessage) => void;

const listeners = new Set<Listener>();
let host: { postMessage: (data: string) => void } | null = null;

declare global {
  interface Window {
    chrome?: { webview?: { postMessage: (data: string) => void; addEventListener: (t: "message", cb: (e: { data: string }) => void) => void } };
    rwebReceive?: (payload: string | HostMessage) => void;
  }
}

export function connect(): boolean {
  const webview = window.chrome?.webview;
  if (!webview) return false;
  host = webview;
  webview.addEventListener("message", (e) => dispatch(typeof e.data === "string" ? JSON.parse(e.data) : e.data));
  return true;
}

export function send(msg: WebMessage): void {
  host?.postMessage(JSON.stringify(msg));
}

export function onMessage(listener: Listener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function dispatch(msg: HostMessage): void {
  for (const listener of listeners) listener(msg);
}

// C# 入口：ExecuteScriptAsync 调用；字符串与对象两种载荷都接受
window.rwebReceive = (payload) => dispatch(typeof payload === "string" ? JSON.parse(payload) : payload);

export function applyPalette(theme: "dark" | "light", colors: Palette): void {
  // 让原生控件（滚动条、输入框）跟随主题
  document.documentElement.style.colorScheme = theme;
  const root = document.documentElement;
  root.dataset.theme = theme;
  const vars: Record<string, string> = {
    "--bg": colors.bg,
    "--surface": colors.surface,
    "--surface-2": colors.surface2,
    "--border": colors.border,
    "--text": colors.text,
    "--text-2": colors.text2,
    "--text-3": colors.text3,
    "--accent": colors.accent,
    "--success": colors.success,
    "--caution": colors.caution,
    "--critical": colors.critical,
    "--code-bg": colors.codeBg,
  };
  for (const [key, value] of Object.entries(vars)) root.style.setProperty(key, value);
}