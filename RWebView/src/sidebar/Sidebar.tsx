import { useSyncExternalStore } from "react";
import { createRoot } from "react-dom/client";
import { sidebarStore, type SidebarTab } from "./store";
import { t } from "../i18n";
import { ReviewPanel } from "./ReviewPanel";

// 迁移自 DSH 的右侧栏骨架：一个可开关的侧栏，空态是入口卡片，有内容时是一排标签页。
const icons = {
  sidebar: `<svg viewBox="0 0 16 16" width="14" height="14"><rect x="2" y="3" width="12" height="10" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.2"/><path d="M10 3v10" stroke="currentColor" stroke-width="1.2"/></svg>`,
  files: `<svg viewBox="0 0 16 16" width="20" height="20"><path d="M2.5 4.5h4l1.2 1.6h5.8v6.4h-11z" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>`,
  terminal: `<svg viewBox="0 0 16 16" width="20" height="20"><path d="M3 4l3 3-3 3M8.5 11h4.5" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/></svg>`,
  browser: `<svg viewBox="0 0 16 16" width="20" height="20"><circle cx="8" cy="8" r="5.5" fill="none" stroke="currentColor" stroke-width="1.2"/><path d="M2.6 8h10.8M8 2.6c1.6 1.6 1.6 9.2 0 10.8M8 2.6c-1.6 1.6-1.6 9.2 0 10.8" fill="none" stroke="currentColor" stroke-width="1.1"/></svg>`,
  more: `<svg viewBox="0 0 16 16" width="20" height="20"><rect x="2.5" y="2.5" width="4.5" height="4.5" rx="1" fill="none" stroke="currentColor" stroke-width="1.2"/><rect x="9" y="2.5" width="4.5" height="4.5" rx="1" fill="none" stroke="currentColor" stroke-width="1.2"/><rect x="2.5" y="9" width="4.5" height="4.5" rx="1" fill="none" stroke="currentColor" stroke-width="1.2"/><rect x="9" y="9" width="4.5" height="4.5" rx="1" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>`,
};

function tabContent(tab: SidebarTab | undefined) {
  if (tab === undefined) return null;
  switch (tab.kind) {
    case "review":
      return <ReviewPanel tab={tab} />;
    case "files":
      return <div className="sidebar-placeholder">{t("sidebar.filesEmpty")}</div>;
  }
}

function StartPage() {
  const entries = [
    { icon: icons.files, title: t("sidebar.entry.files"), hint: t("sidebar.entry.filesHint") },
    { icon: icons.terminal, title: t("sidebar.entry.terminal"), hint: t("sidebar.entry.terminalHint") },
    { icon: icons.browser, title: t("sidebar.entry.browser"), hint: t("sidebar.entry.browserHint") },
  ];
  return (
    <div className="sidebar-start">
      <div className="sidebar-start-list">
        {entries.map(entry => (
          <button key={entry.title} type="button" className="sidebar-entry" disabled>
            <span className="sidebar-entry-icon" dangerouslySetInnerHTML={{ __html: entry.icon }} />
            <span className="sidebar-entry-text">
              <span className="sidebar-entry-title">{entry.title}</span>
              <span className="sidebar-entry-hint">{entry.hint}</span>
            </span>
          </button>
        ))}
        <button type="button" className="sidebar-entry" disabled>
          <span className="sidebar-entry-icon" dangerouslySetInnerHTML={{ __html: icons.more }} />
          <span className="sidebar-entry-text">
            <span className="sidebar-entry-title">{t("sidebar.entry.more")}</span>
          </span>
        </button>
      </div>
    </div>
  );
}

function SidebarBody() {
  const state = useSyncExternalStore(sidebarStore.subscribe, sidebarStore.get);
  const active = state.tabs.find(tab => tab.id === state.activeId);

  return (
    <aside className={state.open ? "sidebar open" : "sidebar"}>
      <header className="sidebar-head">
        <span className="sidebar-title">{t("sidebar.title")}</span>
        <button type="button" className="sidebar-close" onClick={() => sidebarStore.close()} aria-label={t("sidebar.close")}>
          <svg viewBox="0 0 16 16" width="12" height="12"><path d="M4 4l8 8M12 4l-8 8" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" /></svg>
        </button>
      </header>
      {state.tabs.length > 0 && (
        <div className="sidebar-tabs">
          {state.tabs.map(tab => (
            <button
              key={tab.id}
              type="button"
              className={tab.id === state.activeId ? "sidebar-tab active" : "sidebar-tab"}
              onClick={() => sidebarStore.activate(tab.id)}
            >
              <span className="sidebar-tab-title">{tab.title}</span>
              <span
                className="sidebar-tab-close"
                role="button"
                aria-label={t("sidebar.closeTab")}
                onClick={event => {
                  event.stopPropagation();
                  sidebarStore.closeTab(tab.id);
                }}
              >
                ×
              </span>
            </button>
          ))}
        </div>
      )}
      <div className="sidebar-body">
        {active === undefined ? <StartPage /> : tabContent(active)}
      </div>
    </aside>
  );
}

export function mountSidebar(host: HTMLElement): void {
  createRoot(host).render(<SidebarBody />);
}

/** 右上角的展开按钮：挂到对话区顶部，和 DSH 一样常驻。 */
export function mountSidebarToggle(host: HTMLElement): void {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "sidebar-toggle";
  button.innerHTML = icons.sidebar;
  button.title = t("sidebar.toggle");
  button.addEventListener("click", () => sidebarStore.toggle());
  const mount = () => {
    host.append(button);
    const sync = () => button.classList.toggle("active", sidebarStore.get().open);
    sidebarStore.subscribe(sync);
    sync();
  };
  if (host.isConnected) mount();
  else new MutationObserver((_, observer) => {
    if (!host.isConnected) return;
    observer.disconnect();
    mount();
  }).observe(document.body, { childList: true, subtree: true });
}
