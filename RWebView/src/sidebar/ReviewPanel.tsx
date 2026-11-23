import { useMemo, useState } from "react";
import type { SidebarTab } from "./store";
import { t } from "../i18n";
import type { WireChanges } from "../bridge";

// 迁移自 DSH 的 review 视图：一个标签里放这一轮的文件选择器 + 双行号 diff。

type RowKind = "add" | "del" | "context";

interface DiffRow {
  kind: RowKind;
  old?: number;
  new?: number;
  text: string;
}

interface Hunk {
  header: string;
  rows: DiffRow[];
}

function parseDiff(text: string): Hunk[] {
  const hunks: Hunk[] = [];
  let current: Hunk | undefined;
  let oldNo = 0;
  let newNo = 0;
  for (const raw of text.split("\n")) {
    const line = raw.replace(/\r$/, "");
    if (line.startsWith("@@")) {
      const match = /@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(line);
      oldNo = match ? Number(match[1]) : 0;
      newNo = match ? Number(match[2]) : 0;
      current = { header: line, rows: [] };
      hunks.push(current);
      continue;
    }
    if (current === undefined) continue;
    if (line.startsWith("\\")) continue;
    if (line.startsWith("+")) current.rows.push({ kind: "add", new: newNo++, text: line.slice(1) });
    else if (line.startsWith("-")) current.rows.push({ kind: "del", old: oldNo++, text: line.slice(1) });
    else {
      const body = line.startsWith(" ") ? line.slice(1) : line;
      current.rows.push({ kind: "context", old: oldNo++, new: newNo++, text: body });
    }
  }
  return hunks;
}

function splitRows(rows: DiffRow[]): Array<{ left?: DiffRow; right?: DiffRow }> {
  const out: Array<{ left?: DiffRow; right?: DiffRow }> = [];
  let index = 0;
  while (index < rows.length) {
    const row = rows[index];
    if (row.kind === "context") {
      out.push({ left: row, right: row });
      index += 1;
      continue;
    }
    const dels: DiffRow[] = [];
    const adds: DiffRow[] = [];
    while (index < rows.length && rows[index].kind === "del") dels.push(rows[index++]);
    while (index < rows.length && rows[index].kind === "add") adds.push(rows[index++]);
    const count = Math.max(dels.length, adds.length);
    for (let i = 0; i < count; i += 1) out.push({ left: dels[i], right: adds[i] });
  }
  return out;
}

function statusOf(diff: string | undefined): string {
  if (diff === undefined) return "M";
  if (diff.includes("new file mode")) return "A";
  if (diff.includes("deleted file mode")) return "D";
  return "M";
}

export function ReviewPanel({ tab }: { tab: SidebarTab }) {
  const changes = tab.data as WireChanges | undefined;
  const [fileIndex, setFileIndex] = useState(0);
  const [menuOpen, setMenuOpen] = useState(false);
  const [split, setSplit] = useState(true);

  const files = changes?.files ?? [];
  const file = files[Math.min(fileIndex, Math.max(files.length - 1, 0))];
  const diff = file === undefined ? undefined : changes?.diffs?.[file.path];
  const hunks = useMemo(() => (diff === undefined ? [] : parseDiff(diff)), [diff]);

  if (changes === undefined || file === undefined) {
    return <div className="sidebar-placeholder">{t("changes.noDiff")}</div>;
  }

  const counts = file.binary ? t("changes.binary") : `+${file.added} -${file.deleted}`;

  return (
    <div className="review-panel">
      <div className="review-header">
        <button type="button" className="review-selector" onClick={() => setMenuOpen(open => !open)}>
          <span className={"review-badge s-" + statusOf(diff)}>{statusOf(diff)}</span>
          <span className="review-file">{file.display}</span>
          <svg viewBox="0 0 16 16" width="10" height="10"><path d="M4 6l4 4 4-4" fill="none" stroke="currentColor" stroke-width="1.5" /></svg>
        </button>
        <span className="review-counts">{counts}</span>
        <div className="review-tools">
          <button type="button" className="review-tool" aria-pressed={split} onClick={() => setSplit(value => !value)} title={t("changes.compare")}>
            <svg viewBox="0 0 16 16" width="14" height="14"><rect x="2" y="3" width="5" height="10" rx="1" fill="none" stroke="currentColor" stroke-width="1.2" /><rect x="9" y="3" width="5" height="10" rx="1" fill="none" stroke="currentColor" stroke-width="1.2" /></svg>
          </button>
        </div>
        {menuOpen && (
          <div className="review-menu">
            {files.map((item, index) => (
              <button
                key={item.path}
                type="button"
                className={index === fileIndex ? "review-menu-item active" : "review-menu-item"}
                onClick={() => {
                  setFileIndex(index);
                  setMenuOpen(false);
                }}
              >
                <span className="review-menu-name">{item.display}</span>
                <span className="review-menu-counts">{item.binary ? t("changes.binary") : `+${item.added} -${item.deleted}`}</span>
              </button>
            ))}
          </div>
        )}
      </div>

      <div className={split ? "review-body split" : "review-body"} data-review-view={split ? "split" : "unified"}>
        {hunks.length === 0 && (
          <div className="sidebar-placeholder">{file.binary ? t("changes.binaryHint") : t("changes.noDiff")}</div>
        )}
        {split
          ? (
            <div className="review-columns">
              <div className="review-column">
                {hunks.map((hunk, index) => (
                  <div key={"l" + index} className="review-hunk">
                    <div className="review-hunk-head">{hunk.header}</div>
                    {splitRows(hunk.rows).map((pair, rowIndex) => (
                      <div key={rowIndex} className={"review-line " + (pair.left === undefined ? "empty" : pair.left.kind === "del" ? "del" : "context")}>
                        <span className="review-no">{pair.left?.old ?? ""}</span>
                        <span className="review-text">{pair.left?.text ?? ""}</span>
                      </div>
                    ))}
                  </div>
                ))}
              </div>
              <div className="review-column">
                {hunks.map((hunk, index) => (
                  <div key={"r" + index} className="review-hunk">
                    <div className="review-hunk-head">{hunk.header}</div>
                    {splitRows(hunk.rows).map((pair, rowIndex) => (
                      <div key={rowIndex} className={"review-line " + (pair.right === undefined ? "empty" : pair.right.kind === "add" ? "add" : "context")}>
                        <span className="review-no">{pair.right?.new ?? ""}</span>
                        <span className="review-text">{pair.right?.text ?? ""}</span>
                      </div>
                    ))}
                  </div>
                ))}
              </div>
            </div>
          )
          : hunks.map((hunk, index) => (
            <div key={index} className="review-hunk">
              <div className="review-hunk-head">{hunk.header}</div>
              {hunk.rows.map((row, rowIndex) => (
                <div key={rowIndex} className={"review-line unified " + row.kind}>
                  <span className="review-no">{row.old ?? ""}</span>
                  <span className="review-no">{row.new ?? ""}</span>
                  <span className="review-sign">{row.kind === "add" ? "+" : row.kind === "del" ? "−" : ""}</span>
                  <span className="review-text">{row.text}</span>
                </div>
              ))}
            </div>
          ))}
      </div>
    </div>
  );
}
