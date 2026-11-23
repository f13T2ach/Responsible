// 右侧栏的状态：谁在开、当前选中的标签。
// 视图用 React（迁移自 DSH 的侧栏视图），vanilla 模块通过这里驱动。
export type SidebarKind = "review" | "files";

export interface SidebarTab {
  /** 唯一键：review 用 turn，files 用固定值 */
  id: string;
  kind: SidebarKind;
  title: string;
  /** 该标签自己的数据（review 携带这一轮的改动） */
  data?: unknown;
}

interface SidebarState {
  open: boolean;
  tabs: SidebarTab[];
  activeId: string | null;
}

type Listener = () => void;

let state: SidebarState = { open: false, tabs: [], activeId: null };
const listeners = new Set<Listener>();

function emit(): void {
  for (const listener of listeners) listener();
}

export const sidebarStore = {
  subscribe(listener: Listener): () => void {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
  get(): SidebarState {
    return state;
  },
  toggle(): void {
    state = { ...state, open: !state.open };
    emit();
  },
  close(): void {
    if (!state.open) return;
    state = { ...state, open: false };
    emit();
  },
  /** 打开（或聚焦）一个标签，必要时刷新它的数据。 */
  openTab(tab: SidebarTab): void {
    const exists = state.tabs.some(item => item.id === tab.id);
    state = {
      open: true,
      tabs: exists
        ? state.tabs.map(item => (item.id === tab.id ? { ...item, ...tab } : item))
        : [...state.tabs, tab],
      activeId: tab.id,
    };
    emit();
  },
  activate(id: string): void {
    state = { ...state, activeId: id };
    emit();
  },
  closeTab(id: string): void {
    const tabs = state.tabs.filter(item => item.id !== id);
    const activeId = state.activeId === id ? (tabs[tabs.length - 1]?.id ?? null) : state.activeId;
    state = { tabs, activeId, open: tabs.length === 0 ? false : state.open };
    emit();
  },
};
