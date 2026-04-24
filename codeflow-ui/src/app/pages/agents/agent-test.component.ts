import { Component, inject, input, signal, OnInit, OnDestroy } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { AuthService } from '../../auth/auth.service';
import { AgentsApi } from '../../core/agents.api';
import { AgentConfig } from '../../core/models';
import {
  AgentTestEvent,
  AgentTestTokenUsage,
  streamAgentTest
} from '../../core/agent-test-stream';

interface LogEntry {
  id: number;
  kind:
    | 'started'
    | 'model-call-started'
    | 'model-call-completed'
    | 'tool-call-started'
    | 'tool-call-completed'
    | 'completed'
    | 'error';
  timestampUtc: string;
  title: string;
  detail?: string;
  isError?: boolean;
}

interface VariableEntry {
  name: string;
  value: string;
  detected: boolean;
}

const VARIABLE_PATTERN = /\{\{\s*([A-Za-z0-9_.\-]+)\s*\}\}/g;
const RESERVED_VARIABLES = new Set(['input']);

@Component({
  selector: 'cf-agent-test',
  standalone: true,
  imports: [FormsModule, DatePipe, RouterLink],
  template: `
    <header class="page-header">
      <div>
        <h1>Test agent</h1>
        <p class="muted monospace">{{ key() }}</p>
      </div>
      <a [routerLink]="['/agents', key()]"><button class="secondary">Back</button></a>
    </header>

    <div class="test-grid">
      <section class="test-column">
        <header class="section-header">
          <h2>Input</h2>
        </header>
        <form (submit)="submit($event)">
          <div class="form-field">
            <label>Agent version (blank = latest)</label>
            <input type="number" [(ngModel)]="agentVersion" name="agentVersion" min="1" [disabled]="running()" (change)="onVersionChanged()" />
          </div>

          <div class="form-field">
            <label>Input text <span class="muted small">(bound to {{ formatVar('input') }})</span></label>
            <textarea
              [(ngModel)]="input"
              name="input"
              rows="12"
              placeholder="Enter the content to send to the agent…"
              [disabled]="running()"></textarea>
          </div>

          @if (variables().length > 0 || customCount() > 0) {
            <div class="form-field">
              <label>Template variables</label>
              @if (configError()) {
                <p class="tag error">{{ configError() }}</p>
              }
              <div class="variable-list">
                @for (entry of variables(); track entry.name; let i = $index) {
                  <div class="variable-row">
                    <code class="variable-name" [class.custom]="!entry.detected">
                      {{ formatVar(entry.name) }}
                    </code>
                    <input
                      type="text"
                      [ngModel]="entry.value"
                      (ngModelChange)="setVariableValue(i, $event)"
                      [name]="'var_' + i"
                      [disabled]="running()"
                      placeholder="value" />
                    @if (!entry.detected) {
                      <button type="button" class="icon" (click)="removeVariable(i)" [disabled]="running()" title="Remove">×</button>
                    }
                  </div>
                }
              </div>
            </div>
          }

          <div class="row">
            <button type="button" class="secondary small" (click)="addCustomVariable()" [disabled]="running()">
              + Add variable
            </button>
          </div>

          <div class="row" style="margin-top: 1rem;">
            <button type="submit" [disabled]="running() || !input().trim()">
              {{ running() ? 'Running…' : 'Run agent' }}
            </button>
            @if (running()) {
              <button type="button" class="secondary" (click)="cancel()">Cancel</button>
            }
            @if (!running() && log().length > 0) {
              <button type="button" class="secondary" (click)="clear()">Clear</button>
            }
          </div>
        </form>
      </section>

      <section class="test-column">
        <header class="section-header">
          <h2>Live log</h2>
          <div class="row small muted">
            @if (cumulativeUsage(); as u) {
              <span class="tag">in: {{ u.inputTokens }}</span>
              <span class="tag">out: {{ u.outputTokens }}</span>
              <span class="tag accent">total: {{ u.totalTokens }}</span>
            }
            <span class="tag">tool calls: {{ toolCallCount() }}</span>
          </div>
        </header>

        @if (log().length === 0) {
          <p class="muted small">No events yet. Run the agent to see tool calls and token usage.</p>
        } @else {
          <ul class="event-log">
            @for (entry of log(); track entry.id) {
              <li [class.error]="entry.isError" [class]="'entry-' + entry.kind">
                <div class="log-header">
                  <span class="log-kind">{{ entry.kind }}</span>
                  <span class="muted small">{{ entry.timestampUtc | date:'HH:mm:ss.SSS' }}</span>
                </div>
                <div class="log-title">{{ entry.title }}</div>
                @if (entry.detail) {
                  <pre class="log-detail">{{ entry.detail }}</pre>
                }
              </li>
            }
          </ul>
        }

        @if (finalOutput(); as output) {
          <header class="section-header section-header-spaced">
            <h2>Final output</h2>
            @if (finalDecision(); as decision) {
              <span
                class="tag"
                [class.ok]="decision === 'Completed' || decision === 'Approved'"
                [class.error]="decision === 'Failed' || decision === 'Rejected'">
                {{ decision }}
              </span>
            }
          </header>
          <pre class="card monospace">{{ output }}</pre>
        }
      </section>
    </div>
  `,
  styles: [`
    .muted { color: var(--muted); }
    .small { font-size: 0.8rem; }
    .test-grid {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(0, 1.2fr);
      gap: 2rem;
      align-items: start;
    }
    .test-column { display: flex; flex-direction: column; min-width: 0; }
    .section-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 0.75rem;
      gap: 0.5rem;
    }
    .section-header.section-header-spaced { margin-top: 1.5rem; }
    .section-header h2 { margin: 0; font-size: 1.1rem; }
    .variable-list { display: flex; flex-direction: column; gap: 0.35rem; }
    .variable-row {
      display: grid;
      grid-template-columns: minmax(120px, 0.5fr) 1fr auto;
      gap: 0.5rem;
      align-items: center;
    }
    .variable-name {
      font-size: 0.8rem;
      padding: 0.3rem 0.5rem;
      background: var(--surface-2);
      border-radius: 3px;
      color: var(--accent);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .variable-name.custom { color: var(--muted); }
    button.icon {
      padding: 0.2rem 0.5rem;
      font-size: 1rem;
      line-height: 1;
    }
    button.small { font-size: 0.8rem; padding: 0.3rem 0.6rem; }
    .event-log {
      list-style: none;
      padding: 0;
      margin: 0;
      display: flex;
      flex-direction: column;
      gap: 0.5rem;
      max-height: 600px;
      overflow-y: auto;
    }
    .event-log li {
      padding: 0.6rem 0.75rem;
      border-radius: 4px;
      border: 1px solid var(--border);
      background: var(--surface);
    }
    .event-log li.error {
      border-color: var(--sem-red);
      background: rgba(239, 68, 68, 0.05);
    }
    .event-log li.entry-tool-call-started,
    .event-log li.entry-tool-call-completed {
      border-left: 3px solid var(--accent);
    }
    .event-log li.entry-model-call-completed {
      border-left: 3px solid var(--sem-green);
    }
    .log-header {
      display: flex;
      justify-content: space-between;
      align-items: baseline;
      font-size: 0.75rem;
      text-transform: uppercase;
      letter-spacing: 0.03em;
    }
    .log-kind { color: var(--muted); font-weight: 600; }
    .log-title {
      font-size: 0.9rem;
      margin-top: 0.2rem;
      word-break: break-word;
    }
    .log-detail {
      margin: 0.4rem 0 0 0;
      padding: 0.4rem 0.5rem;
      background: var(--surface-2);
      border-radius: 3px;
      font-size: 0.8rem;
      white-space: pre-wrap;
      word-break: break-word;
      max-height: 200px;
      overflow: auto;
    }
    pre.card.monospace {
      white-space: pre-wrap;
      word-break: break-word;
      max-height: 400px;
      overflow: auto;
    }
    @media (max-width: 900px) {
      .test-grid { grid-template-columns: minmax(0, 1fr); }
    }
  `]
})
export class AgentTestComponent implements OnInit, OnDestroy {
  private readonly auth = inject(AuthService);
  private readonly agentsApi = inject(AgentsApi);

  readonly key = input.required<string>();

  readonly input = signal('');
  readonly agentVersion = signal<number | null>(null);

  readonly variables = signal<VariableEntry[]>([]);
  readonly customCount = signal(0);
  readonly configError = signal<string | null>(null);

  readonly running = signal(false);
  readonly log = signal<LogEntry[]>([]);
  readonly cumulativeUsage = signal<AgentTestTokenUsage | null>(null);
  readonly toolCallCount = signal(0);
  readonly finalOutput = signal<string | null>(null);
  readonly finalDecision = signal<string | null>(null);

  private nextEntryId = 0;
  private streamSub?: Subscription;

  ngOnInit(): void {
    this.loadConfig();
  }

  formatVar(name: string): string {
    return `{{${name}}}`;
  }

  ngOnDestroy(): void {
    this.streamSub?.unsubscribe();
  }

  onVersionChanged(): void {
    this.loadConfig();
  }

  private loadConfig(): void {
    this.configError.set(null);
    const version = this.agentVersion();
    const request$ = version !== null && Number.isFinite(version)
      ? this.agentsApi.getVersion(this.key(), version)
      : this.agentsApi.getLatest(this.key());

    request$.subscribe({
      next: agent => this.applyDetectedVariables(agent.config),
      error: err => this.configError.set(err?.message ?? 'Failed to load agent config')
    });
  }

  private applyDetectedVariables(config: AgentConfig | null): void {
    const detected = this.extractVariableNames(config);
    const existing = new Map(this.variables().map(v => [v.name, v.value]));
    const next: VariableEntry[] = [];

    for (const name of detected) {
      next.push({ name, value: existing.get(name) ?? '', detected: true });
    }

    for (const entry of this.variables()) {
      if (!entry.detected) {
        next.push({ ...entry });
      }
    }

    this.variables.set(next);
    this.customCount.set(next.filter(v => !v.detected).length);
  }

  private extractVariableNames(config: AgentConfig | null): string[] {
    if (!config) { return []; }
    const sources = [config.systemPrompt, config.promptTemplate].filter(
      (s): s is string => typeof s === 'string' && s.length > 0
    );
    const found = new Set<string>();
    for (const text of sources) {
      VARIABLE_PATTERN.lastIndex = 0;
      let match: RegExpExecArray | null;
      while ((match = VARIABLE_PATTERN.exec(text)) !== null) {
        const name = match[1];
        if (!RESERVED_VARIABLES.has(name.toLowerCase())) {
          found.add(name);
        }
      }
    }
    return Array.from(found);
  }

  setVariableValue(index: number, value: string): void {
    const current = this.variables();
    if (index < 0 || index >= current.length) { return; }
    const next = current.slice();
    next[index] = { ...next[index], value };
    this.variables.set(next);
  }

  addCustomVariable(): void {
    const name = window.prompt('Variable name');
    if (!name) { return; }
    const trimmed = name.trim();
    if (!trimmed) { return; }
    if (this.variables().some(v => v.name === trimmed)) { return; }
    this.variables.set([
      ...this.variables(),
      { name: trimmed, value: '', detected: false }
    ]);
    this.customCount.set(this.customCount() + 1);
  }

  removeVariable(index: number): void {
    const current = this.variables();
    if (index < 0 || index >= current.length) { return; }
    if (current[index].detected) { return; }
    const next = current.slice();
    next.splice(index, 1);
    this.variables.set(next);
    this.customCount.set(next.filter(v => !v.detected).length);
  }

  submit(event: Event): void {
    event.preventDefault();
    if (!this.input().trim()) { return; }
    this.start();
  }

  cancel(): void {
    this.streamSub?.unsubscribe();
    this.streamSub = undefined;
    this.running.set(false);
    this.appendLog({
      kind: 'error',
      title: 'Run cancelled',
      timestampUtc: new Date().toISOString(),
      isError: true
    });
  }

  clear(): void {
    this.log.set([]);
    this.cumulativeUsage.set(null);
    this.toolCallCount.set(0);
    this.finalOutput.set(null);
    this.finalDecision.set(null);
  }

  private buildVariablesPayload(): Record<string, string> | null {
    const payload: Record<string, string> = {};
    for (const entry of this.variables()) {
      if (entry.value.length > 0) {
        payload[entry.name] = entry.value;
      }
    }
    return Object.keys(payload).length > 0 ? payload : null;
  }

  private start(): void {
    this.clear();
    this.running.set(true);

    const token = this.auth.getAccessToken();
    this.streamSub = streamAgentTest(
      {
        agentKey: this.key(),
        agentVersion: this.agentVersion() ?? null,
        input: this.input(),
        variables: this.buildVariablesPayload()
      },
      token
    ).subscribe({
      next: evt => this.handleEvent(evt),
      error: err => {
        this.appendLog({
          kind: 'error',
          title: err?.message ?? 'Stream error',
          timestampUtc: new Date().toISOString(),
          isError: true
        });
        this.running.set(false);
      },
      complete: () => {
        this.running.set(false);
      }
    });
  }

  private handleEvent(evt: AgentTestEvent): void {
    switch (evt.type) {
      case 'started':
        this.appendLog({
          kind: 'started',
          title: `Started ${evt.agentKey} v${evt.agentVersion}`,
          detail: `provider: ${evt.provider}\nmodel: ${evt.model}`,
          timestampUtc: evt.timestampUtc
        });
        break;
      case 'model-call-started':
        this.appendLog({
          kind: 'model-call-started',
          title: `Model call #${evt.roundNumber}`,
          timestampUtc: evt.timestampUtc
        });
        break;
      case 'model-call-completed': {
        if (evt.cumulativeTokenUsage) {
          this.cumulativeUsage.set(evt.cumulativeTokenUsage);
        }
        const detailLines: string[] = [];
        if (evt.assistantText?.trim()) {
          detailLines.push(evt.assistantText.trim());
        }
        if (evt.callTokenUsage) {
          detailLines.push(
            `tokens — in: ${evt.callTokenUsage.inputTokens}, out: ${evt.callTokenUsage.outputTokens}, total: ${evt.callTokenUsage.totalTokens}`
          );
        }
        this.appendLog({
          kind: 'model-call-completed',
          title: `Model response (${evt.toolCallCount} tool call${evt.toolCallCount === 1 ? '' : 's'})`,
          detail: detailLines.join('\n') || undefined,
          timestampUtc: evt.timestampUtc
        });
        break;
      }
      case 'tool-call-started': {
        this.toolCallCount.set(this.toolCallCount() + 1);
        let argsText: string | undefined;
        if (evt.arguments !== undefined && evt.arguments !== null) {
          try {
            argsText = JSON.stringify(evt.arguments, null, 2);
          } catch {
            argsText = String(evt.arguments);
          }
        }
        this.appendLog({
          kind: 'tool-call-started',
          title: `→ ${evt.name}`,
          detail: argsText,
          timestampUtc: evt.timestampUtc
        });
        break;
      }
      case 'tool-call-completed': {
        let detail = evt.resultPreview ?? undefined;
        if (detail && evt.resultTruncated) {
          detail += '\n… (truncated)';
        }
        this.appendLog({
          kind: 'tool-call-completed',
          title: `← ${evt.name}${evt.isError ? ' (error)' : ''}`,
          detail,
          isError: evt.isError,
          timestampUtc: evt.timestampUtc
        });
        break;
      }
      case 'completed':
        this.finalOutput.set(evt.output);
        this.finalDecision.set(evt.decisionKind);
        if (evt.tokenUsage) {
          this.cumulativeUsage.set(evt.tokenUsage);
        }
        this.appendLog({
          kind: 'completed',
          title: `Completed — ${evt.decisionKind} (${evt.toolCallsExecuted} tool call${evt.toolCallsExecuted === 1 ? '' : 's'}, ${Math.round(evt.durationMs)}ms)`,
          timestampUtc: evt.timestampUtc
        });
        break;
      case 'error':
        this.appendLog({
          kind: 'error',
          title: evt.message,
          isError: true,
          timestampUtc: new Date().toISOString()
        });
        break;
    }
  }

  private appendLog(entry: Omit<LogEntry, 'id'>): void {
    this.log.set([...this.log(), { id: this.nextEntryId++, ...entry }]);
  }
}
