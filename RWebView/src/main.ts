import { applyPalette, connect, defaultPalette, onMessage, send, type HostMessage, type WireStats } from "./bridge";
import { HistoryView, icons } from "./history";

import { StatsView } from "./stats";
import { ApprovalDock } from "./approval";
import { QuestionsDock } from "./questions";
import { mountSidebar, mountSidebarToggle } from "./sidebar/Sidebar";
import { sidebarStore } from "./sidebar/store";

import { installPreview } from "./preview";
import { setLocale, t } from "./i18n";
import type { WireChanges } from "./bridge";

const app = document.getElementById("app")!;

// 右侧栏要挤压对话区：把所有对话内容放进一列，侧栏作为兄弟节点
const chatColumn = document.createElement("div");
chatColumn.className = "chat-column";
app.append(chatColumn);
const sidebarHost = document.createElement("div");
sidebarHost.className = "sidebar-host";
app.append(sidebarHost);
const stats = new StatsView(chatColumn);
stats.bindTasksToggle();
const history = new HistoryView(chatColumn);
// 审批卡片悬浮在对话区底部；RVM 会话的请求在宿主侧就被拒绝，不会走到这里
const approvalDock = new ApprovalDock(document.body);
const questionsDock = new QuestionsDock(document.body);
mountSidebarToggle(chatColumn);
try {
  mountSidebar(sidebarHost);
} catch (error) {
  sidebarHost.textContent = "sidebar mount error: " + String(error);
}
// 改动 diff 在右侧栏里渲染（与 DSH 的 review 标签同一语义）
function openReview(changes: WireChanges, index: number): void {
  sidebarStore.openTab({
    id: `review:${changes.turn}:${index}`,
    kind: "review",
    title: t("sidebar.reviewTab", changes.turn),
    data: changes,
  });
}
history.onToggleDiff = (changes) => openReview(changes, 0);
history.onShowDiff = (changes, _path, index) => openReview(changes, index);

const empty = document.createElement("div");
empty.className = "empty";
empty.innerHTML = `${icons.sparkleBig}<span class="title">${t("empty.title")}</span>`;
chatColumn.append(empty);

const loading = document.createElement("div");
loading.className = "loading";
loading.innerHTML = `<span class="spinner"></span><span class="title">${t("loading")}</span>`;
chatColumn.append(loading);

const toast = document.createElement("div");
toast.className = "toast";
document.body.append(toast);
let toastTimer = 0;

function paintChrome(): void {
  empty.innerHTML = `${icons.sparkleBig}<span class="title">${t("empty.title")}</span>`;
  loading.innerHTML = `<span class="spinner"></span><span class="title">${t("loading")}</span>`;
}

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
let sessionLoading = false;

function refreshEmpty(): void {
  const isEmpty = history.count === 0;
  const showLoading = sessionLoading && isEmpty;
  loading.style.display = showLoading ? "flex" : "none";
  empty.style.display = isEmpty && !sessionLoading ? "flex" : "none";
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

// 前端异常转发给宿主日志（WebView 里看不到 console）
window.addEventListener("error", event => send({ type: "clientError", message: `${event.message} @ ${event.filename}:${event.lineno}` }));
window.addEventListener("unhandledrejection", event => send({ type: "clientError", message: String(event.reason) }));

const connected = connect();

function copyText(text: string): void {
  if (connected) {
    send({ type: "copyText", text });
  } else if (navigator.clipboard) {
    void navigator.clipboard.writeText(text);
  }
  showToast(t("copied"));
}

history.onCopyCode = copyText;
history.onAction = (action, id) => {
  if (action === "copy") {
    copyText(history.textOf(id));
    history.markCopied(id);
    return;
  }
  if (action === "branch") {
    if (connected) send({ type: "branch", id });
    else showToast(t("preview.noBranch"));
    return;
  }
  if (connected) send({ type: action, id });
  else previewAction(action, id);
};

history.onOpenLink = (href) => {
  if (connected) send({ type: "openLink", href });
  else showToast(t("preview.noLink"));
};

let previewAction: (action: "stop", id: string) => void = () => {};

onMessage((msg: HostMessage) => {
  switch (msg.type) {
    case "init":
      setLocale(msg.locale);
      paintChrome();
      applyPalette(msg.theme, msg.colors);
      lastStats = msg.stats;
      sessionLoading = msg.loading === true;
      history.reset(msg.messages);
      for (const changes of msg.changes ?? []) history.appendChanges(changes);
      refreshEmpty();
      refreshStreaming();
      break;
    case "theme":
      applyPalette(msg.theme, msg.colors);
      break;
    case "locale":
      setLocale(msg.locale);
      paintChrome();
      history.relocalize();
      stats.relocalize();
      break;
    case "history.reset":
      history.reset(msg.messages);
      refreshEmpty();
      refreshStreaming();
      break;
    case "history.loading":
      sessionLoading = msg.loading;
      refreshEmpty();
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
    case "message.update":
      history.update(msg.message);
      refreshStreaming();
      break;
    case "message.done":
      history.done(msg.id, msg.state);
      refreshStreaming();
      break;
    case "questions.request":
      questionsDock.update(msg.batches ?? []);
      break;
    case "tasks.running":
      stats.setTasks(msg.tasks ?? []);
      break;
    case "workspace.files":
      sidebarStore.openTab({ id: "files", kind: "files", title: t("sidebar.entry.files"), data: msg.entries });
      break;
    case "approval.request":
      approvalDock.update(msg.approvals ?? []);
      break;
    case "changes.reset":
      history.replaceChanges(msg.changes ?? []);
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