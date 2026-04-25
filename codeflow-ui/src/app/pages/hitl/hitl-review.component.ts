import { Component, computed, effect, inject, input, output, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AgentsApi } from '../../core/agents.api';
import { TracesApi } from '../../core/traces.api';
import { AgentOutputDeclaration, HitlTask } from '../../core/models';
import {
  HitlPlaceholder,
  parseHitlTemplate,
  renderHitlTemplate
} from '../../core/hitl-template';

@Component({
  selector: 'cf-hitl-review',
  standalone: true,
  imports: [FormsModule],
  template: `
    <article class="hitl-review-card">
      <header class="review-header">
        <div class="review-title">
          <strong>{{ task().agentKey }}</strong>
          <span class="muted small"> v{{ task().agentVersion }}</span>
        </div>
        <div class="review-actions">
          <span class="tag warn">Pending</span>
          <a href="" (click)="downloadReviewArtifact($event)">Download review artifact</a>
        </div>
      </header>

      @if (task().inputPreview) {
        <section class="prompt-section">
          <div class="section-header compact">
            <h4>Review Prompt</h4>
          </div>
          <pre class="monospace preview">{{ task().inputPreview }}</pre>
        </section>
      } @else {
        <p class="muted small">(no preview — see artifact {{ task().inputRef }})</p>
      }

      @if (configLoading()) {
        <p class="muted small">Loading agent template…</p>
      } @else if (hasTemplate()) {
        <p class="muted small">Fill in each field. The output below will be sent to the next agent — change a value to see it update.</p>

        @if (displayPlaceholders().length > 0) {
          <section class="template-section display-section">
            <div class="section-header">
              <h4>Provided Context</h4>
              <p class="muted small">Read-only values coming from the workflow context or the previous agent output.</p>
            </div>

            <div class="display-grid">
              @for (ph of displayPlaceholders(); track ph.name) {
                <div class="form-field display-field">
                  <label class="field-label">{{ placeholderLabel(ph.name) }}</label>
                  <pre class="monospace preview readonly-value">{{ displayValue(ph.name) }}</pre>
                </div>
              }
            </div>
          </section>
        }

        <section class="template-section input-section">
          <div class="section-header">
            <h4>Your Response</h4>
            <p class="muted small">These fields shape the output artifact that will be handed to the next step.</p>
          </div>

          @for (ph of editablePlaceholders(); track ph.name) {
            <div class="form-field">
              <label class="field-label">{{ placeholderLabel(ph.name) }}</label>
              @if (ph.kind === 'select') {
                <div class="choice-group" [attr.aria-label]="placeholderLabel(ph.name)">
                  @for (opt of optionsFor(ph); track opt) {
                    <button
                      type="button"
                      class="choice-chip"
                      [class.selected]="fieldValues()[ph.name] === opt"
                      (click)="setField(ph.name, opt)">
                      {{ opt }}
                    </button>
                  }
                </div>
              } @else {
                <textarea
                  [ngModel]="fieldValues()[ph.name]"
                  (ngModelChange)="setField(ph.name, $event)"
                  [name]="'field-' + task().id + '-' + ph.name"
                  rows="3"></textarea>
              }
            </div>
          }
        </section>

        <div class="submit-row">
          @if (declaredOutputs().length > 0) {
            @for (decl of declaredOutputs(); track decl.kind) {
              <button
                class="submit-button"
                type="button"
                [title]="decl.description ?? ''"
                [disabled]="submitting() || configLoading()"
                (mouseenter)="setSelectedPort(decl.kind)"
                (focus)="setSelectedPort(decl.kind)"
                (click)="submit(decl.kind)">
                {{ submitting() && submittingPort() === decl.kind ? 'Submitting…' : decl.kind }}
              </button>
            }
          } @else {
            <button class="submit-button" type="button" (click)="submit('Completed')" [disabled]="submitting() || configLoading()">
              {{ submitting() ? 'Submitting…' : 'Submit' }}
            </button>
          }
        </div>

        <section class="template-section preview-section">
          <div class="section-header">
            <h4>
              Output Preview
              @if (serverPreviewPending()) {
                <span class="muted small"> — rendering…</span>
              } @else if (resolvedDecisionTemplate()) {
                <span class="muted small"> — server-rendered for port "{{ selectedPortName() }}"</span>
              }
            </h4>
            <p class="muted small">This is the exact content that will be submitted for the next node to consume.</p>
          </div>
          @if (serverPreviewError()) {
            <pre class="monospace preview preview-output preview-error-box">{{ serverPreviewError() }}</pre>
          } @else {
            <pre class="monospace preview preview-output">{{ renderedOutput() }}</pre>
          }
        </section>
      }

      @if (error()) {
        <div class="tag error">{{ error() }}</div>
      }
    </article>
  `,
  styles: [`
    .hitl-review-card {
      border: 1px solid var(--border);
      border-radius: 6px;
      padding: 1rem;
      margin-bottom: 1rem;
      display: flex;
      flex-direction: column;
      gap: 1rem;
      background: var(--surface);
    }
    .review-header {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 1rem;
      flex-wrap: wrap;
    }
    .review-title,
    .review-actions {
      display: flex;
      align-items: center;
      gap: 0.6rem;
      flex-wrap: wrap;
    }
    .template-section {
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 0.9rem;
    }
    .prompt-section {
      display: flex;
      flex-direction: column;
      gap: 0.5rem;
    }
    .display-section {
      background:
        linear-gradient(180deg, color-mix(in srgb, var(--surface-2) 90%, #8fb7ff 10%), var(--surface-2));
      border-left: 4px solid color-mix(in srgb, var(--accent) 45%, #8fb7ff 55%);
    }
    .input-section {
      background:
        linear-gradient(180deg, color-mix(in srgb, var(--surface) 94%, #66d9a3 6%), var(--surface));
      border-left: 4px solid color-mix(in srgb, var(--accent) 25%, #66d9a3 75%);
    }
    .preview-section {
      background:
        linear-gradient(180deg, color-mix(in srgb, var(--surface) 94%, #ffd36a 6%), var(--surface));
      border-left: 4px solid #ffd36a;
    }
    .section-header {
      margin-bottom: 0.75rem;
    }
    .section-header.compact {
      margin-bottom: 0;
    }
    .section-header h4 {
      margin: 0 0 0.2rem;
      font-size: 0.95rem;
      letter-spacing: 0.02em;
    }
    .section-header p {
      margin: 0;
    }
    .display-grid {
      display: grid;
      gap: 0.75rem;
    }
    .preview {
      white-space: pre-wrap;
      background: var(--surface-2);
      padding: 0.75rem;
      border-radius: 4px;
      max-height: 240px;
      overflow: auto;
      margin: 0;
      word-break: break-word;
    }
    .preview-output {
      border-left: 3px solid var(--accent);
    }
    .preview-error-box {
      color: #f85149;
      border-left-color: rgba(248, 81, 73, 0.7);
      background: rgba(248, 81, 73, 0.08);
    }
    .readonly-value {
      margin: 0;
      max-height: none;
      border: 1px solid color-mix(in srgb, var(--border) 88%, #8fb7ff 12%);
      background: color-mix(in srgb, var(--surface-2) 88%, #8fb7ff 12%);
    }
    .field-label { display: block; margin-bottom: 0.35rem; }
    .form-field:last-child { margin-bottom: 0; }
    textarea {
      width: 100%;
      min-height: 4.5rem;
      resize: vertical;
      border: 1px solid var(--border);
      border-radius: 6px;
      background: var(--surface-2);
      color: inherit;
      padding: 0.6rem 0.7rem;
    }
    textarea:focus {
      outline: none;
      box-shadow: var(--focus-ring);
      border-color: var(--accent);
    }
    .choice-group {
      display: flex;
      flex-wrap: wrap;
      gap: 0.5rem;
    }
    .choice-chip {
      border: 1px solid var(--border);
      background: var(--surface-2);
      color: inherit;
      padding: 0.5rem 0.8rem;
      border-radius: 999px;
      cursor: pointer;
      font: inherit;
    }
    .choice-chip.selected {
      border-color: var(--accent);
      background: color-mix(in srgb, var(--accent) 18%, var(--surface-2));
      box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--accent) 40%, transparent);
    }
    .submit-row {
      display: flex;
      justify-content: flex-start;
      flex-wrap: wrap;
      gap: 0.5rem;
    }
    .submit-button {
      border: 1px solid var(--accent);
      background: var(--accent);
      /* --accent-ink is a blue-on-neutral token meant for chips/links; reusing it on a
         solid accent fill leaves both fg and bg in the same hue family and reads as
         "disabled". Use a near-white instead so the label is high-contrast in both themes. */
      color: oklch(0.97 0.01 265);
      border-radius: 6px;
      padding: 0.55rem 0.85rem;
      cursor: pointer;
      font: inherit;
      font-weight: 600;
    }
    .submit-button:disabled {
      cursor: wait;
      opacity: 0.6;
    }
    .submit-button:focus-visible {
      outline: none;
      box-shadow: var(--focus-ring);
    }
    @media (min-width: 860px) {
      .display-grid {
        grid-template-columns: repeat(2, minmax(0, 1fr));
      }
    }
    @media (max-width: 640px) {
      .review-header,
      .review-actions,
      .submit-row {
        align-items: stretch;
        flex-direction: column;
      }
      .submit-button {
        width: 100%;
      }
    }
    .muted { color: var(--muted); }
    .small { font-size: 0.8rem; }
  `]
})
export class HitlReviewComponent implements OnInit {
  private readonly api = inject(TracesApi);
  private readonly agentsApi = inject(AgentsApi);

  readonly task = input.required<HitlTask>();
  readonly decided = output<void>();

  // Template-driven state
  readonly configLoading = signal(true);
  readonly template = signal<string | null>(null);
  readonly decisionOutputTemplates = signal<Record<string, string> | null>(null);
  readonly declaredOutputs = signal<AgentOutputDeclaration[]>([]);
  readonly fieldValues = signal<Record<string, string>>({});
  readonly resolvedTemplateValues = signal<Record<string, unknown>>({});
  readonly contextInputs = signal<Record<string, unknown>>({});

  /** Tracks which port is currently driving the preview (hovered/focused/last-clicked). */
  readonly selectedPortName = signal<string>('Completed');

  // Server-rendered preview (populated via /api/agents/templates/render-preview when the agent
  // declares a decision-output template for the selected port). Falls back to the client-side
  // renderHitlTemplate when no matching template exists.
  readonly serverRenderedOutput = signal<string | null>(null);
  readonly serverPreviewError = signal<string | null>(null);
  readonly serverPreviewPending = signal(false);

  readonly parsedTemplate = computed(() => parseHitlTemplate(this.template()));
  readonly placeholders = computed<HitlPlaceholder[]>(() => this.parsedTemplate().placeholders);
  readonly displayPlaceholders = computed<HitlPlaceholder[]>(() =>
    this.placeholders().filter(ph => isDisplayOnlyPlaceholder(ph.name))
  );
  readonly editablePlaceholders = computed<HitlPlaceholder[]>(() =>
    this.placeholders().filter(ph => !isDisplayOnlyPlaceholder(ph.name))
  );
  readonly hasTemplate = computed(() => this.placeholders().length > 0);
  readonly mergedTemplateValues = computed<Record<string, unknown>>(() => ({
    ...this.resolvedTemplateValues(),
    ...this.fieldValues()
  }));
  readonly resolvedDecisionTemplate = computed(() => {
    const templates = this.decisionOutputTemplates();
    if (!templates) { return null; }
    const port = this.selectedPortName();
    const match = Object.keys(templates).find(k => k.toLowerCase() === port.toLowerCase());
    if (match) { return templates[match]; }
    if (templates['*']) { return templates['*']; }
    return null;
  });
  readonly renderedOutput = computed(() => {
    const server = this.serverRenderedOutput();
    if (server !== null) { return server; }
    const tpl = this.template();
    if (!tpl) { return ''; }
    return renderHitlTemplate(tpl, this.mergedTemplateValues());
  });

  readonly submitting = signal(false);
  readonly submittingPort = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  private previewTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    effect(() => {
      const template = this.resolvedDecisionTemplate();
      const port = this.selectedPortName();
      const fields = this.fieldValues();
      const context = this.contextInputs();
      void fields; void context; // signals tracked for reactivity

      if (!template) {
        this.serverRenderedOutput.set(null);
        this.serverPreviewError.set(null);
        this.serverPreviewPending.set(false);
        return;
      }

      if (this.previewTimer !== null) {
        clearTimeout(this.previewTimer);
      }
      this.previewTimer = setTimeout(() => this.runServerPreview(template, port), 200);
    });
  }

  ngOnInit(): void {
    const task = this.task();
    this.seedResolvedTemplateValues(task.inputPreview);
    this.loadResolvedTemplateValues(task);
    this.loadResolvedContextValues(task.traceId);

    this.agentsApi.getVersion(task.agentKey, task.agentVersion).subscribe({
      next: version => {
        const tpl = version.config?.['outputTemplate'] as string | undefined;
        this.template.set(tpl?.length ? tpl : null);
        const decisionTemplates = version.config?.['decisionOutputTemplates'] as
          Record<string, string> | undefined;
        this.decisionOutputTemplates.set(
          decisionTemplates && Object.keys(decisionTemplates).length > 0 ? decisionTemplates : null);

        const outputs = (version.config?.['outputs'] as AgentOutputDeclaration[] | undefined) ?? [];
        this.declaredOutputs.set(outputs);
        // Initial preview keys off the first declared port (or 'Completed' if none).
        if (outputs.length > 0) {
          this.selectedPortName.set(outputs[0].kind);
        } else {
          this.selectedPortName.set('Completed');
        }

        this.seedDefaults();
        this.configLoading.set(false);
      },
      error: () => {
        // Fall back to default form on config-load failure.
        this.configLoading.set(false);
      }
    });
  }

  private runServerPreview(template: string, port: string): void {
    this.serverPreviewPending.set(true);
    this.serverPreviewError.set(null);

    const fieldValues = { ...this.fieldValues() };
    const context = this.contextInputs();

    this.agentsApi.renderDecisionOutputTemplate({
      template,
      mode: 'hitl',
      decision: port,
      outputPortName: port,
      fieldValues,
      context: Object.keys(context).length > 0 ? context as Record<string, unknown> : undefined
    }).subscribe({
      next: response => {
        this.serverRenderedOutput.set(response.rendered);
        this.serverPreviewError.set(null);
        this.serverPreviewPending.set(false);
      },
      error: err => {
        this.serverRenderedOutput.set(null);
        this.serverPreviewError.set(extractPreviewError(err));
        this.serverPreviewPending.set(false);
      }
    });
  }

  placeholderLabel(name: string): string {
    return humanizePlaceholderLabel(name);
  }

  displayValue(name: string): string {
    return formatDisplayValue(this.mergedTemplateValues()[name]);
  }

  setField(name: string, value: string): void {
    this.fieldValues.set({ ...this.fieldValues(), [name]: value });
  }

  setSelectedPort(port: string): void {
    if (this.selectedPortName() !== port) {
      this.selectedPortName.set(port);
    }
  }

  optionsFor(placeholder: HitlPlaceholder): string[] {
    return placeholder.options ?? [];
  }

  private seedDefaults(): void {
    const next: Record<string, string> = {};
    for (const ph of this.editablePlaceholders()) {
      if (ph.kind === 'select') {
        next[ph.name] = ph.options?.[0] ?? '';
      } else {
        next[ph.name] = '';
      }
    }
    this.fieldValues.set(next);
  }

  submit(portName: string): void {
    if (this.submitting()) {
      return;
    }

    this.setSelectedPort(portName);
    this.submitting.set(true);
    this.submittingPort.set(portName);
    this.error.set(null);

    if (this.hasTemplate()) {
      this.submitTemplated(portName);
    } else {
      this.submitDefault(portName);
    }
  }

  private submitTemplated(portName: string): void {
    // For templated submissions, render with the clicked port so the output the next node
    // consumes matches that port's decision-output template (when one exists).
    const rendered = this.renderForPort(portName);

    this.api.submitHitlDecision(this.task().traceId, {
      outputPortName: portName,
      outputText: rendered,
      fieldValues: { ...this.fieldValues() }
    }).subscribe({
      next: () => {
        this.submitting.set(false);
        this.submittingPort.set(null);
        this.decided.emit();
      },
      error: err => {
        this.submitting.set(false);
        this.submittingPort.set(null);
        this.error.set(err?.message ?? 'Failed to submit');
      }
    });
  }

  private renderForPort(portName: string): string {
    // The server preview signal is keyed by selectedPortName; if that already matches the
    // clicked port and is fresh, prefer it. Otherwise fall back to the client-side render.
    if (this.selectedPortName() === portName && this.serverRenderedOutput() !== null) {
      return this.serverRenderedOutput() ?? '';
    }
    const tpl = this.template();
    if (!tpl) { return ''; }
    return renderHitlTemplate(tpl, this.mergedTemplateValues());
  }

  private submitDefault(portName: string): void {
    this.api.submitHitlDecision(this.task().traceId, {
      outputPortName: portName
    }).subscribe({
      next: () => {
        this.submitting.set(false);
        this.submittingPort.set(null);
        this.decided.emit();
      },
      error: err => {
        this.submitting.set(false);
        this.submittingPort.set(null);
        this.error.set(err?.message ?? 'Failed to submit');
      }
    });
  }

  downloadReviewArtifact(event: Event): void {
    event.preventDefault();

    this.api.downloadArtifact(this.task().traceId, this.task().inputRef).subscribe({
      next: response => {
        const blob = response.body;
        if (!blob) {
          return;
        }

        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = this.fileNameFromResponse(response.headers.get('content-disposition')) ?? this.fileNameForArtifact(this.task().inputRef);
        anchor.click();
        URL.revokeObjectURL(url);
      },
      error: err => {
        this.error.set(err?.message ?? 'Failed to download artifact.');
      }
    });
  }

  private seedResolvedTemplateValues(raw: string | null | undefined): void {
    if (!raw) {
      return;
    }

    const extracted = extractTemplateInputValues(raw);
    if (Object.keys(extracted).length === 0) {
      return;
    }

    this.resolvedTemplateValues.set({
      ...this.resolvedTemplateValues(),
      ...extracted
    });
  }

  private loadResolvedTemplateValues(task: HitlTask): void {
    this.api.getArtifact(task.traceId, task.inputRef).subscribe({
      next: content => this.seedResolvedTemplateValues(content),
      error: () => {
        // The preview usually contains enough for the form; don't block on artifact fetch failures.
      }
    });
  }

  private loadResolvedContextValues(traceId: string): void {
    this.api.get(traceId).subscribe({
      next: detail => {
        // Used for the existing client-side HITL template preview (flattened under the `context.*`
        // dotted keys hitl-template.ts expects).
        const contextValues = extractScopedTemplateValues('context', detail.contextInputs);
        if (Object.keys(contextValues).length > 0) {
          this.resolvedTemplateValues.set({
            ...this.resolvedTemplateValues(),
            ...contextValues
          });
        }

        // Used for the new server-side decision-output-template preview (raw structured object
        // passed through as context.* by DecisionOutputTemplateContext.BuildForHitl).
        if (detail.contextInputs && typeof detail.contextInputs === 'object') {
          this.contextInputs.set(detail.contextInputs as Record<string, unknown>);
        }
      },
      error: () => {
        // Context values are optional for the form; keep rendering even if unavailable.
      }
    });
  }

  private fileNameForArtifact(uri: string): string {
    try {
      const parsed = new URL(uri);
      const fileName = parsed.pathname.split('/').filter(Boolean).at(-1);
      return fileName || 'artifact.txt';
    } catch {
      return 'artifact.txt';
    }
  }

  private fileNameFromResponse(contentDisposition: string | null): string | null {
    if (!contentDisposition) {
      return null;
    }

    const match = /filename=\"?([^\";]+)\"?/i.exec(contentDisposition);
    return match?.[1] ?? null;
  }
}

function isTemplateInputReference(name: string): boolean {
  const normalized = name.toLowerCase();
  return normalized === 'input' || normalized.startsWith('input.');
}

function isTemplateContextReference(name: string): boolean {
  const normalized = name.toLowerCase();
  return normalized === 'context' || normalized.startsWith('context.');
}

function isDisplayOnlyPlaceholder(name: string): boolean {
  return isTemplateInputReference(name) || isTemplateContextReference(name);
}

function humanizePlaceholderLabel(name: string): string {
  const normalized = name.toLowerCase();
  const base = normalized === 'input'
    ? 'input'
    : normalized === 'context'
      ? 'context'
      : isTemplateInputReference(name)
        ? name.slice('input.'.length)
        : isTemplateContextReference(name)
          ? name.slice('context.'.length)
          : name;

  return base
    .split(/[._-]+/)
    .filter(part => part.length > 0)
    .map(part => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ') || 'Value';
}

function extractTemplateInputValues(raw: string): Record<string, unknown> {
  const trimmed = raw.trim();
  if (!trimmed) {
    return {};
  }

  try {
    const parsed = JSON.parse(trimmed) as unknown;
    return extractScopedTemplateValues('input', parsed);
  } catch {
    return {
      input: trimmed
    };
  }
}

function extractScopedTemplateValues(scope: string, value: unknown): Record<string, unknown> {
  const values: Record<string, unknown> = {};
  addTemplateValue(values, scope, value);
  return values;
}

function addTemplateValue(target: Record<string, unknown>, key: string, value: unknown): void {
  target[key] = value;

  if (Array.isArray(value)) {
    value.forEach((item, index) => addTemplateValue(target, `${key}.${index}`, item));
    return;
  }

  if (!value || typeof value !== 'object') {
    return;
  }

  for (const [childKey, childValue] of Object.entries(value)) {
    addTemplateValue(target, `${key}.${childKey}`, childValue);
  }
}

function extractPreviewError(err: unknown): string {
  if (err && typeof err === 'object') {
    const httpErr = err as { error?: { error?: string } | string; message?: string };
    if (httpErr.error && typeof httpErr.error === 'object' && 'error' in httpErr.error) {
      return String((httpErr.error as { error: unknown }).error);
    }
    if (typeof httpErr.error === 'string') {
      return httpErr.error;
    }
    if (httpErr.message) {
      return httpErr.message;
    }
  }
  return 'Preview unavailable';
}

function formatDisplayValue(value: unknown): string {
  if (typeof value === 'string') {
    return value;
  }

  if (value === null || value === undefined) {
    return '';
  }

  return typeof value === 'object'
    ? JSON.stringify(value, null, 2)
    : String(value);
}
