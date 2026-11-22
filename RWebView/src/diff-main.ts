import { applyPalette, connect, defaultPalette, onMessage } from "./bridge";
import { DiffPanel } from "./diffpanel";
import { setLocale } from "./i18n";

// 独立页：宿主把它放进右侧的 WebView2 壳子里，与对话区并排（不是覆盖层），
// 所以输入框和对话内容会自然退让。消息由宿主转发：changes.show / theme / locale。
const app = document.getElementById("app")!;
const panel = new DiffPanel(app);
panel.root.classList.add("open");

connect();

onMessage((msg) => {
  switch (msg.type) {
    case "init": {
      applyPalette(msg.theme ?? "dark", msg.colors ?? defaultPalette);
      if (msg.locale) setLocale(msg.locale);
      const latest = msg.changes?.[msg.changes.length - 1];
      if (latest) panel.show(latest);
      break;
    }
    case "theme":
      applyPalette(msg.theme, msg.colors);
      break;
    case "locale":
      setLocale(msg.locale);
      panel.relocalize();
      break;
    case "changes.show":
      panel.show(msg.changes, msg.path);
      break;
    default:
      break;
  }
});
