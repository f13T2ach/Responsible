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

export interface WireMessage {
  id: string;
  role: "user" | "assistant";
  text: string;
  state: "streaming" | "done" | "error";
  model?: string;
  ts: number;
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
  | { type: "init"; protocol: 1; theme: "dark" | "light"; colors: Palette; messages: WireMessage[]; stats: WireStats }
  | { type: "theme"; theme: "dark" | "light"; colors: Palette }
  | { type: "history.reset"; messages: WireMessage[] }
  | { type: "message.append"; message: WireMessage }
  | { type: "message.delta"; id: string; text: string }
  | { type: "message.restart"; id: string }
  | { type: "message.done"; id: string; state: "done" | "error" }
  | { type: "stats"; stats: WireStats }
  | { type: "agent.state"; state: "running" | "idle" };

export type WebMessage =
  | { type: "ready"; protocol: 1 }
  | { type: "copyText"; text: string }
  | { type: "retry"; id: string }
  | { type: "stop"; id: string };

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