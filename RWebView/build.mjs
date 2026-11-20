import { build } from "esbuild";
import { readFile, writeFile, mkdir } from "node:fs/promises";

const js = await build({
  entryPoints: ["src/main.ts"],
  bundle: true,
  format: "iife",
  target: "chrome100",
  minify: true,
  write: false,
  legalComments: "none",
});

const css = await build({
  entryPoints: ["src/styles.css"],
  bundle: true,
  minify: true,
  write: false,
  legalComments: "none",
});

const template = await readFile("src/index.html", "utf8");
const html = template
  .replace("/*__CSS__*/", css.outputFiles[0].text)
  .replace("/*__JS__*/", js.outputFiles[0].text);

await mkdir("dist", { recursive: true });
await writeFile("dist/index.html", html);
console.log(`dist/index.html ${(html.length / 1024).toFixed(1)} KB`);