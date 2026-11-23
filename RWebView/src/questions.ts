import { send } from "./bridge";
import { t } from "./i18n";

// 迁移自 DSH 的提问卡片：底部接管条，逐题作答（单选/多选 + 自定义），带模型自己的倒计时。
export interface WireQuestionOption {
  label: string;
  description?: string | null;
}

export interface WireQuestion {
  id: string;
  question: string;
  detail?: string | null;
  header?: string | null;
  multiSelect?: boolean | null;
  options?: WireQuestionOption[] | null;
}

export interface WireQuestionBatch {
  id: string;
  questions: WireQuestion[];
  waitMs?: number | null;
}

interface AnswerState {
  selected: Set<string>;
  custom: string;
}

export class QuestionsDock {
  readonly root: HTMLDivElement;
  private readonly card: HTMLDivElement;
  private readonly title: HTMLSpanElement;
  private readonly countdown: HTMLSpanElement;
  private readonly body: HTMLDivElement;
  private readonly submit: HTMLButtonElement;
  private readonly skip: HTMLButtonElement;
  private batch: WireQuestionBatch | undefined;
  private answers: AnswerState[] = [];
  private timer = 0;
  private deadline = 0;
  private submitting = false;

  constructor(mount: HTMLElement) {
    this.root = document.createElement("div");
    this.root.className = "q-dock";

    this.card = document.createElement("div");
    this.card.className = "q-card";

    const head = document.createElement("div");
    head.className = "q-head";
    this.title = document.createElement("span");
    this.title.className = "q-title";
    this.countdown = document.createElement("span");
    this.countdown.className = "q-countdown";
    head.append(this.title, this.countdown);

    this.body = document.createElement("div");
    this.body.className = "q-body";

    const actions = document.createElement("div");
    actions.className = "q-actions";
    this.skip = document.createElement("button");
    this.skip.type = "button";
    this.skip.className = "q-button ghost";
    this.skip.textContent = t("questions.skip");
    this.skip.addEventListener("click", () => this.cancel());
    this.submit = document.createElement("button");
    this.submit.type = "button";
    this.submit.className = "q-button primary";
    this.submit.textContent = t("questions.submit");
    this.submit.addEventListener("click", () => this.commit());
    actions.append(this.skip, this.submit);

    this.card.append(head, this.body, actions);
    this.root.append(this.card);
    this.root.style.display = "none";
    mount.append(this.root);
  }

  /** 宿主推来待答问题；空数组收起。 */
  update(batches: WireQuestionBatch[]): void {
    const batch = batches[0];
    if (batch === undefined) {
      this.close();
      return;
    }
    if (this.batch?.id !== batch.id) {
      this.batch = batch;
      this.answers = batch.questions.map(() => ({ selected: new Set<string>(), custom: "" }));
      this.submitting = false;
      this.render();
      this.startCountdown(batch.waitMs ?? null);
    }
    this.root.style.display = "flex";
  }

  private close(): void {
    window.clearInterval(this.timer);
    this.batch = undefined;
    this.root.style.display = "none";
  }

  private startCountdown(waitMs: number | null): void {
    window.clearInterval(this.timer);
    if (waitMs === null || waitMs <= 0) {
      this.countdown.textContent = "";
      return;
    }
    this.deadline = Date.now() + waitMs;
    const tick = () => {
      const left = Math.max(0, this.deadline - Date.now());
      this.countdown.textContent = t("questions.left", Math.ceil(left / 1000));
      if (left === 0) {
        window.clearInterval(this.timer);
        this.countdown.textContent = t("questions.expired");
      }
    };
    tick();
    this.timer = window.setInterval(tick, 500);
  }

  private render(): void {
    const batch = this.batch;
    if (batch === undefined) return;
    this.title.textContent = batch.questions.length === 1
      ? t("questions.title")
      : t("questions.titleMany", batch.questions.length);
    this.body.replaceChildren();

    batch.questions.forEach((question, index) => {
      const block = document.createElement("div");
      block.className = "q-block";

      if (question.header) {
        const header = document.createElement("div");
        header.className = "q-block-header";
        header.textContent = question.header;
        block.append(header);
      }

      const text = document.createElement("div");
      text.className = "q-question";
      text.textContent = question.question;
      block.append(text);

      if (question.detail) {
        const detail = document.createElement("div");
        detail.className = "q-detail";
        detail.textContent = question.detail;
        block.append(detail);
      }

      const options = question.options ?? [];
      const list = document.createElement("div");
      list.className = "q-options";
      for (const option of options) {
        const row = document.createElement("button");
        row.type = "button";
        row.className = "q-option";
        const mark = document.createElement("span");
        mark.className = question.multiSelect === true ? "q-mark check" : "q-mark radio";
        const label = document.createElement("span");
        label.className = "q-option-label";
        label.textContent = option.label;
        row.append(mark, label);
        if (option.description) {
          const desc = document.createElement("span");
          desc.className = "q-option-desc";
          desc.textContent = option.description;
          row.append(desc);
        }
        row.addEventListener("click", () => {
          const state = this.answers[index];
          if (question.multiSelect === true) {
            if (state.selected.has(option.label)) state.selected.delete(option.label);
            else state.selected.add(option.label);
          } else {
            state.selected.clear();
            state.selected.add(option.label);
          }
          this.syncOption(row, state.selected.has(option.label));
          this.refreshSubmit();
        });
        list.append(row);
      }
      block.append(list);

      const custom = document.createElement("input");
      custom.type = "text";
      custom.className = "q-custom";
      custom.placeholder = t("questions.custom");
      custom.addEventListener("input", () => {
        this.answers[index].custom = custom.value;
        this.refreshSubmit();
      });
      block.append(custom);

      this.body.append(block);
    });

    this.refreshSubmit();
  }

  private syncOption(row: HTMLElement, active: boolean): void {
    row.classList.toggle("active", active);
  }

  private refreshSubmit(): void {
    const batch = this.batch;
    if (batch === undefined) return;
    const complete = batch.questions.every((_, index) => {
      const state = this.answers[index];
      return state.selected.size > 0 || state.custom.trim().length > 0;
    });
    this.submit.disabled = !complete || this.submitting;
    this.submit.classList.toggle("disabled", !complete || this.submitting);
  }

  private commit(): void {
    const batch = this.batch;
    if (batch === undefined || this.submitting) return;
    this.submitting = true;
    this.refreshSubmit();
    const answers = batch.questions.map((question, index) => {
      const state = this.answers[index];
      const custom = state.custom.trim();
      return {
        id: question.id,
        selected: [...state.selected],
        ...(custom.length > 0 ? { custom } : {}),
      };
    });
    send({ type: "questions.answer", id: batch.id, answers });
    this.close();
  }

  private cancel(): void {
    const batch = this.batch;
    if (batch === undefined) return;
    // 不答：交回内核，让模型按“用户没答”处理
    send({ type: "questions.answer", id: batch.id, answers: [] });
    this.close();
  }
}
