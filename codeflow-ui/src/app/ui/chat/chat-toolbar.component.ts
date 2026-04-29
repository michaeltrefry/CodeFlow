import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  Output,
  computed,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { LlmProviderKey, LlmProviderModelOption } from '../../core/models';

/**
 * HAA-16/HAA-17 — toolbar that sits beneath the chat composer. Carries:
 *  - the per-conversation provider/model dropdowns (selection persists per user via
 *    {@link AssistantPreferencesService}, defaults seeded from the admin endpoint).
 *  - a live token chip showing cumulative input/output tokens for the conversation, with a
 *    warning state as the cap from HAA-15 is approached.
 *
 * Embeddable in any surface — the homepage main pane and the right-rail sidebar both mount the
 * shared chat-panel which renders this toolbar.
 */
@Component({
  selector: 'cf-chat-toolbar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="toolbar">
      <div class="selectors">
        <label class="select-wrap" [title]="'Provider'">
          <span class="select-label">Provider</span>
          <select
            class="select"
            [disabled]="disabled || providerOptions().length === 0"
            [ngModel]="provider ?? ''"
            (ngModelChange)="onProviderChange($event)"
            aria-label="Assistant provider"
          >
            <option value="">— default —</option>
            @for (p of providerOptions(); track p) {
              <option [value]="p">{{ providerDisplayName(p) }}</option>
            }
          </select>
        </label>
        <label class="select-wrap" [title]="'Model'">
          <span class="select-label">Model</span>
          <select
            class="select mono"
            [disabled]="disabled || modelOptions().length === 0"
            [ngModel]="model ?? ''"
            (ngModelChange)="onModelChange($event)"
            aria-label="Assistant model"
          >
            <option value="">— default —</option>
            @for (m of modelOptions(); track m) {
              <option [value]="m">{{ m }}</option>
            }
          </select>
        </label>
      </div>
      <div class="tokens" [attr.data-state]="tokenState()" [attr.aria-label]="tokenAriaLabel()">
        <span class="token-chip" title="Input tokens (this conversation)">
          <span class="token-arrow">↑</span>
          <span class="token-num">{{ formatNum(inputTokens) }}</span>
          <span class="token-suffix">in</span>
        </span>
        <span class="token-chip" title="Output tokens (this conversation)">
          <span class="token-arrow">↓</span>
          <span class="token-num">{{ formatNum(outputTokens) }}</span>
          <span class="token-suffix">out</span>
        </span>
        @if (capDisplay()) {
          <span class="token-cap" [title]="capTitle()">
            / {{ capDisplay() }}
          </span>
        }
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .toolbar {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      padding: 6px 10px;
      border-top: 1px solid var(--border, rgba(255,255,255,0.06));
      background: var(--surface, #131519);
      font-size: 11px;
      color: var(--text-muted, #9aa3b2);
      flex-wrap: wrap;
    }
    .selectors {
      display: flex;
      gap: 8px;
      flex: 1 1 auto;
      min-width: 0;
    }
    .select-wrap {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      min-width: 0;
    }
    .select-label {
      text-transform: uppercase;
      letter-spacing: 0.06em;
      font-size: 10px;
      color: var(--text-muted, #9aa3b2);
    }
    .select {
      appearance: none;
      background: var(--bg, #0B0C0E);
      color: var(--text, #E7E9EE);
      border: 1px solid var(--border, rgba(255,255,255,0.08));
      border-radius: 4px;
      padding: 3px 22px 3px 8px;
      font-size: 11px;
      max-width: 200px;
      background-image: linear-gradient(45deg, transparent 50%, var(--text-muted, #9aa3b2) 50%),
                        linear-gradient(135deg, var(--text-muted, #9aa3b2) 50%, transparent 50%);
      background-position: calc(100% - 12px) 50%, calc(100% - 8px) 50%;
      background-size: 4px 4px, 4px 4px;
      background-repeat: no-repeat;
    }
    .select:focus {
      outline: none;
      border-color: var(--accent, #5765ff);
    }
    .select.mono { font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace); }
    .select:disabled { opacity: 0.55; cursor: not-allowed; }

    .tokens {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 2px 8px;
      border-radius: 999px;
      border: 1px solid var(--border, rgba(255,255,255,0.08));
      background: var(--surface-2, rgba(255,255,255,0.03));
      flex: 0 0 auto;
    }
    .tokens[data-state="warn"] {
      border-color: var(--sem-amber, #d29922);
      color: var(--sem-amber, #d29922);
    }
    .tokens[data-state="full"] {
      border-color: var(--sem-red, #f85149);
      color: var(--sem-red, #f85149);
      background: rgba(248, 81, 73, 0.08);
    }
    .token-chip {
      display: inline-flex;
      align-items: center;
      gap: 3px;
      font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace);
      font-size: 11px;
    }
    .token-arrow { opacity: 0.7; }
    .token-num { font-variant-numeric: tabular-nums; }
    .token-suffix { opacity: 0.7; text-transform: lowercase; }
    .token-cap {
      font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace);
      font-size: 11px;
      opacity: 0.7;
    }
  `],
})
export class ChatToolbarComponent {
  /** Available (provider, model) options. Empty array = nothing configured. */
  @Input() set models(value: ReadonlyArray<LlmProviderModelOption>) {
    this.modelsSig.set(value ?? []);
  }

  /** Current provider selection. Null = use server default. */
  @Input() provider: LlmProviderKey | null = null;

  /** Current model selection. Null = use server default for the chosen provider. */
  @Input() model: string | null = null;

  /** Cumulative input tokens captured against the conversation. */
  @Input() inputTokens = 0;

  /** Cumulative output tokens captured against the conversation. */
  @Input() outputTokens = 0;

  /** Conversation-level cap from admin settings; null = uncapped. */
  @Input() cap: number | null = null;

  /** Disables the dropdowns (e.g. while a turn is streaming or while loading defaults). */
  @Input() disabled = false;

  @Output() readonly selectionChanged = new EventEmitter<{
    provider: LlmProviderKey | null;
    model: string | null;
  }>();

  private readonly modelsSig = signal<ReadonlyArray<LlmProviderModelOption>>([]);

  protected readonly providerOptions = computed<LlmProviderKey[]>(() => {
    const seen = new Set<LlmProviderKey>();
    for (const m of this.modelsSig()) {
      seen.add(m.provider);
    }
    return [...seen];
  });

  protected readonly modelOptions = computed<string[]>(() => {
    const list = this.modelsSig();
    if (this.provider) {
      return list.filter(m => m.provider === this.provider).map(m => m.model);
    }
    // No provider selected — surface every model so the operator can still pin one and let the
    // backend infer the provider from the (provider, model) pair on the wire.
    return list.map(m => m.model);
  });

  protected readonly tokenState = computed<'idle' | 'warn' | 'full'>(() => {
    const cap = this.cap;
    if (!cap || cap <= 0) return 'idle';
    const total = this.inputTokens + this.outputTokens;
    if (total >= cap) return 'full';
    if (total >= cap * 0.8) return 'warn';
    return 'idle';
  });

  protected readonly capDisplay = computed<string | null>(() => {
    return this.cap && this.cap > 0 ? this.formatNum(this.cap) : null;
  });

  protected capTitle(): string {
    if (!this.cap) return '';
    const total = this.inputTokens + this.outputTokens;
    return `${this.formatNum(total)} of ${this.formatNum(this.cap)} tokens used`;
  }

  protected tokenAriaLabel(): string {
    const total = this.inputTokens + this.outputTokens;
    const base = `${this.formatNum(total)} tokens used`;
    return this.cap ? `${base} of ${this.formatNum(this.cap)} cap` : base;
  }

  protected providerDisplayName(key: LlmProviderKey): string {
    switch (key) {
      case 'anthropic': return 'Anthropic';
      case 'openai': return 'OpenAI';
      case 'lmstudio': return 'LM Studio';
      default: return key;
    }
  }

  protected formatNum(n: number): string {
    if (!Number.isFinite(n)) return '0';
    return Math.round(n).toLocaleString();
  }

  protected onProviderChange(value: string): void {
    const provider = (value || null) as LlmProviderKey | null;
    // Clear the model when the provider changes — keeps the selector consistent and avoids
    // sending a model that doesn't belong to the new provider.
    this.selectionChanged.emit({ provider, model: null });
  }

  protected onModelChange(value: string): void {
    const model = value || null;
    // If a model is picked but no provider, infer the provider from the (provider, model) row.
    let provider = this.provider;
    if (model && !provider) {
      const row = this.modelsSig().find(m => m.model === model);
      provider = row?.provider ?? null;
    }
    this.selectionChanged.emit({ provider, model });
  }
}
