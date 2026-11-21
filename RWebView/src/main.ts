import { applyPalette, connect, defaultPalette, onMessage, send, type HostMessage, type WireStats } from "./bridge";
import { HistoryView, icons } from "./history";
import { StatsView } from "./stats";
import { installPreview } from "./preview";

const app = document.getElementById("app")!;

const stats = new StatsView(app);
const history = new HistoryView(app);

const empty = document.createElement("div");
empty.className = "empty";
empty.innerHTML = `${icons.sparkleBig}<span class="title">Make a response</span>`;
app.append(empty);

const toast = document.createElement("div");
toast.className = "toast";
document.body.append(toast);
let toastTimer = 0;

function showToast(text: string): void {
  toast.textContent = text;
  toast.classList.add("show");
  window.clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => toast.classList.remove("show"), 1600);
}

let streaming = false;
let agentRunning = false;
let lastStats: WireStats = { rounds: 0, steps: 0, tokensPerSecond: 0, totalTokens: 0, cacheHitRate: 0, contextPercent: 0 };

let wasEmpty = true;

function refreshEmpty(): void {
  const isEmpty = history.count === 0;
  empty.style.display = isEmpty ? "flex" : "none";
  stats.bar.style.display = isEmpty ? "none" : "flex";
  history.root.style.display = isEmpty ? "none" : "flex";
  if (wasEmpty && !isEmpty) history.snapBottom();
  wasEmpty = isEmpty;
}

function refreshStreaming(): void {
  streaming = false;
  history.scroll.querySelectorAll(".msg-assistant.streaming").forEach(() => {
    streaming = true;
  });
  stats.update(lastStats, streaming, agentRunning);
}

const connected = connect();

function copyText(text: string): void {
  if (connected) {
    send({ type: "copyText", text });
  } else if (navigator.clipboard) {
    void navigator.clipboard.writeText(text);
  }
  showToast("已复制");
}

history.onCopyCode = copyText;
history.onAction = (action, id) => {
  if (action === "copy") {
    const node = history.scroll.querySelector<HTMLElement>(`.msg-assistant[data-id="${id}"] .msg-body`);
    copyText(node?.innerText ?? "");
    history.markCopied(id);
    return;
  }
  if (connected) send({ type: action, id });
  else previewAction(action, id);
};

history.onOpenLink = (href) => {
  if (connected) send({ type: "openLink", href });
  else showToast("浏览器预览：不打开外部链接");
};

let previewAction: (action: "retry" | "stop", id: string) => void = () => {};

onMessage((msg: HostMessage) => {
  switch (msg.type) {
    case "init":
      applyPalette(msg.theme, msg.colors);
      lastStats = msg.stats;
      history.reset(msg.messages);
      for (const changes of msg.changes ?? []) history.appendChanges(changes);
      refreshEmpty();
      refreshStreaming();
      break;
    case "theme":
      applyPalette(msg.theme, msg.colors);
      break;
    case "history.reset":
      history.reset(msg.messages);
      refreshEmpty();
      refreshStreaming();
      break;
    case "message.append":
      history.append(msg.message);
      refreshEmpty();
      refreshStreaming();
      break;
    case "message.delta":
      history.delta(msg.id, msg.text);
      break;
    case "reasoning.delta":
      history.reasoningDelta(msg.id, msg.text);
      break;
    case "message.restart":
      history.restart(msg.id);
      refreshStreaming();
      break;
    case "message.done":
      history.done(msg.id, msg.state);
      refreshStreaming();
      break;
    case "changes.append":
      history.appendChanges(msg.changes);
      refreshEmpty();
      break;
    case "toast":
      showToast(msg.text);
      break;
    case "stats":
      lastStats = msg.stats;
      stats.update(lastStats, streaming, agentRunning);
      break;
    case "agent.state":
      agentRunning = msg.state === "running";
      stats.update(lastStats, streaming, agentRunning);
      break;
  }
});

applyPalette("dark", defaultPalette);
refreshEmpty();
stats.update(lastStats, false);

if (connected) {
  send({ type: "ready", protocol: 1 });
} else {
  previewAction = installPreview({ history, stats, setStats: (s) => (lastStats = s), refresh: refreshEmpty });
}