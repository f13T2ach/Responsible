import { renderTex } from "./tex";
import { t } from "./i18n";

// 极简 markdown：先整体转义再做块级/行内变换，消息文本不可注入 HTML。
const escapeHtml = (s: string): string =>
  s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

const fileIcon =
  '<svg class="link-icon" width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/></svg>';

const globeIcon =
  '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M3 12h18"/><path d="M12 3a15 15 0 0 1 0 18a15 15 0 0 1 0-18z"/></svg>';

const isWeb = (href: string): boolean => /^https?:\/\//i.test(href);
const isFile = (href: string): boolean => /^(file:\/\/|[A-Za-z]:[\\/]|\\\\|\/)/.test(href);

function hostOf(url: string): string {
  try {
    return new URL(url).hostname;
  } catch {
    return "";
  }
}

// href/label 均已转义；点击由 HistoryView 截获后交宿主，Web 自身不导航
function anchor(href: string, label: string): string {
  if (isWeb(href)) {
    const host = hostOf(href);
    const fav = `<span class="fav"><span class="fav-fallback">${globeIcon}</span>` +
      (host ? `<img class="fav-img" data-host="${host}" alt=""/>` : "") + `</span>`;
    return `<a class="link link-web" href="${href}" title="${href}" target="_blank" rel="noreferrer" data-href="${href}">${fav}<span class="link-text">${label}</span></a>`;
  }
  if (isFile(href)) {
    return `<a class="link link-file" href="${href}" title="${href}" data-href="${href}">${fileIcon}<span class="link-text">${label}</span></a>`;
  }
  return `<a class="link" href="${href}" target="_blank" rel="noreferrer" data-href="${href}">${label}</a>`;
}

const trimTail = (s: string): [string, string] => {
  const m = /[.,，。;；:：!！?？)）】」]+$/.exec(s);
  return m ? [s.slice(0, -m[0].length), m[0]] : [s, ""];
};

// 行内公式：$...$ 与 \(...\)；块级公式只在行首处理，避免误吞货币数字
const inlineMath = (s: string, hold: (tex: string, display: boolean) => string): string =>
  s
    .replace(/\\\[([\s\S]+?)\\\]/g, (_m, tex: string) => hold(tex, true))
    .replace(/\\\(([\s\S]+?)\\\)/g, (_m, tex: string) => hold(tex, false))
    .replace(/\$\$([^$]+?)\$\$/g, (_m, tex: string) => hold(tex, true))
    .replace(/(?<![\\$])\$(?![\s$])([^$\n]+?)(?<![\s$])\$(?!\$)/g, (_m, tex: string) => hold(tex, false));

function inline(raw: string, math: boolean): string {
  let s = raw;
  const codes: string[] = [];
  s = s.replace(/`([^`]+)`/g, (_m, code: string) => {
    codes.push(code);
    return `\u0000${codes.length - 1}\u0000`;
  });
  const maths: string[] = [];
  if (math) {
    s = inlineMath(s, (tex, display) => {
      maths.push(renderTex(tex, display));
      return `\u0002${maths.length - 1}\u0002`;
    });
  }
  s = escapeHtml(s);
  const links: string[] = [];
  const hold = (html: string): string => {
    links.push(html);
    return `\u0001${links.length - 1}\u0001`;
  };
  s = s.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_m, label: string, href: string) => hold(anchor(href, label)));
  s = s.replace(/(^|[\s（(【「>])(file:\/\/[^\s<>"]+)/g, (_m, pre: string, url: string) => {
    const [u, tail] = trimTail(url);
    return pre + hold(anchor(u, u)) + tail;
  });
  s = s.replace(/(^|[\s（(【「>])(https?:\/\/[^\s<>"]+)/g, (_m, pre: string, url: string) => {
    const [u, tail] = trimTail(url);
    return pre + hold(anchor(u, u)) + tail;
  });
  s = s.replace(/(^|[\s（(【「>])([A-Za-z]:[\\/][^\s<>"]+)/g, (_m, pre: string, path: string) => {
    const [p, tail] = trimTail(path);
    return pre + hold(anchor(p, p)) + tail;
  });
  s = s.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
  s = s.replace(/(^|[^*])\*([^*]+)\*/g, "$1<em>$2</em>");
  s = s.replace(/\u0001(\d+)\u0001/g, (_m, i: string) => links[Number(i)] ?? "");
  s = s.replace(/\u0002(\d+)\u0002/g, (_m, i: string) => maths[Number(i)] ?? "");
  s = s.replace(/\u0000(\d+)\u0000/g, (_m, i: string) => `<code class="inline">${codes[Number(i)]}</code>`);
  return s;
}

export interface CodeBlock {
  id: string;
  lang: string;
  code: string;
}

export interface MarkdownOptions {
  /** 流式期间不解析 TeX，避免半截公式闪出错标；落定后按 DSH settled 语法渲染。 */
  math?: boolean;
}

let blockSeq = 0;

const isSeparatorRow = (line: string): boolean => {
  const cells = splitRow(line);
  return cells.length > 0 && cells.every((cell) => /^:?-{1,}:?$/.test(cell.replace(/\s/g, "")));
};

const splitRow = (line: string): string[] => {
  let s = line.trim();
  if (s.startsWith("|")) s = s.slice(1);
  if (s.endsWith("|")) s = s.slice(0, -1);
  return s.split("|").map((cell) => cell.trim());
};

const alignOf = (cell: string): string => {
  const left = cell.startsWith(":");
  const right = cell.endsWith(":");
  if (left && right) return "center";
  return right ? "right" : left ? "left" : "";
};

const tableHtml = (lines: string[], start: number, math: boolean): { html: string; end: number } => {
  const header = splitRow(lines[start]);
  const aligns = splitRow(lines[start + 1]).map(alignOf);
  const cell = (tag: "th" | "td", text: string, index: number): string => {
    const align = aligns[index] ?? "";
    const style = align === "" ? "" : ` style="text-align:${align}"`;
    return `<${tag}${style}>${inline(text, math)}</${tag}>`;
  };
  const rows: string[] = [];
  let i = start + 2;
  while (i < lines.length && lines[i].includes("|") && lines[i].trim() !== "") {
    const cells = splitRow(lines[i]);
    rows.push(`<tr>${cells.map((text, index) => cell("td", text, index)).join("")}</tr>`);
    i++;
  }
  const wide = header.length >= 4 ? " md-table-wide" : "";
  const html =
    `<div class="md-table-scroll${wide}"><table>` +
      `<thead><tr>${header.map((text, index) => cell("th", text, index)).join("")}</tr></thead>` +
      (rows.length > 0 ? `<tbody>${rows.join("")}</tbody>` : "") +
    `</table></div>`;
  return { html, end: i - 1 };
};

// 块级公式：$$ 围栏（可同行）+ \[ \] 围栏；未闭合时按普通段落处理
const displayMathAt = (lines: string[], start: number, math: boolean): { tex: string; end: number } | null => {
  if (!math) return null;
  const line = lines[start].trim();
  const sameLine = /^\$\$(.+?)\$\$$/.exec(line);
  if (sameLine) return { tex: sameLine[1], end: start };
  if (line !== "$$" && !line.startsWith("\\[")) return null;
  const open = line.startsWith("\\[") ? "\\[" : "$$";
  const close = open === "\\[" ? "\\]" : "$$";
  const body: string[] = [];
  let first = line.slice(open.length);
  if (first.includes(close)) return { tex: first.slice(0, first.indexOf(close)), end: start };
  if (first !== "") body.push(first);
  for (let i = start + 1; i < lines.length; i++) {
    const current = lines[i];
    const at = current.indexOf(close);
    if (at >= 0) {
      body.push(current.slice(0, at));
      return { tex: body.join("\n"), end: i };
    }
    body.push(current);
  }
  return null;
};

export function renderMarkdown(text: string, blocks: Map<string, CodeBlock>, options: MarkdownOptions = {}): string {
  const math = options.math === true;
  const lines = text.split(/\r?\n/);
  const out: string[] = [];
  let para: string[] = [];
  let list: "ul" | "ol" | null = null;
  let quote: string[] = [];

  const flushPara = () => {
    if (para.length) {
      out.push(`<p>${para.map((line) => inline(line, math)).join("<br/>")}</p>`);
      para = [];
    }
  };
  const flushList = () => {
    if (list) {
      out.push(`</${list}>`);
      list = null;
    }
  };
  const flushQuote = () => {
    if (quote.length) {
      out.push(`<blockquote>${quote.map((line) => inline(line, math)).join("<br/>")}</blockquote>`);
      quote = [];
    }
  };
  const flushAll = () => {
    flushPara();
    flushList();
    flushQuote();
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const fence = /^```(\w*)\s*$/.exec(line);
    if (fence) {
      flushAll();
      const lang = fence[1] || "text";
      const body: string[] = [];
      while (i + 1 < lines.length && !/^```\s*$/.test(lines[i + 1])) body.push(lines[++i]);
      i++;
      const id = `cb${++blockSeq}`;
      blocks.set(id, { id, lang, code: body.join("\n") });
      out.push(
        `<div class="codeblock"><div class="codeblock-head"><span>${escapeHtml(lang)}</span>` +
          `<button class="code-copy" data-code="${id}" type="button">${t("copy")}</button></div>` +
          `<pre><code>${escapeHtml(body.join("\n"))}</code></pre></div>`,
      );
      continue;
    }
    const display = displayMathAt(lines, i, math);
    if (display) {
      flushAll();
      out.push(`<div class="md-math">${renderTex(display.tex.trim(), true)}</div>`);
      i = display.end;
      continue;
    }
    if (line.includes("|") && i + 1 < lines.length && isSeparatorRow(lines[i + 1])) {
      flushAll();
      const table = tableHtml(lines, i, math);
      out.push(table.html);
      i = table.end;
      continue;
    }
    const heading = /^(#{1,4})\s+(.*)$/.exec(line);
    if (heading) {
      flushAll();
      const level = heading[1].length + 2;
      out.push(`<h${level}>${inline(heading[2], math)}</h${level}>`);
      continue;
    }
    if (/^\s*(-{3,}|\*{3,})\s*$/.test(line)) {
      flushAll();
      out.push("<hr/>");
      continue;
    }
    const ul = /^\s*[-*]\s+(.*)$/.exec(line);
    const ol = /^\s*\d+[.)]\s+(.*)$/.exec(line);
    if (ul || ol) {
      flushPara();
      flushQuote();
      const kind = ul ? "ul" : "ol";
      if (list !== kind) {
        flushList();
        out.push(`<${kind}>`);
        list = kind;
      }
      out.push(`<li>${inline((ul ?? ol)![1], math)}</li>`);
      continue;
    }
    const q = /^>\s?(.*)$/.exec(line);
    if (q) {
      flushPara();
      flushList();
      quote.push(q[1]);
      continue;
    }
    if (line.trim() === "") {
      flushAll();
      continue;
    }
    flushList();
    flushQuote();
    para.push(line);
  }
  flushAll();
  return out.join("");
}
