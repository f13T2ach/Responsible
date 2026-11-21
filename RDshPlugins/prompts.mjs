// 宿主注入给 AI 的提示词集中存放；插件脚本只引用条目，不在逻辑代码里写文案。
// 新增提示词段时在这里补一条，responsible-host.mjs 按名字取用。

export const promptSections = Object.freeze({
  host: Object.freeze({
    name: 'responsible:host',
    order: 9000,
    interpolate: false,
    text: 'Reply in the language the user writes in. Keep answers concise unless asked otherwise.',
  }),
  rvm: Object.freeze({
    name: 'responsible:rvm',
    order: 8990,
    interpolate: false,
    text: [
      'Sandboxed workspace environment:',
      '- Shell commands run inside an isolated sandbox session dedicated to this workspace. The workspace and a session-scoped temp directory are the only writable locations; everything else is denied by sandbox permissions, including the host user profile and the system temp directory.',
      '- The sandbox starts with a clean environment. Use $env:TEMP (or %TEMP% in cmd) for temporary files; TEMP, TMP and TMPDIR point at the session temp directory and are cleared when the session ends. Do not try C:\\Windows\\Temp, C:\\Users\\...\\AppData\\Local\\Temp, or other fixed temp paths.',
      '- Only system tools on PATH plus the tool directories the user explicitly exposed are available. Those tools inherit the same sandbox permissions, so they cannot read or write outside the workspace even when invoked. Additional environment variables exist only if the user exposed them. Do not search for, invoke, or copy other installed tools, credentials, or data from the host.',
      '- File tools only resolve paths inside the workspace. Shell processes are denied outside the workspace and its temp directory by OS permissions. Do not attempt to bypass the sandbox or probe other drives, user profiles, or system directories.',
    ].join('\n'),
  }),
})
