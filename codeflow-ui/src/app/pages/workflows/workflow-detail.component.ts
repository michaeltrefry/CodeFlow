import { Component, inject, input, signal, OnInit, computed } from '@angular/core';
import { DatePipe, JsonPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { WorkflowsApi } from '../../core/workflows.api';
import { WorkflowDetail } from '../../core/models';

interface GraphNode {
  key: string;
  x: number;
  y: number;
  isStart: boolean;
  isEscalation: boolean;
}

interface GraphEdge {
  from: GraphNode;
  to: GraphNode;
  label: string;
  rotatesRound: boolean;
}

@Component({
  selector: 'cf-workflow-detail',
  standalone: true,
  imports: [RouterLink, DatePipe, JsonPipe],
  template: `
    <header class="page-header">
      <div>
        <h1>{{ workflow()?.name ?? key() }}</h1>
        @if (workflow(); as wf) {
          <p class="muted">{{ wf.key }} &middot; v{{ wf.version }} &middot; created {{ wf.createdAtUtc | date:'medium' }}</p>
        }
      </div>
      <div class="row">
        <a routerLink="/workflows/new" [queryParams]="{ key: key() }"><button>New version</button></a>
        <a routerLink="/traces/new" [queryParams]="{ workflow: key() }"><button class="secondary">Submit run</button></a>
      </div>
    </header>

    @if (workflow(); as wf) {
      <section class="card">
        <h3>Configuration</h3>
        <div class="row"><span class="tag">start: {{ wf.startAgentKey }}</span>
        @if (wf.escalationAgentKey) { <span class="tag warn">escalation: {{ wf.escalationAgentKey }}</span> }
        <span class="tag">max rounds: {{ wf.maxRoundsPerRound }}</span></div>
      </section>

      <section class="card">
        <h3>Graph</h3>
        @if (graphNodes().length === 0) {
          <p class="muted">No nodes to render.</p>
        } @else {
          <svg [attr.viewBox]="viewBox()" preserveAspectRatio="xMidYMid meet" class="graph-svg">
            <defs>
              <marker id="arrow" viewBox="0 0 10 10" refX="10" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
                <path d="M 0 0 L 10 5 L 0 10 z" fill="#38bdf8"/>
              </marker>
            </defs>
            @for (edge of graphEdges(); track edge.label) {
              <line
                [attr.x1]="edge.from.x"
                [attr.y1]="edge.from.y"
                [attr.x2]="edge.to.x"
                [attr.y2]="edge.to.y"
                [class.rotates]="edge.rotatesRound"
                stroke="#38bdf8"
                stroke-width="1.5"
                marker-end="url(#arrow)"/>
              <text
                [attr.x]="(edge.from.x + edge.to.x) / 2"
                [attr.y]="(edge.from.y + edge.to.y) / 2 - 6"
                fill="#e2e8f0"
                font-size="11"
                text-anchor="middle">
                {{ edge.label }}
              </text>
            }
            @for (node of graphNodes(); track node.key) {
              <g>
                <circle
                  [attr.cx]="node.x"
                  [attr.cy]="node.y"
                  r="34"
                  [attr.fill]="node.isStart ? '#0ea5e9' : node.isEscalation ? '#f59e0b' : '#334155'"
                  stroke="#e2e8f0" stroke-width="1.5"/>
                <text
                  [attr.x]="node.x"
                  [attr.y]="node.y + 4"
                  text-anchor="middle"
                  fill="#0f172a"
                  font-weight="600"
                  font-size="11">
                  {{ node.key }}
                </text>
              </g>
            }
          </svg>
        }
      </section>

      <section class="card">
        <h3>Edges</h3>
        <table class="edges">
          <thead>
            <tr><th>From</th><th>Decision</th><th>Discriminator</th><th>To</th><th>Rotates</th></tr>
          </thead>
          <tbody>
            @for (edge of wf.edges; track $index) {
              <tr>
                <td>{{ edge.fromAgentKey }}</td>
                <td><span class="tag accent">{{ edge.decision }}</span></td>
                <td class="monospace small">{{ edge.discriminator ? (edge.discriminator | json) : '—' }}</td>
                <td>{{ edge.toAgentKey }}</td>
                <td>{{ edge.rotatesRound ? 'yes' : 'no' }}</td>
              </tr>
            }
          </tbody>
        </table>
      </section>
    } @else {
      <p>Loading workflow&hellip;</p>
    }
  `,
  styles: [`
    .page-header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      margin-bottom: 1.5rem;
    }
    .muted { color: var(--color-muted); }
    .graph-svg {
      width: 100%;
      height: 360px;
      background: var(--color-bg);
      border: 1px solid var(--color-border);
      border-radius: 6px;
    }
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
    }
    .small { font-size: 0.8rem; }
  `]
})
export class WorkflowDetailComponent implements OnInit {
  private readonly api = inject(WorkflowsApi);

  readonly key = input.required<string>();
  readonly workflow = signal<WorkflowDetail | null>(null);

  readonly graphNodes = computed<GraphNode[]>(() => {
    const wf = this.workflow();
    if (!wf) { return []; }
    const keys = new Set<string>();
    keys.add(wf.startAgentKey);
    if (wf.escalationAgentKey) { keys.add(wf.escalationAgentKey); }
    for (const edge of wf.edges) {
      keys.add(edge.fromAgentKey);
      keys.add(edge.toAgentKey);
    }
    const arr = Array.from(keys);
    const n = Math.max(arr.length, 1);
    const width = 800;
    const height = 360;
    const radius = Math.min(width, height) / 2.8;
    return arr.map((key, index) => {
      const angle = (index / n) * 2 * Math.PI - Math.PI / 2;
      return {
        key,
        x: width / 2 + radius * Math.cos(angle),
        y: height / 2 + radius * Math.sin(angle),
        isStart: wf.startAgentKey === key,
        isEscalation: wf.escalationAgentKey === key
      };
    });
  });

  readonly graphEdges = computed<GraphEdge[]>(() => {
    const wf = this.workflow();
    if (!wf) { return []; }
    const byKey = new Map(this.graphNodes().map(n => [n.key, n] as const));
    return wf.edges.map(edge => ({
      from: byKey.get(edge.fromAgentKey)!,
      to: byKey.get(edge.toAgentKey)!,
      label: edge.decision,
      rotatesRound: edge.rotatesRound
    })).filter(edge => edge.from && edge.to);
  });

  readonly viewBox = signal('0 0 800 360');

  ngOnInit(): void {
    this.api.getLatest(this.key()).subscribe({
      next: wf => this.workflow.set(wf)
    });
  }
}
