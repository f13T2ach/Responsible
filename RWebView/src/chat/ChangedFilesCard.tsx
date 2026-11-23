import { useState } from "react";
import type { WireChanges } from "../bridge";
import { t } from "../i18n";

// 迁移自 DSH 的 turn 改动卡片：单文件只留 header，多文件是 header + 可折叠的行列表。
const COLLAPSED_ROWS = 4;

const icons = {
  file: `<svg viewBox="0 0 16 16" width="20" height="20"><path d="M3.5 2.5h6l3 3v8h-9z" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>`,
  code: `<svg viewBox="0 0 16 16" width="20" height="20"><path d="M6 4L2.5 8 6 12M10 4l3.5 4L10 12" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>`,
  chevron: `<svg viewBox="0 0 16 16" width="12" height="12"><path d="M4 6l4 4 4-4" fill="none" stroke="currentColor" stroke-width="1.5"/></svg>`,
};

export function ChangedFilesCard({
  changes,
  onOpen,
}: {
  changes: WireChanges;
  onOpen: (index: number) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const single = changes.total === 1 ? changes.files[0] : undefined;
  const foldable = changes.files.length > COLLAPSED_ROWS;
  const rows = foldable && !expanded ? changes.files.slice(0, COLLAPSED_ROWS) : changes.files;
  const counts = single?.binary
    ? t("changes.binary")
    : `+${changes.added} −${changes.deleted}`;

  return (
    <div className="cf-card" data-single={single === undefined ? undefined : ""}>
      <button type="button" className="cf-head" onClick={() => onOpen(0)}>
        <span className="cf-tile" dangerouslySetInnerHTML={{ __html: single === undefined ? icons.code : icons.file }} />
        <span className="cf-titles">
          <span className="cf-title">
            {single === undefined ? t("changes.edited", changes.total) : single.display}
          </span>
          <span className="cf-stat">{counts}</span>
        </span>
      </button>
      {single === undefined && (
        <>
          <div className="cf-rows">
            {rows.map((file, index) => (
              <button
                key={file.path}
                type="button"
                className="cf-row"
                title={file.path}
                onClick={() => onOpen(index)}
              >
                <span className="cf-row-icon" dangerouslySetInnerHTML={{ __html: icons.file }} />
                <span className="cf-row-name">{file.display}</span>
                <span className="cf-row-counts">
                  {file.binary ? t("changes.binary") : `+${file.added} −${file.deleted}`}
                </span>
              </button>
            ))}
          </div>
          {foldable && (
            <button type="button" className="cf-more" onClick={() => setExpanded(value => !value)}>
              <span dangerouslySetInnerHTML={{ __html: icons.chevron }} />
              {expanded ? t("changes.fold") : t("changes.more", changes.total - COLLAPSED_ROWS)}
            </button>
          )}
        </>
      )}
    </div>
  );
}
