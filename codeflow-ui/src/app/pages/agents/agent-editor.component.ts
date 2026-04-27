import {
  Component,
  EventEmitter,
  Output,
  booleanAttribute,
  computed,
  effect,
  inject,
  input,
  signal,
  OnInit
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
import { LlmProvidersApi } from '../../core/llm-providers.api';
import { AgentConfig, AgentOutputDeclaration, DecisionOutputTemplateMode, LlmProviderKey, LlmProviderModelOption } from '../../core/models';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';
import { CardComponent } from '../../ui/card.component';
import { TabsComponent, TabItem } from '../../ui/tabs.component';
import { MonacoScriptEditorComponent } from '../workflows/editor/monaco-script-editor.component';

interface DecisionTemplateRow {
  port: string;
  template: string;
  preview: string;
  previewError: string | null;
  previewPending: boolean;
}

interface OutputRow {
  kind: string;
  description: string | null;
  payloadExample: unknown;
  template: string;
  preview: string;
  previewError: string | null;
  previewPending: boolean;
  expanded: boolean;
}

type EditorTab = 'identity' | 'prompt' | 'model' | 'outputs';

@Component({
  selector: 'cf-agent-editor',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink,
    PageHeaderComponent, ButtonComponent, ChipComponent, CardComponent, TabsComponent,
    MonacoScriptEditorComponent,
  ],
  template: `
    <div [class.page]="!embedded()">
      @if (!embedded()) {
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
      }

      <div [class.card]="!embedded()" style="padding: 0 20px">
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
                  <cf-monaco-script-editor
                    class="hitl-template-editor"
                    language="plaintext"
                    [value]="outputTemplate()"
                    (valueChange)="outputTemplate.set($event)"></cf-monaco-script-editor>
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
                  @if (availableModels().length > 0) {
                    <select class="select mono" [ngModel]="model()" (ngModelChange)="model.set($event)" name="model">
                      @for (option of modelOptions(); track option.value) {
                        <option [value]="option.value">{{ option.label }}</option>
                      }
                    </select>
                    @if (modelNotConfigured()) {
                      <span class="field-hint">
                        Current value <code>{{ model() }}</code> isn't in the configured list. Add it on the
                        <a routerLink="/settings/llm-providers" class="mono-link">LLM providers page ↗</a> to keep it as a canonical choice.
                      </span>
                    } @else {
                      <span class="field-hint">
                        Manage the choices on the <a routerLink="/settings/llm-providers" class="mono-link">LLM providers page ↗</a>.
                      </span>
                    }
                  } @else {
                    <input class="input mono" [(ngModel)]="model" name="model" placeholder="gpt-5.4" />
                    <span class="field-hint">
                      No models configured for {{ provider() }} yet — add some on the
                      <a routerLink="/settings/llm-providers" class="mono-link">LLM providers page ↗</a> to get a dropdown.
                    </span>
                  }
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
                <h3>Decisions</h3>
                <p>
                  Each decision this agent emits becomes an output port on workflow nodes. Optionally attach a
                  Scriban <strong>decision template</strong> to reshape the artifact passed downstream, and a
                  <strong>payload example</strong> that documents the JSON shape the agent produces.
                  Baseline agents at least emit <code>Completed</code> and <code>Failed</code>. See the
                  <a class="mono-link" href="https://github.com/michaeltrefry/CodeFlow/blob/main/docs/decision-output-templates.md" target="_blank" rel="noopener noreferrer">decision template guide ↗</a>.
                  A routing script that calls <code>setOutput()</code> still wins over any template here.
                </p>
              </div>

              <label class="field">
                <span class="field-label">Preview scope <span class="muted small">(JSON matching the template variables below)</span></span>
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
                      <div class="field span-2 output-advanced">
                        <button type="button" class="advanced-toggle"
                                [attr.aria-expanded]="output.expanded"
                                (click)="toggleOutputExpanded(i)">
                          <span class="advanced-caret" [attr.data-expanded]="output.expanded ? 'true' : null">▸</span>
                          <span>{{ output.expanded ? 'Hide' : 'Show' }} decision template &amp; payload example</span>
                          @if (!output.expanded && output.template) {
                            <cf-chip mono>template</cf-chip>
                          }
                          @if (!output.expanded && output.payloadExample !== null && output.payloadExample !== undefined) {
                            <cf-chip mono>payload</cf-chip>
                          }
                        </button>
                        @if (output.expanded) {
                          <div class="advanced-body">
                            <label class="field">
                              <span class="field-label">Decision template <span class="muted small">(Scriban — leave blank to pass artifact through unchanged)</span></span>
                              <textarea class="textarea mono" rows="10"
                                        [ngModel]="output.template"
                                        (ngModelChange)="updateOutput(i, { template: $event })"
                                        [name]="'output_template_' + i"
                                        placeholder="[{{ '{{ decision }}' }}] {{ '{{ output.headline }}' }}"></textarea>
                            </label>
                            <label class="field">
                              <span class="field-label">Template preview</span>
                              @if (output.previewError) {
                                <pre class="preview-error mono">{{ output.previewError }}</pre>
                              } @else {
                                <pre class="preview-output mono">{{ output.preview || '(add a template to see the rendered output)' }}</pre>
                              }
                            </label>
                            <label class="field">
                              <div class="row" style="justify-content: space-between; align-items: center">
                                <span class="field-label">Payload example (JSON)</span>
                                <button type="button" cf-button variant="ghost" size="sm" icon="refresh"
                                        [disabled]="!canGeneratePayload(output)"
                                        (click)="generatePayloadFromTemplate(i)">
                                  Generate from template
                                </button>
                              </div>
                              <textarea class="textarea mono" rows="8"
                                        [ngModel]="payloadExampleText(output)"
                                        (ngModelChange)="updatePayloadExample(i, $event)"
                                        [name]="'output_example_' + i"
                                        placeholder='{"reasons": ["..."]}'></textarea>
                            </label>
                          </div>
                        }
                      </div>
                    </div>
                  </div>
                }
                <button type="button" cf-button size="sm" icon="plus" style="align-self: flex-start"
                        (click)="addOutput()">Add decision</button>
              </div>
            </div>

            <div class="form-section">
              <div class="form-section-head">
                <h3>Fallback templates</h3>
                <p>
                  Catch-all templates keyed by <code>*</code> or by a port name not declared above
                  (e.g. ports emitted only by a routing script). Rarely needed.
                </p>
              </div>
              <div class="stack">
                @for (row of fallbackTemplates(); track $index; let i = $index) {
                  <div class="output-card">
                    <div class="row" style="justify-content: space-between">
                      <cf-chip mono>{{ row.port || '(unnamed port)' }}</cf-chip>
                      <button type="button" cf-button variant="ghost" size="sm" icon="x" iconOnly
                              (click)="removeFallbackTemplate(i)" [attr.aria-label]="'Remove template'"></button>
                    </div>
                    <label class="field">
                      <span class="field-label">Output port <span class="muted small">(or <code>*</code>)</span></span>
                      <input class="input mono" type="text"
                             [ngModel]="row.port"
                             (ngModelChange)="updateFallbackTemplate(i, { port: $event })"
                             [name]="'fallback_template_port_' + i" placeholder="*" />
                    </label>
                    <label class="field">
                      <span class="field-label">Template</span>
                      <textarea class="textarea mono" rows="5"
                                [ngModel]="row.template"
                                (ngModelChange)="updateFallbackTemplate(i, { template: $event })"
                                [name]="'fallback_template_body_' + i"
                                placeholder="[{{ '{{ decision }}' }}] {{ '{{ output.headline }}' }}"></textarea>
                    </label>
                    <label class="field">
                      <span class="field-label">Preview</span>
                      @if (row.previewError) {
                        <pre class="preview-error mono">{{ row.previewError }}</pre>
                      } @else {
                        <pre class="preview-output mono">{{ row.preview || '(enter a template to see the rendered output)' }}</pre>
                      }
                    </label>
                  </div>
                }
                <button type="button" cf-button size="sm" icon="plus" style="align-self: flex-start"
                        (click)="addFallbackTemplate()">Add fallback template</button>
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
    .output-advanced {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .advanced-toggle {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 6px 0;
      background: transparent;
      border: 0;
      color: var(--muted);
      font: inherit;
      cursor: pointer;
      align-self: flex-start;
    }
    .advanced-toggle:hover { color: var(--fg); }
    .advanced-caret {
      display: inline-block;
      transition: transform 0.12s ease-out;
      font-size: 0.9em;
    }
    .advanced-caret[data-expanded="true"] { transform: rotate(90deg); }
    .advanced-body {
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
  `]
})
export class AgentEditorComponent implements OnInit {
  private readonly agentsApi = inject(AgentsApi);
  private readonly llmProvidersApi = inject(LlmProvidersApi);
  private readonly router = inject(Router);

  readonly existingKey = input<string | undefined>(undefined, { alias: 'key' });
  readonly embedded = input(false, { transform: booleanAttribute });
  readonly initialConfig = input<AgentConfig | null>(null);
  readonly initialType = input<'agent' | 'hitl' | null>(null);

  @Output() saveRequested = new EventEmitter<{ key: string; type: 'agent' | 'hitl'; config: AgentConfig }>();

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
  readonly outputs = signal<OutputRow[]>([
    emptyOutputRow('Completed'),
    emptyOutputRow('Failed'),
  ]);

  readonly fallbackTemplates = signal<DecisionTemplateRow[]>([]);

  readonly previewContextText = signal(defaultLlmPreviewContext());
  readonly previewContextPlaceholder = computed(() => {
    if (this.type() === 'hitl') return defaultHitlPreviewContext();
    const sample = pickSampleOutputRow(this.outputs());
    return defaultLlmPreviewContext(
      sample ? coerceSamplePayload(sample.payloadExample) : undefined,
      sample?.kind);
  });
  readonly previewContextError = signal<string | null>(null);

  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly configuredModels = signal<LlmProviderModelOption[]>([]);
  readonly availableModels = computed(() => {
    const all = this.configuredModels();
    const current = this.provider();
    return all.filter(o => o.provider === current).map(o => o.model);
  });
  readonly modelNotConfigured = computed(() => {
    const current = this.model();
    if (!current) return false;
    return !this.availableModels().includes(current);
  });
  readonly modelOptions = computed(() => {
    const options = this.availableModels().map(value => ({ value, label: value }));
    const current = this.model();
    if (current && !options.some(o => o.value === current)) {
      options.unshift({ value: current, label: `${current} (unconfigured)` });
    }
    return options;
  });

  readonly tab = signal<EditorTab>('identity');
  readonly tabs = computed<TabItem[]>(() => [
    { value: 'identity', label: 'Identity' },
    { value: 'prompt', label: this.type() === 'hitl' ? 'Output template' : 'Prompt & output' },
    { value: 'model', label: 'Model' },
    { value: 'outputs', label: 'Decisions', count: this.outputs().length },
  ]);

  constructor() {
    effect(() => {
      const outputRows = this.outputs();
      const fallbackRows = this.fallbackTemplates();
      const contextText = this.previewContextText();
      const mode: DecisionOutputTemplateMode = this.type() === 'hitl' ? 'hitl' : 'llm';

      let parsedContext: Record<string, unknown> = {};
      try {
        const parsed = contextText.trim() ? JSON.parse(contextText) : {};
        if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
          parsedContext = parsed as Record<string, unknown>;
          this.previewContextError.set(null);
        } else {
          this.clearPreviewTimers();
          this.previewSignatures.clear();
          this.previewContextError.set('Preview context must be a JSON object.');
          return;
        }
      } catch {
        this.clearPreviewTimers();
        this.previewSignatures.clear();
        this.previewContextError.set('Preview context is not valid JSON.');
        return;
      }

      outputRows.forEach((row, index) => {
        const key = `output:${index}`;
        if (!row.template.trim()) {
          this.cancelPreviewRender(key);
          this.previewSignatures.delete(key);
          if (row.preview || row.previewError || row.previewPending) {
            this.patchOutputRowSilently(index, { preview: '', previewError: null, previewPending: false });
          }
          return;
        }
        const port = row.kind.trim() || '*';
        const signature = previewSignature(mode, port, row.template, contextText);
        if (this.previewSignatures.get(key) === signature) return;
        this.previewSignatures.set(key, signature);
        this.schedulePreviewRender(
          key,
          row.template, port, mode, parsedContext,
          patch => this.patchOutputRowSilently(index, patch)
        );
      });

      fallbackRows.forEach((row, index) => {
        const key = `fallback:${index}`;
        if (!row.template.trim()) {
          this.cancelPreviewRender(key);
          this.previewSignatures.delete(key);
          if (row.preview || row.previewError || row.previewPending) {
            this.patchFallbackRowSilently(index, { preview: '', previewError: null, previewPending: false });
          }
          return;
        }
        const port = row.port.trim() || '*';
        const signature = previewSignature(mode, port, row.template, contextText);
        if (this.previewSignatures.get(key) === signature) return;
        this.previewSignatures.set(key, signature);
        this.schedulePreviewRender(
          key,
          row.template, port, mode, parsedContext,
          patch => this.patchFallbackRowSilently(index, patch)
        );
      });
    });
  }

  ngOnInit(): void {
    this.llmProvidersApi.listModels().subscribe({
      next: options => this.configuredModels.set(options),
      error: () => this.configuredModels.set([]),
    });
    if (this.embedded()) {
      const existing = this.existingKey();
      if (existing) this.key.set(existing);
      const providedType = this.initialType();
      if (providedType) this.type.set(providedType);
      const providedConfig = this.initialConfig();
      if (providedConfig) {
        this.hydrateFromConfig(providedConfig, providedType ?? 'agent');
      }
      return;
    }

    const existing = this.existingKey();
    if (existing) {
      this.key.set(existing);
      this.agentsApi.getLatest(existing).subscribe({
        next: version => {
          const config = version.config ?? {};
          const resolvedType = version.type === 'hitl' ? 'hitl' : 'agent';
          this.hydrateFromConfig(config, resolvedType);
        }
      });
    }
  }

  private hydrateFromConfig(config: AgentConfig, resolvedType: 'agent' | 'hitl'): void {
    this.type.set(resolvedType);
    this.name.set((config['name'] as string) ?? '');
    this.description.set((config['description'] as string) ?? '');
    this.provider.set((config['provider'] as 'openai' | 'anthropic' | 'lmstudio') ?? 'openai');
    this.model.set((config['model'] as string) ?? 'gpt-5.4');
    this.systemPrompt.set((config['systemPrompt'] as string) ?? '');
    this.promptTemplate.set((config['promptTemplate'] as string) ?? '');
    this.outputTemplate.set((config['outputTemplate'] as string) ?? '');
    this.maxTokens.set(config['maxTokens'] as number | undefined);
    this.temperature.set(config['temperature'] as number | undefined);
    const templates = (config['decisionOutputTemplates'] as Record<string, string> | undefined) ?? {};
    const declared = config['outputs'];
    const declaredKinds = new Set<string>();
    if (Array.isArray(declared) && declared.length > 0) {
      this.outputs.set(declared.map(d => {
        const kind = d.kind ?? '';
        declaredKinds.add(kind);
        const template = String(templates[kind] ?? '');
        const payloadExample = d.payloadExample ?? null;
        return {
          kind,
          description: d.description ?? null,
          payloadExample,
          template,
          preview: '',
          previewError: null,
          previewPending: false,
          expanded: template.length > 0 || payloadExample !== null
        } as OutputRow;
      }));
    } else {
      // Keep defaults but hydrate templates for them
      this.outputs.update(rows => rows.map(r => {
        const template = String(templates[r.kind] ?? '');
        return {
          ...r,
          template,
          expanded: template.length > 0 || r.payloadExample !== null
        };
      }));
      this.outputs().forEach(r => declaredKinds.add(r.kind));
    }
    const orphans: DecisionTemplateRow[] = Object.entries(templates)
      .filter(([port]) => !declaredKinds.has(port))
      .map(([port, template]) => ({
        port,
        template: String(template ?? ''),
        preview: '',
        previewError: null,
        previewPending: false
      }));
    this.fallbackTemplates.set(orphans);
    if (resolvedType === 'hitl') {
      this.previewContextText.set(defaultHitlPreviewContext());
    } else {
      const sample = pickSampleOutputRow(this.outputs());
      this.previewContextText.set(defaultLlmPreviewContext(
        sample ? coerceSamplePayload(sample.payloadExample) : undefined,
        sample?.kind));
    }
  }

  addFallbackTemplate(): void {
    this.fallbackTemplates.set([
      ...this.fallbackTemplates(),
      { port: '', template: '', preview: '', previewError: null, previewPending: false }
    ]);
  }

  removeFallbackTemplate(index: number): void {
    this.fallbackTemplates.set(this.fallbackTemplates().filter((_, i) => i !== index));
  }

  updateFallbackTemplate(index: number, patch: Partial<DecisionTemplateRow>): void {
    this.fallbackTemplates.set(this.fallbackTemplates().map((row, i) =>
      i === index ? { ...row, ...patch } : row));
  }

  private patchFallbackRowSilently(index: number, patch: Partial<DecisionTemplateRow>): void {
    this.fallbackTemplates.update(rows => rows.map((row, i) =>
      i === index ? { ...row, ...patch } : row));
  }

  private patchOutputRowSilently(index: number, patch: Partial<OutputRow>): void {
    this.outputs.update(rows => rows.map((row, i) =>
      i === index ? { ...row, ...patch } : row));
  }

  private readonly previewTimers = new Map<string, ReturnType<typeof setTimeout>>();
  private readonly previewSignatures = new Map<string, string>();

  private schedulePreviewRender(
    key: string,
    template: string,
    port: string,
    mode: DecisionOutputTemplateMode,
    parsedContext: Record<string, unknown>,
    commit: (patch: { preview?: string; previewError?: string | null; previewPending?: boolean }) => void
  ): void {
    this.cancelPreviewRender(key);
    const timer = setTimeout(() => {
      this.previewTimers.delete(key);
      this.runPreview(template, port, mode, parsedContext, commit);
    }, 200);
    this.previewTimers.set(key, timer);
  }

  private cancelPreviewRender(key: string): void {
    const timer = this.previewTimers.get(key);
    if (timer === undefined) return;
    clearTimeout(timer);
    this.previewTimers.delete(key);
  }

  private clearPreviewTimers(): void {
    this.previewTimers.forEach(timer => clearTimeout(timer));
    this.previewTimers.clear();
  }

  private runPreview(
    template: string,
    port: string,
    mode: DecisionOutputTemplateMode,
    parsedContext: Record<string, unknown>,
    commit: (patch: { preview?: string; previewError?: string | null; previewPending?: boolean }) => void
  ): void {
    this.agentsApi.renderDecisionOutputTemplate({
      template,
      mode,
      decision: port,
      outputPortName: port,
      output: mode === 'llm' ? stringifyPreviewOutput(parsedContext['output']) : undefined,
      input: mode === 'llm' ? parsedContext['input'] : undefined,
      fieldValues: mode === 'hitl'
        ? asRecord(parsedContext['input']) ?? asRecord(parsedContext['fieldValues'])
        : undefined,
      reason: mode === 'hitl' ? asString(parsedContext['reason']) : undefined,
      reasons: mode === 'hitl' ? asStringArray(parsedContext['reasons']) : undefined,
      actions: mode === 'hitl' ? asStringArray(parsedContext['actions']) : undefined,
      context: asRecord(parsedContext['context']),
      global: asRecord(parsedContext['global'])
    }).subscribe({
      next: response => commit({ preview: response.rendered, previewError: null, previewPending: false }),
      error: err => commit({ preview: '', previewError: extractPreviewError(err), previewPending: false })
    });
  }

  addOutput(): void {
    this.outputs.set([...this.outputs(), emptyOutputRow('')]);
  }

  removeOutput(index: number): void {
    this.outputs.set(this.outputs().filter((_, i) => i !== index));
  }

  updateOutput(index: number, patch: Partial<OutputRow>): void {
    this.outputs.set(this.outputs().map((o, i) => (i === index ? { ...o, ...patch } : o)));
  }

  toggleOutputExpanded(index: number): void {
    const row = this.outputs()[index];
    if (!row) return;
    this.updateOutput(index, { expanded: !row.expanded });
  }

  payloadExampleText(output: OutputRow): string {
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

  generatePayloadFromTemplate(index: number): void {
    const row = this.outputs()[index];
    if (!row) return;
    const generated = generatePayloadFromTemplate(row.template);
    if (generated === null) return;
    this.updateOutput(index, { payloadExample: generated });
  }

  canGeneratePayload(row: OutputRow): boolean {
    return !!row.template && /\boutput\.[a-zA-Z_]/.test(row.template);
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

    const outputRows = this.outputs();
    const cleanedOutputs: AgentOutputDeclaration[] = outputRows
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

    const templatesMap: Record<string, string> = {};
    for (const row of outputRows) {
      const kind = (row.kind ?? '').trim();
      if (kind && row.template.trim().length > 0) {
        templatesMap[kind] = row.template;
      }
    }
    for (const row of this.fallbackTemplates()) {
      const port = row.port.trim();
      if (port && row.template.trim().length > 0) {
        templatesMap[port] = row.template;
      }
    }
    if (Object.keys(templatesMap).length > 0) {
      config.decisionOutputTemplates = templatesMap;
    }

    if (this.embedded()) {
      // Parent owns the API call + error surfacing.
      this.saving.set(false);
      this.saveRequested.emit({ key: this.key(), type: this.type(), config });
      return;
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

function emptyOutputRow(kind: string): OutputRow {
  return {
    kind,
    description: null,
    payloadExample: null,
    template: '',
    preview: '',
    previewError: null,
    previewPending: false,
    expanded: false
  };
}

function generatePayloadFromTemplate(template: string): Record<string, unknown> | null {
  // Match references like `output.foo`, `output.foo.bar`, etc.
  // Stops at any non-identifier character; ignores bracket access on purpose.
  const re = /\boutput\.([a-zA-Z_][\w]*(?:\.[a-zA-Z_][\w]*)*)/g;
  const paths: string[][] = [];
  const seen = new Set<string>();
  for (const m of template.matchAll(re)) {
    if (seen.has(m[1])) continue;
    seen.add(m[1]);
    paths.push(m[1].split('.'));
  }
  if (paths.length === 0) return null;
  const result: Record<string, unknown> = {};
  for (const path of paths) {
    let cur: Record<string, unknown> = result;
    for (let i = 0; i < path.length; i++) {
      const key = path[i];
      const isLeaf = i === path.length - 1;
      if (isLeaf) {
        if (!(key in cur)) cur[key] = `<${path.join('.')}>`;
      } else {
        const next = cur[key];
        if (!next || typeof next !== 'object' || Array.isArray(next)) {
          cur[key] = {};
        }
        cur = cur[key] as Record<string, unknown>;
      }
    }
  }
  return result;
}

function defaultHitlPreviewContext(): string {
  return JSON.stringify({
    input: { feedback: 'looks good' },
    reason: 'short explanation',
    reasons: [],
    actions: [],
    context: {},
    global: {}
  }, null, 2);
}

function defaultLlmPreviewContext(sampleOutput?: unknown, sampleDecision?: string): string {
  const decision = sampleDecision && sampleDecision.trim() ? sampleDecision : 'Completed';
  const output = sampleOutput !== undefined ? sampleOutput : 'Example markdown content';
  return JSON.stringify({
    decision,
    outputPortName: decision,
    output,
    input: {},
    context: {},
    global: {}
  }, null, 2);
}

function pickSampleOutputRow(rows: readonly OutputRow[]): OutputRow | null {
  return rows.find(r => r.payloadExample !== null && r.payloadExample !== undefined) ?? null;
}

function coerceSamplePayload(value: unknown): unknown {
  if (typeof value !== 'string') return value;
  const trimmed = value.trim();
  if (!trimmed) return value;
  try { return JSON.parse(trimmed); } catch { return value; }
}

function stringifyPreviewOutput(value: unknown): string | undefined {
  if (value === undefined || value === null) return undefined;
  if (typeof value === 'string') return value;
  return JSON.stringify(value);
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  return value as Record<string, unknown>;
}

function asString(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined;
}

function asStringArray(value: unknown): string[] | undefined {
  if (!Array.isArray(value)) return undefined;
  return value.filter(item => typeof item === 'string') as string[];
}

function previewSignature(
  mode: DecisionOutputTemplateMode,
  port: string,
  template: string,
  contextText: string
): string {
  return JSON.stringify([mode, port, template, contextText]);
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
