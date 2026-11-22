import katex from "../vendor/katex/katex.mjs";

// 与 DSH 同一条三级回落：严格渲染 → 忽略严格告警 → 原文错误标记
export function renderTex(tex: string, displayMode: boolean): string {
  try {
    return katex.renderToString(tex, { displayMode, throwOnError: true });
  } catch {
    try {
      return katex.renderToString(tex, { displayMode, strict: "ignore", throwOnError: false });
    } catch {
      return `<span class="katex-error" style="color:var(--critical)" title="Formula parse error">${escapeTex(tex)}</span>`;
    }
  }
}

const escapeTex = (text: string): string =>
  text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
