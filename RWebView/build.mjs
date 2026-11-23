import { build } from "esbuild";
import { readFile, writeFile, mkdir } from "node:fs/promises";

const replaceAsync = async (text, pattern, replacer) => {
  const pieces = [];
  let last = 0;
  for (const match of text.matchAll(pattern)) {
    pieces.push(text.slice(last, match.index));
    pieces.push(await replacer(...match));
    last = match.index + match[0].length;
  }
  pieces.push(text.slice(last));
  return pieces.join("");
};

// JSX：React 只用于右侧栏/审批这些迁移过来的视图，vanilla 模块继续共存
const jsx = {
  loader: { ".tsx": "tsx" },
  jsx: "automatic",
  define: { "process.env.NODE_ENV": "\"production\"" },
};

const js = await build({
  entryPoints: ["src/main.ts"],
  bundle: true,
  format: "iife",
  target: "chrome100",
  minify: true,
  write: false,
  legalComments: "none",
  ...jsx,
});

const css = await build({
  entryPoints: ["src/styles.css"],
  bundle: true,
  minify: true,
  write: false,
  legalComments: "none",
});

// KaTeX 字体内联成 data URI：页面用 NavigateToString 加载，没有可解析的相对资源路径
const katexDir = "vendor/katex";
let katexCss = await readFile(`${katexDir}/katex.min.css`, "utf8");
katexCss = await replaceAsync(
  katexCss,
  /src:url\(fonts\/([^)]+\.woff2)\) format\("woff2"\),url\([^)]+\) format\("woff"\),url\([^)]+\) format\("truetype"\)/g,
  async (_match, file) => {
    const font = await readFile(`${katexDir}/fonts/${file}`);
    return `src:url(data:font/woff2;base64,${font.toString("base64")}) format("woff2")`;
  },
);

// 用函数式替换：替换串里的 $$、$& 等会被 String.replace 当成特殊模式
const template = await readFile("src/index.html", "utf8");
const html = template
  .replace("/*__CSS__*/", () => `${katexCss}\n${css.outputFiles[0].text}`)
  .replace("/*__JS__*/", () => js.outputFiles[0].text);

// 右侧改动面板是独立页面：宿主用另一个 WebView2 承载，与对话区并排
const diffJs = await build({
  entryPoints: ["src/diff-main.ts"],
  bundle: true,
  format: "iife",
  target: "chrome100",
  minify: true,
  write: false,
  legalComments: "none",
  ...jsx,
});

const diffTemplate = await readFile("src/diff.html", "utf8");
const diffHtml = diffTemplate
  .replace("/*__CSS__*/", () => css.outputFiles[0].text)
  .replace("/*__JS__*/", () => diffJs.outputFiles[0].text);

await mkdir("dist", { recursive: true });
await writeFile("dist/index.html", html);
console.log(`dist/index.html ${(html.length / 1024).toFixed(1)} KB`);
await writeFile("dist/diff.html", diffHtml);
console.log(`dist/diff.html ${(diffHtml.length / 1024).toFixed(1)} KB`);