import { Component, computed, effect, inject, input, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
import { AgentConfig, AgentOutputDeclaration, DecisionOutputTemplateMode } from '../../core/models';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';
import { CardComponent } from '../../ui/card.component';
import { TabsComponent, TabItem } from '../../ui/tabs.component';

interface DecisionTemplateRow {
  port: string;
  template: string;
  preview: string;
  previewError: string | null;
  previewPending: boolean;
}

type EditorTab = 'identity' | 'prompt' | 'model' | 'outputs' | 'templates';

@Component({
  selector: 'cf-agent-editor',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink,
    PageHeaderComponent, ButtonComponent, ChipComponent, CardComponent, TabsComponent,
  ],
  template: `
    <div class="page">
      <cf-page-header
        [title]="existingKey() ? 'Edit ' + existingKey() : 'New agent'"
        subtitle="Saving always creates a new immutable version. Tool access flows through roles assigned on the agent's detail page.">
        <a routerLink="/agents">
          <button type="button" cf-button variant="ghost" icon="back">Cancel</button>
        </a>
        <button type="button" cf-button variant="primary" icon="check"
                (click)="submit($event)" [disabled]="saving()">
          {{ saving() ? 'Saving…' : (existingKey() ? 'Save new version' : 'Create agent') }}
        </button>
        <div page-header-body>
          <div class="trace-header-meta">
            @if (existingKey()) { <cf-chip mono>{{ existingKey() }}</cf-chip> }
            <cf-chip [variant]="type() === 'hitl' ? 'accent' : 'default'" mono>{{ type() === 'hitl' ? 'HITL' : 'LLM agent' }}</cf-chip>
            @if (type() === 'agent') {
              <cf-chip mono>{{ provider() }}</cf-chip>
              <cf-chip mono>{{ model() }}</cf-chip>
            }
          </div>
        </div>
      </cf-page-header>

      <div class="card" style="padding: 0 20px">
        <cf-tabs [items]="tabs()" [value]="tab()" (valueChange)="tab.set($any($event))"></cf-tabs>
      </div>

      <form (submit)="submit($event)">
        @if (tab() === 'identity') {
          <cf-card>
            <div class="form-section">
              <div class="form-section-head">
                <h3>Identity</h3>
                <p>Key is immutable once created. Rename the display name freely.</p>
              </div>
              <div class="form-grid">
                <label class="field">
                  <span class="field-label">Agent key</span>
                  <input class="input mono" [(ngModel)]="key" name="key"
                         [disabled]="!!existingKey()" required placeholder="reviewer-v1" />
                  <span class="field-hint">Lowercase letters, digits, '-' or '_'.</span>
                </label>
                <label class="field">
                  <span class="field-label">Display name</span>
                  <input class="input" [(ngModel)]="name" name="name" placeholder="Technical reviewer" />
                </label>
                <label class="field">
                  <span class="field-label">Type</span>
                  <div class="seg" style="width: fit-content">
                    <button type="button" [attr.data-active]="type() === 'agent' ? 'true' : null" (click)="type.set('agent')">LLM agent</button>
                    <button type="button" [attr.data-active]="type() === 'hitl' ? 'true' : null" (click)="type.set('hitl')">HITL</button>
                  </div>
                </label>
                <label class="field span-2">
                  <span class="field-label">Description</span>
                  <textarea class="textarea" [(ngModel)]="description" name="description" rows="2"
                            style="font-family: var(--font-sans); font-size: var(--fs-md)"></textarea>
                </label>
              </div>
            </div>
          </cf-card>
        }

        @if (tab() === 'prompt') {
          @if (type() === 'agent') {
            <cf-card>
              <div class="form-section">
                <div class="form-section-head">
                  <h3>System prompt</h3>
                  <p>Prepended to every call. Keep instruction-focused and stable.</p>
                </div>
                <div class="code-field">
                  <div class="code-field-head"><span>system.md</span><span>markdown</span></div>
                  <textarea class="textarea" rows="8"
                            [(ngModel)]="systemPrompt" name="systemPrompt"
                            style="border: 0; border-radius: 0; background: var(--bg)"></textarea>
                </div>
              </div>
              <div class="form-section">
                <div class="form-section-head">
                  <h3>Prompt template</h3>
                  <p>
                    Rendered per-round with Scriban substitution plus conditionals and loops —
                    see the <a class="mono-link" href="https://github.com/michaeltrefry/CodeFlow/blob/main/docs/prompt-templates.md" target="_blank" rel="noopener noreferrer">prompt template guide ↗</a>.
                  </p>
                </div>
                <div class="code-field">
                  <div class="code-field-head"><span>input.scriban</span><span>scriban</span></div>
                  <textarea class="textarea mono" rows="18"
                            [(ngModel)]="promptTemplate" name="promptTemplate"
                            style="border: 0; border-radius: 0; background: var(--bg); min-height: 28rem; resize: vertical"
                            placeholder="Review the following input: {{ '{{input}}' }}"></textarea>
                </div>
              </div>
            </cf-card>
          } @else {
            <cf-card>
              <div class="form-section">
                <div class="form-section-head">
                  <h3>Output template</h3>
                  <p>
                    Defines exactly how reviewers enter their response. Use <code>{{ '{{name}}' }}</code> for free text,
                    <code>{{ '{{name:Opt1|Opt2}}' }}</code> for a dropdown, <code>{{ '{{decision}}' }}</code> for decision choice,
                    or <code>{{ '{{json(name)}}' }}</code> when the artifact should be JSON.
                  </p>
                </div>
                <div class="code-field">
                  <div class="code-field-head"><span>hitl.template</span><span>form</span></div>
                  <textarea class="textarea mono" rows="10"
                            [(ngModel)]="outputTemplate" name="outputTemplate"
                            style="border: 0; border-radius: 0; background: var(--bg)"
                            placeholder="HITL decision: {{ '{{decision:Approved|Rejected}}' }}&#10;{{ '{{feedback}}' }}"></textarea>
                </div>
              </div>
            </cf-card>
          }
        }

        @if (tab() === 'model' && type() === 'agent') {
          <cf-card>
            <div class="form-section">
              <div class="form-section-head">
                <h3>Model</h3>
                <p>Provider, model id and generation parameters.</p>
              </div>
              <div class="form-grid">
                <label class="field">
                  <span class="field-label">Provider</span>
                  <select class="select" [(ngModel)]="provider" name="provider">
                    <option value="openai">OpenAI</option>
                    <option value="anthropic">Anthropic</option>
                    <option value="lmstudio">LM Studio (local)</option>
                  </select>
                </label>
                <label class="field">
                  <span class="field-label">Model</span>
                  <input class="input mono" [(ngModel)]="model" name="model" placeholder="gpt-5.4" />
                </label>
                <label class="field">
                  <span class="field-label">Temperature</span>
                  <input class="input mono" type="number" step="0.1" min="0" max="2"
                         [ngModel]="temperature() ?? null"
                         (ngModelChange)="temperature.set(coerceOptionalNumber($event))"
                         name="temperature" autocomplete="off" />
                  <span class="field-hint">0.0 for deterministic routing; 0.7 for creative synthesis.</span>
                </label>
                <label class="field">
                  <span class="field-label">Max output tokens</span>
                  <input class="input mono" type="number" min="1"
                         [ngModel]="maxTokens() ?? null"
                         (ngModelChange)="maxTokens.set(coerceOptionalNumber($event))"
                         name="maxTokens" autocomplete="off" />
                </label>
              </div>
            </div>
          </cf-card>
        }

        @if (tab() === 'model' && type() === 'hitl') {
          <cf-card>
            <div class="card-body muted">
              HITL agents don't use a model. Switch to <strong>LLM agent</strong> on the Identity tab to configure a provider and model.
            </div>
          </cf-card>
        }

        @if (tab() === 'outputs') {
          <cf-card>
            <div class="form-section">
              <div class="form-section-head">
                <h3>Declared outputs</h3>
                <p>
                  List the decision kinds this agent emits. Workflow nodes that reference this agent
                  automatically get a matching set of output ports in the canvas editor. Baseline agents at
                  least emit <code>Completed</code> and <code>Failed</code>.
                </p>
              </div>
              <div class="stack">
                @for (output of outputs(); track $index; let i = $index) {
                  <div class="output-card">
                    <div class="row" style="justify-content: space-between">
                      <cf-chip mono>{{ output.kind || '(unnamed)' }}</cf-chip>
                      <button type="button" cf-button variant="ghost" size="sm" icon="x" iconOnly
                              (click)="removeOutput(i)" [attr.aria-label]="'Remove output ' + output.kind"></button>
                    </div>
                    <div class="form-grid">
                      <label class="field">
                        <span class="field-label">Kind</span>
                        <input class="input mono" type="text"
                               [ngModel]="output.kind"
                               (ngModelChange)="updateOutput(i, { kind: $event })"
                               [name]="'output_kind_' + i" placeholder="Completed" />
                        <span class="field-hint">Becomes the port name on workflow edges.</span>
                      </label>
                      <label class="field">
                        <span class="field-label">Description</span>
                        <input class="input" type="text"
                               [ngModel]="output.description ?? ''"
                               (ngModelChange)="updateOutput(i, { description: $event || null })"
                               [name]="'output_desc_' + i" placeholder="Normal success" />
                      </label>
                      <label class="field span-2">
                        <span class="field-label">Payload example (JSON)</span>
                        <textarea class="textarea mono" rows="10"
                                  [ngModel]="payloadExampleText(output)"
                                  (ngModelChange)="updatePayloadExample(i, $event)"
                                  [name]="'output_example_' + i"
                                  placeholder='{"reasons": ["..."]}'></textarea>
                      </label>
                    </div>
                  </div>
                }
                <button type="button" cf-button size="sm" icon="plus" style="align-self: flex-start"
                        (click)="addOutput()">Add output</button>
              </div>
            </div>
          </cf-card>
        }

        @if (tab() === 'templates') {
          <cf-card>
            <div class="form-section">
              <div class="form-section-head">
                <h3>Decision output templates</h3>
                <p>
                  Reshape the artifact passed downstream once a decision lands. Key each template by the output
                  port the agent emits, or use <code>*</code> as a wildcard fallback. Scriban syntax.
                  See the <a class="mono-link" href="https://github.com/michaeltrefry/CodeFlow/blob/main/docs/decision-output-templates.md" target="_blank" rel="noopener noreferrer">guide ↗</a>.
                  A routing script that calls <code>setOutput()</code> still wins over any template here.
                </p>
              </div>

              <label class="field">
                <span class="field-label">Preview context <span class="muted small">(JSON — drives the live preview below)</span></span>
                <textarea class="textarea mono" rows="5"
                          [ngModel]="previewContextText()"
                          (ngModelChange)="previewContextText.set($event)"
                          name="decisionTemplatePreviewContext"
                          [placeholder]="previewContextPlaceholder()"></textarea>
                @if (previewContextError()) {
                  <cf-chip variant="err" dot>{{ previewContextError() }}</cf-chip>
                }
              </label>

              <div class="stack" style="margin-top: 14px">
                @for (row of decisionTemplates(); track $index; let i = $index) {
                  <div class="output-card">
                    <div class="row" style="justify-content: space-between">
                      <cf-chip mono>{{ row.port || '(unnamed port)' }}</cf-chip>
                      <button type="button" cf-button variant="ghost" size="sm" icon="x" iconOnly
                              (click)="removeDecisionTemplate(i)" [attr.aria-label]="'Remove template'"></button>
                    </div>
                    <label class="field">
                      <span class="field-label">Output port <span class="muted small">(or <code>*</code>)</span></span>
                      <input class="input mono" type="text"
                             [ngModel]="row.port"
                             (ngModelChange)="updateDecisionTemplate(i, { port: $event })"
                             [name]="'decision_template_port_' + i" placeholder="Approved" />
                    </label>
                    <label class="field">
                      <span class="field-label">Template</span>
                      <textarea class="textarea mono" rows="6"
                                [ngModel]="row.template"
                                (ngModelChange)="updateDecisionTemplate(i, { template: $event })"
                                [name]="'decision_template_body_' + i"
                                placeholder="[{{ '{{ decision }}' }}] {{ '{{ output.headline }}' }}"></textarea>
                    </label>
                    <label class="field">
                      <span class="field-label">
                        Preview
                        @if (row.previewPending) {
                          <span class="muted small">(rendering…)</span>
                        }
                      </span>
                      @if (row.previewError) {
                        <pre class="preview-error mono">{{ row.previewError }}</pre>
                      } @else {
                        <pre class="preview-output mono">{{ row.preview || '(enter a template to see the rendered output)' }}</pre>
                      }
                    </label>
                  </div>
                }
                <button type="button" cf-button size="sm" icon="plus" style="align-self: flex-start"
                        (click)="addDecisionTemplate()">Add template</button>
              </div>
            </div>
          </cf-card>
        }

        @if (error()) {
          <div class="trace-failure"><strong>Save failed:</strong> {{ error() }}</div>
        }
      </form>
    </div>
  `,
  styles: [`
    .output-card {
      padding: 14px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface-2);
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .preview-output, .preview-error {
      padding: 10px 12px;
      border-radius: var(--radius);
      background: var(--surface-2);
      border: 1px solid var(--border);
      white-space: pre-wrap;
      word-break: break-word;
      margin: 0;
      font-size: var(--fs-sm);
    }
    .preview-error {
      color: var(--sem-red);
      border-color: color-mix(in oklab, var(--sem-red) 40%, transparent);
      background: var(--err-bg);
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

  readonly previewContextText = signal(defaultLlmPreviewContext());
  readonly previewContextPlaceholder = computed(() =>
    this.type() === 'hitl' ? defaultHitlPreviewContext() : defaultLlmPreviewContext());
  readonly previewContextError = signal<string | null>(null);

  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly tab = signal<EditorTab>('identity');
  readonly tabs = computed<TabItem[]>(() => [
    { value: 'identity', label: 'Identity' },
    { value: 'prompt', label: this.type() === 'hitl' ? 'Output template' : 'Prompt & output' },
    { value: 'model', label: 'Model' },
    { value: 'outputs', label: 'Declared outputs', count: this.outputs().length },
    { value: 'templates', label: 'Decision templates', count: this.decisionTemplates().length },
  ]);

  constructor() {
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
      } catch {
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
      if (!trimmed) return undefined;
      const parsed = Number(trimmed);
      return Number.isFinite(parsed) ? parsed : undefined;
    }
    return undefined;
  }
}

function tryParseJson(raw: string): unknown {
  try { return JSON.parse(raw); } catch { return raw; }
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
