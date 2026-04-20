import { Component, inject, input, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
import { AgentConfig } from '../../core/models';

type ToolCategory = 'host' | 'execution' | 'mcp' | 'sub-agent';

interface ToolEntry {
  id: string;
  label: string;
  category: ToolCategory;
}

const KNOWN_TOOLS: ToolEntry[] = [
  { id: 'host.search', label: 'Host: search', category: 'host' },
  { id: 'host.fetch', label: 'Host: HTTP fetch', category: 'host' },
  { id: 'execution.python', label: 'Execution: Python', category: 'execution' },
  { id: 'execution.bash', label: 'Execution: Bash', category: 'execution' },
  { id: 'mcp:artifact-store:read', label: 'MCP: artifact-store/read', category: 'mcp' },
  { id: 'mcp:artifact-store:write', label: 'MCP: artifact-store/write', category: 'mcp' },
  { id: 'sub-agent.summarize', label: 'Sub-agent: summarize', category: 'sub-agent' },
  { id: 'sub-agent.review', label: 'Sub-agent: review', category: 'sub-agent' }
];

@Component({
  selector: 'cf-agent-editor',
  standalone: true,
  imports: [FormsModule, RouterLink],
  template: `
    <header class="page-header">
      <div>
        <h1>{{ existingKey() ? 'New version of ' + existingKey() : 'New agent' }}</h1>
        <p class="muted">Saving always creates a new immutable version.</p>
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

        <div class="form-field">
          <label>Allowed tools</label>
          <div class="tool-grid">
            @for (group of toolGroups; track group.category) {
              <div class="tool-group">
                <div class="tool-group-title">{{ group.label }}</div>
                @for (tool of group.tools; track tool.id) {
                  <label class="tool-option">
                    <input type="checkbox" [checked]="allowedTools().has(tool.id)" (change)="toggleTool(tool.id, $event)" />
                    <span>{{ tool.label }}</span>
                  </label>
                }
              </div>
            }
          </div>
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

        <div class="form-field">
          <label>
            <input type="checkbox" [(ngModel)]="enableHostTools" name="enableHostTools" />
            Enable host tools
          </label>
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
    .page-header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      margin-bottom: 1.5rem;
    }
    .muted {
      color: var(--color-muted);
    }
    .small {
      font-size: 0.8rem;
    }
    .tool-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
      gap: 1rem;
    }
    .tool-group-title {
      font-weight: 600;
      margin-bottom: 0.25rem;
      text-transform: uppercase;
      color: var(--color-muted);
      font-size: 0.8rem;
    }
    .tool-option {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      text-transform: none;
      letter-spacing: normal;
      margin-bottom: 0.25rem;
      color: var(--color-text);
    }
  `]
})
export class AgentEditorComponent implements OnInit {
  private readonly api = inject(AgentsApi);
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
  readonly allowedTools = signal<Set<string>>(new Set());
  readonly maxTokens = signal<number | undefined>(undefined);
  readonly temperature = signal<number | undefined>(undefined);
  readonly enableHostTools = signal(true);

  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly toolGroups = [
    { category: 'host' as ToolCategory, label: 'Host tools', tools: KNOWN_TOOLS.filter(t => t.category === 'host') },
    { category: 'execution' as ToolCategory, label: 'Execution', tools: KNOWN_TOOLS.filter(t => t.category === 'execution') },
    { category: 'mcp' as ToolCategory, label: 'MCP', tools: KNOWN_TOOLS.filter(t => t.category === 'mcp') },
    { category: 'sub-agent' as ToolCategory, label: 'Sub-agents', tools: KNOWN_TOOLS.filter(t => t.category === 'sub-agent') }
  ];

  ngOnInit(): void {
    const existing = this.existingKey();
    if (existing) {
      this.key.set(existing);
      this.api.getLatest(existing).subscribe({
        next: version => {
          const config = version.config ?? {};
          this.type.set(version.type === 'hitl' ? 'hitl' : 'agent');
          this.name.set((config['name'] as string) ?? '');
          this.description.set((config['description'] as string) ?? '');
          this.provider.set((config['provider'] as 'openai' | 'anthropic' | 'lmstudio') ?? 'openai');
          this.model.set((config['model'] as string) ?? 'gpt-5');
          this.systemPrompt.set((config['systemPrompt'] as string) ?? '');
          this.promptTemplate.set((config['promptTemplate'] as string) ?? '');
          const allowed = (config['allowedTools'] as string[]) ?? [];
          this.allowedTools.set(new Set(allowed));
          this.maxTokens.set(config['maxTokens'] as number | undefined);
          this.temperature.set(config['temperature'] as number | undefined);
          this.enableHostTools.set(config['enableHostTools'] !== false);
        }
      });
    }
  }

  toggleTool(id: string, event: Event): void {
    const next = new Set(this.allowedTools());
    if ((event.target as HTMLInputElement).checked) {
      next.add(id);
    } else {
      next.delete(id);
    }
    this.allowedTools.set(next);
  }

  submit(event: Event): void {
    event.preventDefault();
    this.saving.set(true);
    this.error.set(null);

    const config: AgentConfig = {
      type: this.type(),
      name: this.name() || undefined,
      description: this.description() || undefined
    };

    if (this.type() === 'agent') {
      config.provider = this.provider();
      config.model = this.model();
      config.systemPrompt = this.systemPrompt() || undefined;
      config.promptTemplate = this.promptTemplate() || undefined;
      const toolsArr = Array.from(this.allowedTools());
      if (toolsArr.length) {
        config.allowedTools = toolsArr;
      }
      if (this.maxTokens() !== undefined) {
        config.maxTokens = this.maxTokens();
      }
      if (this.temperature() !== undefined) {
        config.temperature = this.temperature();
      }
      config.enableHostTools = this.enableHostTools();
    }

    const existingKey = this.existingKey();
    const save$ = existingKey
      ? this.api.addVersion(existingKey, config)
      : this.api.create(this.key(), config);

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
