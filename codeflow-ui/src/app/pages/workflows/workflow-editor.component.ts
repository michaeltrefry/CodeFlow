import { Component, inject, input, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
import { WorkflowsApi } from '../../core/workflows.api';
import { AgentSummary, AgentDecisionKind, WorkflowEdge } from '../../core/models';

interface EditorEdge {
  fromAgentKey: string;
  toAgentKey: string;
  decision: AgentDecisionKind;
  discriminator?: string;
  rotatesRound: boolean;
}

const DECISIONS: AgentDecisionKind[] = ['Completed', 'Approved', 'ApprovedWithActions', 'Rejected', 'Failed'];

@Component({
  selector: 'cf-workflow-editor',
  standalone: true,
  imports: [FormsModule, RouterLink],
  template: `
    <header class="page-header">
      <div>
        <h1>{{ existingKey() ? 'New version of ' + existingKey() : 'New workflow' }}</h1>
        <p class="muted">Saving creates an immutable new version. Edges are pattern-matched on (from, decision, discriminator?).</p>
      </div>
      <a routerLink="/workflows"><button class="secondary">Cancel</button></a>
    </header>

    <form (submit)="submit($event)">
      <div class="grid-two">
        <div class="form-field">
          @if (!existingKey()) {
            <label>Workflow key</label>
            <input [(ngModel)]="key" name="key" placeholder="review-flow" required />
          } @else {
            <label>Workflow key</label>
            <input [value]="existingKey()" disabled />
          }
        </div>
        <div class="form-field">
          <label>Name</label>
          <input [(ngModel)]="name" name="name" placeholder="Article review flow" required />
        </div>
      </div>

      <div class="grid-two">
        <div class="form-field">
          <label>Start agent</label>
          <select [(ngModel)]="startAgentKey" name="startAgentKey" required>
            @for (agent of agents(); track agent.key) {
              <option [value]="agent.key">{{ agent.key }} (v{{ agent.latestVersion }})</option>
            }
          </select>
        </div>
        <div class="form-field">
          <label>Escalation agent (optional)</label>
          <select [(ngModel)]="escalationAgentKey" name="escalationAgentKey">
            <option [ngValue]="null">&mdash;</option>
            @for (agent of agents(); track agent.key) {
              <option [value]="agent.key">{{ agent.key }}</option>
            }
          </select>
        </div>
      </div>

      <div class="form-field">
        <label>Max rounds per round</label>
        <input type="number" [(ngModel)]="maxRoundsPerRound" name="maxRoundsPerRound" min="1" max="50" required />
      </div>

      <div class="card">
        <h3 class="row" style="justify-content: space-between;">
          <span>Edges</span>
          <button type="button" class="secondary" (click)="addEdge()">Add edge</button>
        </h3>

        @if (edges().length === 0) {
          <p class="muted">No edges yet. Add at least one to route agent decisions.</p>
        } @else {
          <table class="edges">
            <thead>
              <tr>
                <th>From</th>
                <th>Decision</th>
                <th>Discriminator (JSON)</th>
                <th>To</th>
                <th>Rotates round</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (edge of edges(); track $index) {
                <tr>
                  <td>
                    <select [(ngModel)]="edge.fromAgentKey" [name]="'from-' + $index">
                      @for (agent of agents(); track agent.key) {
                        <option [value]="agent.key">{{ agent.key }}</option>
                      }
                    </select>
                  </td>
                  <td>
                    <select [(ngModel)]="edge.decision" [name]="'decision-' + $index">
                      @for (decision of decisions; track decision) {
                        <option [value]="decision">{{ decision }}</option>
                      }
                    </select>
                  </td>
                  <td>
                    <input [(ngModel)]="edge.discriminator" [name]="'disc-' + $index" placeholder='{"severity":"high"}' />
                  </td>
                  <td>
                    <select [(ngModel)]="edge.toAgentKey" [name]="'to-' + $index">
                      @for (agent of agents(); track agent.key) {
                        <option [value]="agent.key">{{ agent.key }}</option>
                      }
                    </select>
                  </td>
                  <td style="text-align:center;">
                    <input type="checkbox" [(ngModel)]="edge.rotatesRound" [name]="'rotates-' + $index" />
                  </td>
                  <td>
                    <button type="button" class="danger" (click)="removeEdge($index)">Remove</button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>

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
    .muted { color: var(--color-muted); }
    table.edges {
      width: 100%;
      border-collapse: collapse;
    }
    table.edges th, table.edges td {
      padding: 0.4rem;
      border-bottom: 1px solid var(--color-border);
      text-align: left;
    }
    table.edges th {
      color: var(--color-muted);
      text-transform: uppercase;
      font-size: 0.75rem;
      letter-spacing: 0.05em;
    }
  `]
})
export class WorkflowEditorComponent implements OnInit {
  private readonly agentsApi = inject(AgentsApi);
  private readonly api = inject(WorkflowsApi);
  private readonly router = inject(Router);

  readonly existingKey = input<string | undefined>(undefined, { alias: 'key' });

  readonly key = signal('');
  readonly name = signal('');
  readonly startAgentKey = signal('');
  readonly escalationAgentKey = signal<string | null>(null);
  readonly maxRoundsPerRound = signal(3);
  readonly edges = signal<EditorEdge[]>([]);

  readonly agents = signal<AgentSummary[]>([]);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly decisions = DECISIONS;

  ngOnInit(): void {
    this.agentsApi.list().subscribe({
      next: agents => {
        this.agents.set(agents);
        if (!this.existingKey() && agents.length && !this.startAgentKey()) {
          this.startAgentKey.set(agents[0].key);
        }
      }
    });

    const existing = this.existingKey();
    if (existing) {
      this.api.getLatest(existing).subscribe({
        next: wf => {
          this.key.set(wf.key);
          this.name.set(wf.name);
          this.startAgentKey.set(wf.startAgentKey);
          this.escalationAgentKey.set(wf.escalationAgentKey ?? null);
          this.maxRoundsPerRound.set(wf.maxRoundsPerRound);
          this.edges.set(wf.edges.map(e => ({
            fromAgentKey: e.fromAgentKey,
            toAgentKey: e.toAgentKey,
            decision: e.decision,
            discriminator: e.discriminator ? JSON.stringify(e.discriminator) : undefined,
            rotatesRound: e.rotatesRound
          })));
        }
      });
    }
  }

  addEdge(): void {
    const fallback = this.agents()[0]?.key ?? '';
    this.edges.set([
      ...this.edges(),
      {
        fromAgentKey: this.startAgentKey() || fallback,
        toAgentKey: fallback,
        decision: 'Completed',
        rotatesRound: false
      }
    ]);
  }

  removeEdge(index: number): void {
    this.edges.set(this.edges().filter((_, i) => i !== index));
  }

  submit(event: Event): void {
    event.preventDefault();
    this.saving.set(true);
    this.error.set(null);

    const edges: WorkflowEdge[] = this.edges().map((edge, index) => ({
      fromAgentKey: edge.fromAgentKey,
      toAgentKey: edge.toAgentKey,
      decision: edge.decision,
      discriminator: this.parseDiscriminator(edge.discriminator),
      rotatesRound: edge.rotatesRound,
      sortOrder: index
    }));

    const payload = {
      key: this.existingKey() ?? this.key(),
      name: this.name(),
      startAgentKey: this.startAgentKey(),
      escalationAgentKey: this.escalationAgentKey(),
      maxRoundsPerRound: this.maxRoundsPerRound(),
      edges
    };

    const save$ = this.existingKey()
      ? this.api.addVersion(this.existingKey()!, payload)
      : this.api.create(payload);

    save$.subscribe({
      next: result => {
        this.saving.set(false);
        this.router.navigate(['/workflows', result.key]);
      },
      error: err => {
        this.saving.set(false);
        this.error.set(typeof err?.error === 'object' ? JSON.stringify(err.error) : err?.message ?? 'Save failed');
      }
    });
  }

  private parseDiscriminator(text?: string): unknown {
    if (!text || !text.trim()) {
      return undefined;
    }
    try {
      return JSON.parse(text);
    } catch {
      return undefined;
    }
  }
}
