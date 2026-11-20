// Responsible 预载插件：经 cordis patch 挂载进 dsh（--profile sdk --patch ...），
// 在不 fork 内核的前提下改写部署 persona 与宿主约定。纯 ESM，无需构建。
export const name = 'responsible-host'

export const inject = ['systemPrompt']

export function apply(ctx, config = {}) {
  ctx.systemPrompt.section({
    name: 'responsible:host',
    order: 9000,
    interpolate: false,
    text:
      config.text ??
      'You are embedded in Responsible, a WPF desktop host. ' +
      'The host renders your markdown in a WebView2 history pane and mirrors token usage into its status bar. ' +
      'Reply in the language the user writes in. Keep answers concise unless asked otherwise.',
  })
}