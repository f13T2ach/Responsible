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
      '- Shell commands run inside a sandbox session dedicated to this workspace. Treat the workspace and its session temp directory as the only writable locations.',
      '- The workspace is exposed through a separate drive letter for shell commands. Shell writes are layered into the workspace\'s RVM area; file tools read and write the host copy directly, so shell changes may not be visible to file tools until they are merged.',
      '- TMP, TEMP and TMPDIR point at a session-scoped temporary directory that is cleared when the session ends. Never write to the system temp directory or other fixed paths.',
      '- Everything outside the workspace and its temp directory is out of scope. Do not read, write, or probe other drives, user profiles, credentials, or system directories, and do not look for workarounds.',
    ].join('\n'),
  }),
})
