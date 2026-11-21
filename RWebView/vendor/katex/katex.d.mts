export interface KatexOptions {
  displayMode?: boolean;
  output?: "html" | "mathml" | "htmlAndMathml";
  throwOnError?: boolean;
  strict?: boolean | "ignore" | "warn" | "error";
  errorColor?: string;
  macros?: Record<string, string>;
}

declare const katex: {
  renderToString(tex: string, options?: KatexOptions): string;
  version: string;
};

export default katex;
