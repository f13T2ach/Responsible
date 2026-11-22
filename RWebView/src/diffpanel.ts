import type { WireChanges } from "./bridge";
import { t } from "./i18n";

// 结构与尺寸对齐 DSH 的 ui-deliverables：header(38px) + file selector + tools + 双行号 diff。
// unified 行是 [oldNo | newNo | sign | text]，split 是左右各 [no | text]，行高 22px。

const icons = {
  close: `<svg viewBox="0 0 16 16" width="12" height="12" aria-hidden="true"><path d="M4 4l8 8M12 4l-8 8" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>`,
  compare: `<svg viewBox="0 0 16 16" width="15" height="15" aria-hidden="true"><rect x="2" y="3" width="5" height="10" rx="1" fill="none" stroke="currentColor" stroke-width="1.2"/><rect x="9" y="3" width="5" height="10" rx="1" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>`,
  wrap: `<svg viewBox="0 0 16 16" width="15" height="15" aria-hidden="true"><path d="M2 4h12M2 8h8a2 2 0 010 4H7M2 12h3" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/></svg>`,
  copy: `<svg viewBox="0 0 16 16" width="15" height="15" aria-hidden="true"><rect x="5" y="5" width="8" height="8" rx="1" fill="none" stroke="currentColor" stroke-width="1.2"/><path d="M3 11V3h8" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>`,
};

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

function statusOf(diff: string | undefined): "A" | "M" | "D" {
  if (diff === undefined) return "M";
  if (diff.includes("new file mode")) return "A";
  if (diff.includes("deleted file mode")) return "D";
  return "M";
}

/** 把 unified diff 切成 hunk + 带行号的行（行号来自 @@ -a,b +c,d @@）。 */
export function parseDiff(text: string): Hunk[] {
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
    if (line.startsWith("\\")) continue; // "\ No newline at end of file"
    if (line.startsWith("+")) {
      current.rows.push({ kind: "add", new: newNo++, text: line.slice(1) });
    } else if (line.startsWith("-")) {
      current.rows.push({ kind: "del", old: oldNo++, text: line.slice(1) });
    } else if (line.startsWith(" ") || line.length === 0) {
      const body = line.startsWith(" ") ? line.slice(1) : line;
      current.rows.push({ kind: "context", old: oldNo++, new: newNo++, text: body });
    }
  }
  return hunks;
}

/** 左右配对：连续 del 与 add 并排，context 两侧各一份。 */
function toSplitRows(rows: DiffRow[]): Array<{ left?: DiffRow; right?: DiffRow }> {
  const result: Array<{ left?: DiffRow; right?: DiffRow }> = [];
  let index = 0;
  while (index < rows.length) {
    const row = rows[index];
    if (row.kind === "context") {
      result.push({ left: row, right: row });
      index += 1;
      continue;
    }
    const dels: DiffRow[] = [];
    const adds: DiffRow[] = [];
    while (index < rows.length && rows[index].kind === "del") dels.push(rows[index++]);
    while (index < rows.length && rows[index].kind === "add") adds.push(rows[index++]);
    const count = Math.max(dels.length, adds.length);
    for (let i = 0; i < count; i += 1) result.push({ left: dels[i], right: adds[i] });
  }
  return result;
}

/**
 * 右侧改动面板：页签是这一轮改过的文件，正文按 DSH 的 review 布局渲染
 * （header 里的文件选择 + +x/-y + 工具按钮，body 支持 side-by-side 与 unified）。
 */
export class DiffPanel {
  readonly root: HTMLDivElement;
  private readonly tabsBar: HTMLDivElement;
  private readonly title: HTMLSpanElement;
  private readonly counts: HTMLSpanElement;
  private readonly compareTool: HTMLButtonElement;
  private readonly wrapTool: HTMLButtonElement;
  private readonly body: HTMLDivElement;
  private current?: WireChanges;
  private selected?: string;
  private split = true;
  private wrap = false;

  constructor(container: HTMLElement) {
    this.root = document.createElement("div");
    this.root.className = "diff-panel";

    // header：文件选择（当前文件）+ 行数 + 工具
    const header = document.createElement("div");
    header.className = "diff-header";
    this.title = document.createElement("span");
    this.title.className = "diff-selector";
    this.counts = document.createElement("span");
    this.counts.className = "diff-counts";
    const tools = document.createElement("div");
    tools.className = "diff-tools";

    this.compareTool = this.tool(icons.compare, () => {
      this.split = !this.split;
      this.render();
    });
    this.wrapTool = this.tool(icons.wrap, () => {
      this.wrap = !this.wrap;
      this.render();
    });
    const copyTool = this.tool(icons.copy, () => {
      const diff = this.currentDiff();
      if (diff !== undefined) void navigator.clipboard?.writeText(diff);
    });
    const closeTool = this.tool(icons.close, () => this.hide());
    tools.append(this.compareTool, this.wrapTool, copyTool, closeTool);
    header.append(this.title, this.counts, tools);

    this.tabsBar = document.createElement("div");
    this.tabsBar.className = "diff-tabs";
    this.body = document.createElement("div");
    this.body.className = "diff-body";

    this.root.append(header, this.tabsBar, this.body);
    container.append(this.root);
  }

  get isOpen(): boolean {
    return this.root.classList.contains("open");
  }

  show(changes: WireChanges, path?: string): void {
    this.current = changes;
    this.selected = path ?? changes.files[0]?.path;
    this.root.classList.add("open");
    this.render();
  }

  hide(): void {
    this.root.classList.remove("open");
  }

  toggle(changes: WireChanges): void {
    if (this.isOpen) this.hide();
    else this.show(changes);
  }

  relocalize(): void {
    if (this.current !== undefined) this.render();
  }

  private tool(inner: string, onClick: () => void): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "diff-tool";
    button.innerHTML = inner;
    button.addEventListener("click", onClick);
    return button;
  }

  private currentDiff(): string | undefined {
    const changes = this.current;
    if (changes === undefined) return undefined;
    return changes.diffs?.[this.selected ?? ""];
  }

  private render(): void {
    const changes = this.current;
    if (changes === undefined) return;
    const file = changes.files.find((candidate) => candidate.path === this.selected) ?? changes.files[0];
    this.selected = file?.path;
    const diff = file === undefined ? undefined : changes.diffs?.[file.path];

    this.title.textContent = file === undefined
      ? t("changes.panel", changes.total, changes.added, changes.deleted)
      : `${statusOf(diff)}  ${file.display}`;
    this.title.title = file?.path ?? "";
    this.counts.textContent = file === undefined
      ? `+${changes.added} -${changes.deleted}`
      : file.binary
        ? t("changes.binary")
        : `+${file.added} -${file.deleted}`;
    this.compareTool.setAttribute("aria-pressed", String(this.split));
    this.wrapTool.setAttribute("aria-pressed", String(this.wrap));

    this.renderTabs();
    this.body.replaceChildren();
    this.body.dataset.reviewView = this.split ? "split" : "unified";
    if (this.wrap) this.body.dataset.reviewWrap = "";
    else delete this.body.dataset.reviewWrap;

    if (diff === undefined || diff.length === 0) {
      const empty = document.createElement("div");
      empty.className = "diff-status";
      empty.textContent = file?.binary ? t("changes.binaryHint") : t("changes.noDiff");
      this.body.append(empty);
      return;
    }

    const hunks = parseDiff(diff);
    if (hunks.length === 0) {
      const pre = document.createElement("pre");
      pre.className = "diff-raw";
      pre.textContent = diff;
      this.body.append(pre);
      return;
    }

    if (this.split) {
      const columns = document.createElement("div");
      columns.className = "diff-columns";
      const left = document.createElement("div");
      left.className = "diff-column";
      left.dataset.diffSide = "left";
      const right = document.createElement("div");
      right.className = "diff-column";
      right.dataset.diffSide = "right";
      for (const hunk of hunks) {
        const hunkLeft = document.createElement("div");
        hunkLeft.className = "diff-hunk";
        const hunkRight = document.createElement("div");
        hunkRight.className = "diff-hunk";
        for (const h of [hunkLeft, hunkRight]) {
          const head = document.createElement("div");
          head.className = "diff-hunk-header";
          head.textContent = hunk.header;
          h.append(head);
        }
        for (const pair of toSplitRows(hunk.rows)) {
          hunkLeft.append(this.splitCell(pair.left, "left"));
          hunkRight.append(this.splitCell(pair.right, "right"));
        }
        left.append(hunkLeft);
        right.append(hunkRight);
      }
      columns.append(left, right);
      this.body.append(columns);
      return;
    }

    for (const hunk of hunks) {
      const block = document.createElement("div");
      block.className = "diff-hunk";
      const head = document.createElement("div");
      head.className = "diff-hunk-header";
      head.textContent = hunk.header;
      block.append(head);
      for (const row of hunk.rows) {
        const line = document.createElement("div");
        line.className = "diff-line " + row.kind;
        line.dataset.diffLine = row.kind;
        line.append(
          this.span("number", row.old === undefined ? "" : String(row.old)),
          this.span("number", row.new === undefined ? "" : String(row.new)),
          this.span("sign", row.kind === "add" ? "+" : row.kind === "del" ? "-" : ""),
          this.span("text", row.text));
        block.append(line);
      }
      this.body.append(block);
    }
  }

  private splitCell(row: DiffRow | undefined, side: "left" | "right"): HTMLDivElement {
    if (row === undefined) {
      const empty = document.createElement("div");
      empty.className = "diff-side-line empty";
      return empty;
    }
    const kind = side === "left" ? (row.kind === "del" ? "del" : "context") : (row.kind === "add" ? "add" : "context");
    const line = document.createElement("div");
    line.className = "diff-side-line " + kind;
    line.dataset.diffLine = kind;
    const no = side === "left" ? row.old : row.new;
    line.append(this.span("number", no === undefined ? "" : String(no)), this.span("text", row.text));
    return line;
  }

  private span(className: string, text: string): HTMLSpanElement {
    const span = document.createElement("span");
    span.className = "diff-" + className;
    span.textContent = text.length === 0 ? " " : text;
    return span;
  }

  private renderTabs(): void {
    this.tabsBar.replaceChildren();
    const changes = this.current;
    if (changes === undefined) return;
    for (const file of changes.files) {
      const diff = changes.diffs?.[file.path];
      const tab = document.createElement("button");
      tab.type = "button";
      tab.className = "diff-tab" + (file.path === this.selected ? " active" : "");
      tab.title = file.path;
      const badge = document.createElement("span");
      badge.className = "diff-status-badge s-" + statusOf(diff);
      badge.textContent = statusOf(diff);
      const name = document.createElement("span");
      name.className = "diff-name";
      name.textContent = file.display;
      const counts = document.createElement("span");
      counts.className = "diff-tab-counts";
      counts.textContent = file.binary ? t("changes.binary") : `+${file.added} -${file.deleted}`;
      tab.append(badge, name, counts);
      tab.addEventListener("click", () => {
        this.selected = file.path;
        this.render();
      });
      this.tabsBar.append(tab);
    }
  }
}
