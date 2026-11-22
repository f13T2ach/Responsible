export type Locale = "zh-CN" | "en-US";

type Dict = Record<string, string>;

const zh: Dict = {
  "empty.title": "Make a response",
  "loading": "正在加载会话…",
  "copy": "复制",
  "copied": "已复制",
  "stop": "停止",
  "branch": "分支",
  "branchUnavailable": "仅可从已完成轮次的最后一条消息分支",
  "branchNew": "在新对话中分支",
  "backBottom": "回到底部",
  "generating": "生成中",
  "interrupted": "生成中断",
  "preview.noBranch": "浏览器预览：不创建分支",
  "preview.noLink": "浏览器预览：不打开外部链接",
  "tool.bash": "运行命令",
  "tool.read": "读取",
  "tool.search": "搜索",
  "tool.write": "写入",
  "tool.edit": "编辑",
  "tool.code": "代码",
  "tool.mcp": "MCP",
  "tool.tools": "工具调用",
  "tool.running": "运行中",
  "tool.error": "失败",
  "tool.stopped": "已停止",
  "print.input": "输入",
  "print.output": "输出",
  "thinking": "思考中",
  "thoughtSeconds": "已思考 · {0}s",
  "thought": "已思考",
  "usage.label": "用量 {0}",
  "usage.title": "本轮用量",
  "usage.providerModel": "提供方 / 模型",
  "usage.cacheHit": "缓存命中",
  "usage.uncachedInput": "未缓存输入",
  "usage.cacheRead": "缓存读取",
  "usage.cacheWrite": "缓存写入",
  "usage.output": "输出",
  "usage.reasoning": "（其中推理 {0} tok）",
  "changes.edited": "已编辑 {0} 个文件",
  "changes.binary": "二进制",
  "changes.oversized": "过大",
  "changes.more": "还有 {0} 个文件",
  "stats.title": "调用统计",
  "stats.rounds": "轮",
  "stats.steps": "步",
  "stats.cacheHit": "缓存命中",
  "stats.context": "上下文",
  "stats.idle": "空闲",
  "stats.running": "运行中",
  "stats.generating": "生成中",
  "rail.jump": "跳转到第 {0} 轮",
  "rail.turn": "第 {0} 轮",
  "rail.code": "代码",
  "rail.codeBlock": "代码块",
  "preview.light": "浅色",
  "preview.dark": "深色",
};

const en: Dict = {
  "empty.title": "Make a response",
  "loading": "Loading conversation…",
  "copy": "Copy",
  "copied": "Copied",
  "stop": "Stop",
  "branch": "Branch",
  "branchUnavailable": "Only the last message of a completed turn can be branched",
  "branchNew": "Branch in a new conversation",
  "backBottom": "Back to bottom",
  "generating": "Generating",
  "interrupted": "Generation interrupted",
  "preview.noBranch": "Browser preview: branching is disabled",
  "preview.noLink": "Browser preview: external links are disabled",
  "tool.bash": "Run command",
  "tool.read": "Read",
  "tool.search": "Search",
  "tool.write": "Write",
  "tool.edit": "Edit",
  "tool.code": "Code",
  "tool.mcp": "MCP",
  "tool.tools": "Tool call",
  "tool.running": "Running",
  "tool.error": "Failed",
  "tool.stopped": "Stopped",
  "print.input": "Input",
  "print.output": "Output",
  "thinking": "Thinking",
  "thoughtSeconds": "Thought for {0}s",
  "thought": "Thought",
  "usage.label": "Usage {0}",
  "usage.title": "Usage this turn",
  "usage.providerModel": "Provider / Model",
  "usage.cacheHit": "Cache hit",
  "usage.uncachedInput": "Uncached input",
  "usage.cacheRead": "Cache read",
  "usage.cacheWrite": "Cache write",
  "usage.output": "Output",
  "usage.reasoning": "(incl. reasoning {0} tok)",
  "changes.edited": "Edited {0} files",
  "changes.binary": "Binary",
  "changes.oversized": "Too large",
  "changes.more": "{0} more files",
  "stats.title": "Call stats",
  "stats.rounds": "rounds",
  "stats.steps": "steps",
  "stats.cacheHit": "Cache hit",
  "stats.context": "Context",
  "stats.idle": "Idle",
  "stats.running": "Running",
  "stats.generating": "Generating",
  "rail.jump": "Jump to turn {0}",
  "rail.turn": "Turn {0}",
  "rail.code": "code",
  "rail.codeBlock": "code block",
  "preview.light": "Light",
  "preview.dark": "Dark",
};

const dictionaries: Record<Locale, Dict> = { "zh-CN": zh, "en-US": en };

let locale: Locale = "zh-CN";
const listeners = new Set<() => void>();

export function getLocale(): Locale {
  return locale;
}

export function setLocale(value: string | undefined | null): void {
  const next: Locale = value?.startsWith("en") ? "en-US" : "zh-CN";
  document.documentElement.lang = next;
  if (next === locale) return;
  locale = next;
  for (const listener of [...listeners]) listener();
}

export function onLocaleChange(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function t(key: string, ...args: (string | number)[]): string {
  const raw = dictionaries[locale][key] ?? dictionaries["en-US"][key] ?? key;
  return args.reduce<string>((text, arg, index) => text.replace(`{${index}}`, String(arg)), raw);
}