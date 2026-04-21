import { Component, inject, input, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
import { AgentConfig } from '../../core/models';

@Component({
  selector: 'cf-agent-editor',
  standalone: true,
  imports: [FormsModule, RouterLink],
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
  readonly maxTokens = signal<number | undefined>(undefined);
  readonly temperature = signal<number | undefined>(undefined);

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
          this.maxTokens.set(config['maxTokens'] as number | undefined);
          this.temperature.set(config['temperature'] as number | undefined);
        }
      });
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
