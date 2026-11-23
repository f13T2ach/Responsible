import { useEffect, useRef } from "react";
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

function FolderIcon() {
  return (
    <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true">
      <path d="M2.5 4.5h4l1.2 1.6h5.8v6.4h-11z" fill="none" stroke="currentColor" strokeWidth="1.2" />
    </svg>
  );
}

function FileIcon() {
  return (
    <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true">
      <path d="M3.5 2.5h6l3 3v8h-9z" fill="none" stroke="currentColor" strokeWidth="1.2" />
    </svg>
  );
}

export function FilesPanel({ tab }: { tab: SidebarTab }) {
  const entries = (tab.data as WireFileEntry[] | undefined) ?? [];
  // 只在首次挂载时请求一次，避免数据回填后再次触发
  const requested = useRef(false);

  useEffect(() => {
    if (requested.current) return;
    requested.current = true;
    if (entries.length === 0) send({ type: "files.request" });
  }, [entries.length]);

  if (entries.length === 0) {
    return <div className="sidebar-placeholder">{t("sidebar.filesEmpty")}</div>;
  }

  return (
    <div className="files-panel">
      {entries.map(entry => (
        <div key={entry.name} className={entry.dir ? "files-row dir" : "files-row"}>
          <span className="files-icon">{entry.dir ? <FolderIcon /> : <FileIcon />}</span>
          <span className="files-name">{entry.name}</span>
          <span className="files-size">{entry.dir ? "" : formatSize(entry.size)}</span>
        </div>
      ))}
    </div>
  );
}
