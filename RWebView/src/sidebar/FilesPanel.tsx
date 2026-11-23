import { useEffect } from "react";
import { send } from "../bridge";
import { t } from "../i18n";
import type { SidebarTab } from "./store";

// 「工作区文件」标签：内容由宿主推 workspace.files 送来，这里只负责呈现。
export interface WireFileEntry {
  name: string;
  dir?: boolean | null;
  size?: number | null;
}

function formatSize(size: number | null | undefined): string {
  if (size === null || size === undefined || size <= 0) return "";
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  return `${(size / 1024 / 1024).toFixed(1)} MB`;
}

export function FilesPanel({ tab }: { tab: SidebarTab }) {
  const entries = (tab.data as WireFileEntry[] | undefined) ?? [];

  useEffect(() => {
    if (entries.length === 0) send({ type: "files.request" });
    // 只在挂载时请求一次；宿主的回答会把数据换掉
  }, [entries.length]);

  if (entries.length === 0) {
    return <div className="sidebar-placeholder">{t("sidebar.filesEmpty")}</div>;
  }

  return (
    <div className="files-panel">
      {entries.map(entry => (
        <div key={entry.name} className={entry.dir ? "files-row dir" : "files-row"}>
          <span className="files-icon">
            {entry.dir
              ? `<svg viewBox="0 0 16 16" width="14" height="14"><path d="M2.5 4.5h4l1.2 1.6h5.8v6.4h-11z" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>`
              : `<svg viewBox="0 0 16 16" width="14" height="14"><path d="M3.5 2.5h6l3 3v8h-9z" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>`}
          </span>
          <span className="files-name">{entry.name}</span>
          <span className="files-size">{entry.dir ? "" : formatSize(entry.size)}</span>
        </div>
      ))}
    </div>
  );
}
