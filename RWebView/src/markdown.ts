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

function inline(raw: string): string {
  let s = escapeHtml(raw);
  const codes: string[] = [];
  s = s.replace(/`([^`]+)`/g, (_m, code: string) => {
    codes.push(code);
    return `\u0000${codes.length - 1}\u0000`;
  });
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
  s = s.replace(/\u0000(\d+)\u0000/g, (_m, i: string) => `<code class="inline">${codes[Number(i)]}</code>`);
  return s;
}

export interface CodeBlock {
  id: string;
  lang: string;
  code: string;
}

let blockSeq = 0;

export function renderMarkdown(text: string, blocks: Map<string, CodeBlock>): string {
  const lines = text.split(/\r?\n/);
  const out: string[] = [];
  let para: string[] = [];
  let list: "ul" | "ol" | null = null;
  let quote: string[] = [];

  const flushPara = () => {
    if (para.length) {
      out.push(`<p>${para.map(inline).join("<br/>")}</p>`);
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
      out.push(`<blockquote>${quote.map(inline).join("<br/>")}</blockquote>`);
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
          `<button class="code-copy" data-code="${id}" type="button">复制</button></div>` +
          `<pre><code>${escapeHtml(body.join("\n"))}</code></pre></div>`,
      );
      continue;
    }
    const heading = /^(#{1,4})\s+(.*)$/.exec(line);
    if (heading) {
      flushAll();
      const level = heading[1].length + 2;
      out.push(`<h${level}>${inline(heading[2])}</h${level}>`);
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
      out.push(`<li>${inline((ul ?? ol)![1])}</li>`);
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