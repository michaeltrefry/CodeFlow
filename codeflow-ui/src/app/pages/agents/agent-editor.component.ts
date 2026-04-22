import { Component, inject, input, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
import { AgentConfig, AgentOutputDeclaration } from '../../core/models';

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
            <code>{{ '{{decision}}' }}</code> is special — its value becomes the decision kind sent to the workflow.
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
            <input [(ngModel)]="model" name="model" placeholder="gpt-5" />
          </div>
        </div>

        <div class="form-field">
          <label>System prompt</label>
          <textarea [(ngModel)]="systemPrompt" name="systemPrompt" rows="4"></textarea>
        </div>

        <div class="form-field">
          <label>Prompt template</label>
          <textarea [(ngModel)]="promptTemplate" name="promptTemplate" rows="4" placeholder="Review the following input: {{ '{{input}}' }}"></textarea>
        </div>

        <div class="grid-two">
          <div class="form-field">
            <label>Max tokens</label>
            <input type="number" [(ngModel)]="maxTokens" name="maxTokens" min="1" />
          </div>
          <div class="form-field">
            <label>Temperature</label>
            <input type="number" [(ngModel)]="temperature" name="temperature" step="0.1" min="0" max="2" />
          </div>
        </div>
      }

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
              <textarea rows="2" class="mono"
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
    .icon-button {
      width: 22px; height: 22px; padding: 0; border-radius: 50%;
      border: 1px solid var(--color-border); background: var(--color-surface);
      cursor: pointer; color: inherit;
    }
    .icon-button:hover { border-color: #f85149; color: #f85149; }
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
  readonly model = signal('gpt-5');
  readonly systemPrompt = signal('');
  readonly promptTemplate = signal('');
  readonly outputTemplate = signal('');
  readonly maxTokens = signal<number | undefined>(undefined);
  readonly temperature = signal<number | undefined>(undefined);
  readonly outputs = signal<AgentOutputDeclaration[]>([
    { kind: 'Completed', description: null, payloadExample: null },
    { kind: 'Failed', description: null, payloadExample: null }
  ]);

  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

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
          this.model.set((config['model'] as string) ?? 'gpt-5');
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
        }
      });
    }
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
}

function tryParseJson(raw: string): unknown {
  try {
    return JSON.parse(raw);
  } catch {
    return raw;
  }
}
