import { Component, computed, effect, inject, input, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
import { AgentConfig, AgentOutputDeclaration, DecisionOutputTemplateMode } from '../../core/models';

interface DecisionTemplateRow {
  port: string;
  template: string;
  preview: string;
  previewError: string | null;
  previewPending: boolean;
}

@Component({
  selector: 'cf-agent-editor',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  template: `
    <header class="page-header">
      <div>
        <h1>{{ existingKey() ? 'New version of ' + existingKey() : 'New agent' }}</h1>
        <p class="muted">Saving always creates a new immutable version. Tool access flows through roles assigned on the agent's detail page.</p>
      </div>
      <a routerLink="/agents"><button class="secondary">Cancel</button></a>
    </header>

    <form (submit)="submit($event)">
      @if (!existingKey()) {
        <div class="form-field">
          <label>Agent key</label>
          <input [(ngModel)]="key" name="key" required placeholder="reviewer-v1" />
          <div class="muted small">Lowercase letters, digits, '-' or '_'.</div>
        </div>
      }

      <div class="form-field">
        <label>Type</label>
        <select [(ngModel)]="type" name="type">
          <option value="agent">Agent (LLM)</option>
          <option value="hitl">HITL (Human reviewer)</option>
        </select>
      </div>

      <div class="form-field">
        <label>Name</label>
        <input [(ngModel)]="name" name="name" placeholder="Technical reviewer" />
      </div>

      <div class="form-field">
        <label>Description</label>
        <textarea [(ngModel)]="description" name="description" rows="2"></textarea>
      </div>

      @if (type() === 'hitl') {
        <div class="form-field">
          <label>Output template</label>
          <textarea [(ngModel)]="outputTemplate" name="outputTemplate" rows="6" placeholder="HITL decision: {{ '{{decision:Approved|Rejected}}' }}&#10;{{ '{{feedback}}' }}"></textarea>
          <div class="muted small">
            Defines exactly how reviewers enter their response.
            Use <code>{{ '{{name}}' }}</code> for free text or <code>{{ '{{name:Opt1|Opt2}}' }}</code> for a dropdown.
            <code>{{ '{{decision}}' }}</code> is special — if its options match built-in decision kinds, that choice is sent directly.
            Otherwise the selected value is emitted as the HITL node's output port while the canonical decision stays <code>Completed</code>.
            Use <code>{{ '{{json(name)}}' }}</code> when the final artifact should be valid JSON; the form still treats it like the underlying field, so
            <code>{{ '{{json(context.lastQuestion)}}' }}</code> stays read-only, <code>{{ '{{json(answer)}}' }}</code> stays a textbox, and
            <code>{{ '{{json(decision:Answered|Exit)}}' }}</code> stays a decision chooser.
          </div>
        </div>
      }

      @if (type() === 'agent') {
        <div class="grid-two">
          <div class="form-field">
            <label>Provider</label>
            <select [(ngModel)]="provider" name="provider">
              <option value="openai">OpenAI</option>
              <option value="anthropic">Anthropic</option>
              <option value="lmstudio">LM Studio</option>
            </select>
          </div>
          <div class="form-field">
            <label>Model</label>
            <input [(ngModel)]="model" name="model" placeholder="gpt-5.4" />
          </div>
        </div>

        <div class="form-field">
          <label>System prompt</label>
          <textarea [(ngModel)]="systemPrompt" name="systemPrompt" rows="4"></textarea>
        </div>

        <div class="form-field">
          <label>
            Prompt template
            <a
              class="doc-link"
              href="https://github.com/michaeltrefry/CodeFlow/blob/main/docs/prompt-templates.md"
              target="_blank"
              rel="noopener noreferrer">Learn more ↗</a>
          </label>
          <textarea
            [(ngModel)]="promptTemplate"
            name="promptTemplate"
            rows="20"
            class="prompt-template-input"
            placeholder="Review the following input: {{ '{{input}}' }}"></textarea>
          <div class="muted small">
            Supports <code>{{ '{{ name }}' }}</code> substitution plus conditionals and loops —
            see the <a href="https://github.com/michaeltrefry/CodeFlow/blob/main/docs/prompt-templates.md" target="_blank" rel="noopener noreferrer">prompt template guide</a>.
          </div>
        </div>

        <div class="grid-two">
          <div class="form-field">
            <label>Max tokens</label>
            <input
              type="number"
              [ngModel]="maxTokens() ?? null"
              (ngModelChange)="maxTokens.set(coerceOptionalNumber($event))"
              name="maxTokens"
              min="1"
              autocomplete="off" />
          </div>
          <div class="form-field">
            <label>Temperature</label>
            <input
              type="number"
              [ngModel]="temperature() ?? null"
              (ngModelChange)="temperature.set(coerceOptionalNumber($event))"
              name="temperature"
              step="0.1"
              min="0"
              max="2"
              autocomplete="off" />
          </div>
        </div>
      }

      <section class="outputs-section">
        <h3>
          Decision output templates
          <a
            class="doc-link"
            href="https://github.com/michaeltrefry/CodeFlow/blob/main/docs/decision-output-templates.md"
            target="_blank"
            rel="noopener noreferrer">Learn more ↗</a>
        </h3>
        <p class="muted small">
          Reshape the artifact passed downstream once a decision lands. Key each template by the output port
          the agent will emit, or use <code>*</code> as a wildcard fallback. Scriban syntax —
          <code>{{ '{{ decision }}' }}</code>, <code>{{ '{{ output.field }}' }}</code>,
          <code>{{ '{{ context.key }}' }}</code>, <code>{{ '{{ if … }}' }}</code>.
          A routing script that calls <code>setOutput()</code> still wins over any template here.
        </p>

        <div class="form-field preview-context">
          <label>Preview context <span class="muted small">(JSON — drives the live preview below)</span></label>
          <textarea
            rows="5"
            class="mono"
            [ngModel]="previewContextText()"
            (ngModelChange)="previewContextText.set($event)"
            name="decisionTemplatePreviewContext"
            [placeholder]='previewContextPlaceholder()'></textarea>
          @if (previewContextError()) {
            <div class="tag error">{{ previewContextError() }}</div>
          }
        </div>

        @for (row of decisionTemplates(); track $index; let i = $index) {
          <div class="output-card">
            <div class="row-spread">
              <strong class="mono">{{ row.port || '(unnamed port)' }}</strong>
              <button type="button" class="icon-button" (click)="removeDecisionTemplate(i)" title="Remove template">×</button>
            </div>
            <div class="form-field">
              <label>Output port <span class="muted small">(or <code>*</code> for wildcard)</span></label>
              <input type="text"
                     [ngModel]="row.port"
                     (ngModelChange)="updateDecisionTemplate(i, { port: $event })"
                     [name]="'decision_template_port_' + i"
                     placeholder="Approved" />
            </div>
            <div class="form-field">
              <label>Template</label>
              <textarea
                rows="6"
                class="mono"
                [ngModel]="row.template"
                (ngModelChange)="updateDecisionTemplate(i, { template: $event })"
                [name]="'decision_template_body_' + i"
                placeholder="[{{ '{{ decision }}' }}] {{ '{{ output.headline }}' }}"></textarea>
            </div>
            <div class="form-field">
              <label>
                Preview
                @if (row.previewPending) {
                  <span class="muted small">(rendering…)</span>
                }
              </label>
              @if (row.previewError) {
                <pre class="preview-error mono">{{ row.previewError }}</pre>
              } @else {
                <pre class="preview-output mono">{{ row.preview || '(enter a template to see the rendered output)' }}</pre>
              }
            </div>
          </div>
        }
        <button type="button" class="secondary" (click)="addDecisionTemplate()">+ Add template</button>
      </section>

      <section class="outputs-section">
        <h3>Declared outputs</h3>
        <p class="muted small">
          List the decision kinds this agent emits. Workflow nodes that reference this agent
          will automatically get a matching set of output ports in the canvas editor. Baseline
          agents at least emit <code>Completed</code> and <code>Failed</code>.
        </p>
        @for (output of outputs(); track $index; let i = $index) {
          <div class="output-card">
            <div class="row-spread">
              <strong class="mono">{{ output.kind || '(unnamed)' }}</strong>
              <button type="button" class="icon-button" (click)="removeOutput(i)" title="Remove output">×</button>
            </div>
            <div class="form-field">
              <label>Kind</label>
              <input type="text" [ngModel]="output.kind" (ngModelChange)="updateOutput(i, { kind: $event })"
                     [name]="'output_kind_' + i" placeholder="Completed" />
              <div class="muted small">Becomes the port name on workflow edges.</div>
            </div>
            <div class="form-field">
              <label>Description <span class="muted small">(optional)</span></label>
              <input type="text" [ngModel]="output.description ?? ''"
                     (ngModelChange)="updateOutput(i, { description: $event || null })"
                     [name]="'output_desc_' + i"
                     placeholder="Normal success" />
            </div>
            <div class="form-field">
              <label>Payload example <span class="muted small">(optional JSON)</span></label>
              <textarea rows="10" class="mono"
                        [ngModel]="payloadExampleText(output)"
                        (ngModelChange)="updatePayloadExample(i, $event)"
                        [name]="'output_example_' + i"
                        placeholder='{"reasons": ["..."]}'></textarea>
            </div>
          </div>
        }
        <button type="button" class="secondary" (click)="addOutput()">+ Add output</button>
      </section>

      @if (error()) {
        <div class="tag error">{{ error() }}</div>
      }

      <div class="row" style="margin-top: 1rem;">
        <button type="submit" [disabled]="saving()">
          {{ saving() ? 'Saving…' : 'Save new version' }}
        </button>
      </div>
    </form>
  `,
  styles: [`
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 1.5rem; gap: 1rem; }
    .muted { color: var(--color-muted); }
    .small { font-size: 0.8rem; }
    .mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
    .outputs-section {
      margin-top: 1.5rem;
      padding: 1rem;
      border: 1px solid var(--color-border);
      border-radius: 6px;
      background: rgba(255, 255, 255, 0.02);
    }
    .outputs-section h3 { margin-top: 0; }
    .output-card {
      padding: 0.75rem;
      border: 1px solid var(--color-border);
      border-radius: 4px;
      margin-bottom: 0.75rem;
      background: var(--color-surface);
    }
    .row-spread { display: flex; justify-content: space-between; align-items: center; margin-bottom: 0.5rem; }
    .prompt-template-input { min-height: 28rem; resize: vertical; }
    .icon-button {
      width: 22px; height: 22px; padding: 0; border-radius: 50%;
      border: 1px solid var(--color-border); background: var(--color-surface);
      cursor: pointer; color: inherit;
    }
    .icon-button:hover { border-color: #f85149; color: #f85149; }
    .doc-link {
      margin-left: 0.5rem; font-size: 0.75rem; font-weight: normal;
      color: var(--color-muted); text-decoration: none;
    }
    .doc-link:hover { color: var(--color-accent, #58a6ff); text-decoration: underline; }
    .preview-context textarea { min-height: 5rem; }
    .preview-output, .preview-error {
      padding: 0.5rem; border-radius: 4px;
      background: rgba(255, 255, 255, 0.02);
      border: 1px solid var(--color-border);
      white-space: pre-wrap; word-break: break-word;
      margin: 0; font-size: 0.85rem;
    }
    .preview-error {
      color: #f85149; border-color: rgba(248, 81, 73, 0.5);
      background: rgba(248, 81, 73, 0.08);
    }
  `]
})
export class AgentEditorComponent implements OnInit {
  private readonly agentsApi = inject(AgentsApi);
  private readonly router = inject(Router);

  readonly existingKey = input<string | undefined>(undefined, { alias: 'key' });

  readonly key = signal('');
  readonly type = signal<'agent' | 'hitl'>('agent');
  readonly name = signal('');
  readonly description = signal('');
  readonly provider = signal<'openai' | 'anthropic' | 'lmstudio'>('openai');
  readonly model = signal('gpt-5.4');
  readonly systemPrompt = signal('');
  readonly promptTemplate = signal('');
  readonly outputTemplate = signal('');
  readonly maxTokens = signal<number | undefined>(undefined);
  readonly temperature = signal<number | undefined>(undefined);
  readonly outputs = signal<AgentOutputDeclaration[]>([
    { kind: 'Completed', description: null, payloadExample: null },
    { kind: 'Failed', description: null, payloadExample: null }
  ]);

  readonly decisionTemplates = signal<DecisionTemplateRow[]>([]);

  // Author-editable JSON that drives the live preview render. Resets to the mode-appropriate
  // default whenever the agent type toggles so switching LLM ↔ HITL shows a sensible starter.
  readonly previewContextText = signal(defaultLlmPreviewContext());
  readonly previewContextPlaceholder = computed(() =>
    this.type() === 'hitl' ? defaultHitlPreviewContext() : defaultLlmPreviewContext());
  readonly previewContextError = signal<string | null>(null);

  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    // Re-render every template's preview whenever its template body, port, preview context, or mode changes.
    effect(() => {
      const rows = this.decisionTemplates();
      const contextText = this.previewContextText();
      const mode: DecisionOutputTemplateMode = this.type() === 'hitl' ? 'hitl' : 'llm';

      let parsedContext: Record<string, unknown> = {};
      try {
        const parsed = contextText.trim() ? JSON.parse(contextText) : {};
        if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
          parsedContext = parsed as Record<string, unknown>;
          this.previewContextError.set(null);
        } else {
          this.previewContextError.set('Preview context must be a JSON object.');
          return;
        }
      } catch (e) {
        this.previewContextError.set('Preview context is not valid JSON.');
        return;
      }

      rows.forEach((row, index) => {
        if (!row.template.trim()) {
          if (row.preview || row.previewError || row.previewPending) {
            this.patchDecisionRowSilently(index, { preview: '', previewError: null, previewPending: false });
          }
          return;
        }
        this.schedulePreviewRender(index, row, mode, parsedContext);
      });
    });
  }

  ngOnInit(): void {
    const existing = this.existingKey();
    if (existing) {
      this.key.set(existing);
      this.agentsApi.getLatest(existing).subscribe({
        next: version => {
          const config = version.config ?? {};
          this.type.set(version.type === 'hitl' ? 'hitl' : 'agent');
          this.name.set((config['name'] as string) ?? '');
          this.description.set((config['description'] as string) ?? '');
          this.provider.set((config['provider'] as 'openai' | 'anthropic' | 'lmstudio') ?? 'openai');
          this.model.set((config['model'] as string) ?? 'gpt-5.4');
          this.systemPrompt.set((config['systemPrompt'] as string) ?? '');
          this.promptTemplate.set((config['promptTemplate'] as string) ?? '');
          this.outputTemplate.set((config['outputTemplate'] as string) ?? '');
          this.maxTokens.set(config['maxTokens'] as number | undefined);
          this.temperature.set(config['temperature'] as number | undefined);
          const declared = (config as AgentConfig)['outputs'];
          if (Array.isArray(declared) && declared.length > 0) {
            this.outputs.set(declared.map(d => ({
              kind: d.kind ?? '',
              description: d.description ?? null,
              payloadExample: d.payloadExample ?? null
            })));
          }
          const templates = (config as AgentConfig)['decisionOutputTemplates'];
          if (templates && typeof templates === 'object') {
            const rows = Object.entries(templates).map(([port, template]) => ({
              port,
              template: String(template ?? ''),
              preview: '',
              previewError: null,
              previewPending: false
            }) as DecisionTemplateRow);
            this.decisionTemplates.set(rows);
          }
          this.previewContextText.set(
            version.type === 'hitl' ? defaultHitlPreviewContext() : defaultLlmPreviewContext());
        }
      });
    }
  }

  addDecisionTemplate(): void {
    this.decisionTemplates.set([
      ...this.decisionTemplates(),
      { port: '', template: '', preview: '', previewError: null, previewPending: false }
    ]);
  }

  removeDecisionTemplate(index: number): void {
    this.decisionTemplates.set(this.decisionTemplates().filter((_, i) => i !== index));
  }

  updateDecisionTemplate(index: number, patch: Partial<DecisionTemplateRow>): void {
    this.decisionTemplates.set(this.decisionTemplates().map((row, i) =>
      i === index ? { ...row, ...patch } : row));
  }

  /**
   * Apply preview fields (preview / previewError / previewPending) without triggering the
   * preview-render effect. Uses signal.update so Angular sees the write but we're careful to
   * only mutate preview-related keys — the effect's dependencies (template, port, mode, context)
   * are read from the rows array and preview-only updates don't change those dependencies.
   */
  private patchDecisionRowSilently(index: number, patch: Partial<DecisionTemplateRow>): void {
    this.decisionTemplates.update(rows => rows.map((row, i) =>
      i === index ? { ...row, ...patch } : row));
  }

  private previewTimer: ReturnType<typeof setTimeout> | null = null;

  private schedulePreviewRender(
    index: number,
    row: DecisionTemplateRow,
    mode: DecisionOutputTemplateMode,
    parsedContext: Record<string, unknown>
  ): void {
    if (this.previewTimer !== null) {
      clearTimeout(this.previewTimer);
    }
    this.previewTimer = setTimeout(() => this.runPreview(index, row, mode, parsedContext), 200);
  }

  private runPreview(
    index: number,
    row: DecisionTemplateRow,
    mode: DecisionOutputTemplateMode,
    parsedContext: Record<string, unknown>
  ): void {
    const port = row.port.trim() || '*';
    this.patchDecisionRowSilently(index, { previewPending: true });
    this.agentsApi.renderDecisionOutputTemplate({
      template: row.template,
      mode,
      decision: port,
      outputPortName: port,
      output: mode === 'llm' ? (parsedContext['output'] as string | undefined) : undefined,
      input: mode === 'llm' ? parsedContext['input'] : undefined,
      fieldValues: mode === 'hitl'
        ? (parsedContext['fieldValues'] as Record<string, unknown> | undefined)
        : undefined,
      reason: mode === 'hitl' ? (parsedContext['reason'] as string | undefined) : undefined,
      reasons: mode === 'hitl' ? (parsedContext['reasons'] as string[] | undefined) : undefined,
      actions: mode === 'hitl' ? (parsedContext['actions'] as string[] | undefined) : undefined,
      context: parsedContext['context'] as Record<string, unknown> | undefined,
      global: parsedContext['global'] as Record<string, unknown> | undefined
    }).subscribe({
      next: response => this.patchDecisionRowSilently(index, {
        preview: response.rendered, previewError: null, previewPending: false
      }),
      error: err => this.patchDecisionRowSilently(index, {
        preview: '', previewError: extractPreviewError(err), previewPending: false
      })
    });
  }

  addOutput(): void {
    this.outputs.set([
      ...this.outputs(),
      { kind: '', description: null, payloadExample: null }
    ]);
  }

  removeOutput(index: number): void {
    this.outputs.set(this.outputs().filter((_, i) => i !== index));
  }

  updateOutput(index: number, patch: Partial<AgentOutputDeclaration>): void {
    this.outputs.set(this.outputs().map((o, i) => (i === index ? { ...o, ...patch } : o)));
  }

  payloadExampleText(output: AgentOutputDeclaration): string {
    if (output.payloadExample === null || output.payloadExample === undefined) return '';
    if (typeof output.payloadExample === 'string') return output.payloadExample;
    return JSON.stringify(output.payloadExample, null, 2);
  }

  updatePayloadExample(index: number, raw: string): void {
    const trimmed = raw?.trim() ?? '';
    if (!trimmed) {
      this.updateOutput(index, { payloadExample: null });
      return;
    }
    try {
      const parsed = JSON.parse(trimmed);
      this.updateOutput(index, { payloadExample: parsed });
    } catch {
      // Keep the raw string so the user can fix it; round-trip will re-parse on save.
      this.updateOutput(index, { payloadExample: raw });
    }
  }

  submit(event: Event): void {
    event.preventDefault();
    this.saving.set(true);
    this.error.set(null);

    const config: AgentConfig = {
      type: this.type(),
      name: this.name() || undefined,
      description: this.description() || undefined,
    };

    if (this.type() === 'agent') {
      config.provider = this.provider();
      config.model = this.model();
      config.systemPrompt = this.systemPrompt() || undefined;
      config.promptTemplate = this.promptTemplate() || undefined;
      if (this.maxTokens() !== undefined) config.maxTokens = this.maxTokens();
      if (this.temperature() !== undefined) config.temperature = this.temperature();
    }

    if (this.type() === 'hitl') {
      config.outputTemplate = this.outputTemplate() || undefined;
    }

    const cleanedTemplates = this.decisionTemplates()
      .map(row => ({ port: row.port.trim(), template: row.template }))
      .filter(row => row.port.length > 0 && row.template.length > 0);
    if (cleanedTemplates.length > 0) {
      config.decisionOutputTemplates = Object.fromEntries(
        cleanedTemplates.map(row => [row.port, row.template]));
    }

    const cleanedOutputs = this.outputs()
      .map(o => ({
        kind: (o.kind ?? '').trim(),
        description: o.description?.trim() || null,
        payloadExample: typeof o.payloadExample === 'string'
          ? tryParseJson(o.payloadExample)
          : o.payloadExample ?? null
      }))
      .filter(o => o.kind.length > 0);

    if (cleanedOutputs.length > 0) {
      config.outputs = cleanedOutputs;
    }

    const existingKey = this.existingKey();
    const save$ = existingKey
      ? this.agentsApi.addVersion(existingKey, config)
      : this.agentsApi.create(this.key(), config);

    save$.subscribe({
      next: result => {
        this.saving.set(false);
        this.router.navigate(['/agents', result.key]);
      },
      error: err => {
        this.saving.set(false);
        this.error.set(this.formatError(err));
      }
    });
  }

  private formatError(err: unknown): string {
    if (err && typeof err === 'object') {
      const httpErr = err as { error?: unknown; message?: string };
      if (httpErr.error && typeof httpErr.error === 'object') {
        return JSON.stringify(httpErr.error);
      }
      if (httpErr.message) {
        return httpErr.message;
      }
    }
    return 'Save failed';
  }

  protected coerceOptionalNumber(value: unknown): number | undefined {
    if (value === null || value === undefined || value === '') {
      return undefined;
    }

    if (typeof value === 'number') {
      return Number.isFinite(value) ? value : undefined;
    }

    if (typeof value === 'string') {
      const trimmed = value.trim();
      if (!trimmed) {
        return undefined;
      }

      const parsed = Number(trimmed);
      return Number.isFinite(parsed) ? parsed : undefined;
    }

    return undefined;
  }
}

function tryParseJson(raw: string): unknown {
  try {
    return JSON.parse(raw);
  } catch {
    return raw;
  }
}

function defaultHitlPreviewContext(): string {
  return JSON.stringify({
    fieldValues: { feedback: 'looks good' },
    reason: 'short explanation',
    reasons: [],
    actions: [],
    context: {},
    global: {}
  }, null, 2);
}

function defaultLlmPreviewContext(): string {
  return JSON.stringify({
    output: '{"headline":"Example headline","summary":"Example summary"}',
    input: {},
    context: {},
    global: {}
  }, null, 2);
}

function extractPreviewError(err: unknown): string {
  if (err && typeof err === 'object') {
    const httpErr = err as { error?: { error?: string } | string; message?: string; status?: number };
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
  return 'Preview render failed';
}
