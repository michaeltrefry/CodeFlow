import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { WorkflowsApi } from '../../core/workflows.api';
import { WorkflowDetail, WorkflowNode } from '../../core/models';

interface GraphNode {
  node: WorkflowNode;
  x: number;
  y: number;
  radius: number;
}

interface GraphEdge {
  fromX: number;
  fromY: number;
  toX: number;
  toY: number;
  label: string;
  rotatesRound: boolean;
}

@Component({
  selector: 'cf-workflow-detail',
  standalone: true,
  imports: [CommonModule, RouterLink, DatePipe],
  template: `
    <header class="page-header">
      <div>
        <h1>{{ workflow()?.name ?? key() }}</h1>
        @if (workflow(); as wf) {
          <p class="muted">{{ wf.key }} &middot; v{{ wf.version }} &middot; created {{ wf.createdAtUtc | date:'medium' }}</p>
        }
      </div>
      <div class="row">
        <a [routerLink]="['/workflows', key(), 'edit']"><button>Edit</button></a>
        <a routerLink="/traces/new" [queryParams]="{ workflow: key() }"><button class="secondary">Submit run</button></a>
      </div>
    </header>

    @if (workflow(); as wf) {
      <section class="card">
        <h3>Configuration</h3>
        <div class="row">
          <span class="tag">max rounds: {{ wf.maxRoundsPerRound }}</span>
          <span class="tag">{{ wf.nodes.length }} nodes</span>
          <span class="tag">{{ wf.edges.length }} edges</span>
          @if (wf.inputs.length > 0) {
            <span class="tag">{{ wf.inputs.length }} inputs</span>
          }
        </div>
      </section>

      @if (wf.inputs.length > 0) {
        <section class="card">
          <h3>Workflow inputs</h3>
          <table class="inputs">
            <thead><tr><th>Key</th><th>Display name</th><th>Kind</th><th>Required</th><th>Default</th></tr></thead>
            <tbody>
              @for (input of wf.inputs; track input.key) {
                <tr>
                  <td class="mono">{{ input.key }}</td>
                  <td>{{ input.displayName }}</td>
                  <td><span class="tag">{{ input.kind }}</span></td>
                  <td>{{ input.required ? 'yes' : 'no' }}</td>
                  <td class="mono small">{{ input.defaultValueJson ?? '—' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </section>
      }

      <section class="card">
        <h3>Graph</h3>
        @if (graphNodes().length === 0) {
          <p class="muted">No nodes configured.</p>
        } @else {
          <svg [attr.viewBox]="viewBox()" preserveAspectRatio="xMidYMid meet" class="graph-svg">
            <defs>
              <marker id="arrow" viewBox="0 0 10 10" refX="10" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
                <path d="M 0 0 L 10 5 L 0 10 z" fill="#58a6ff"/>
              </marker>
            </defs>
            @for (edge of graphEdges(); track $index) {
              <line
                [attr.x1]="edge.fromX"
                [attr.y1]="edge.fromY"
                [attr.x2]="edge.toX"
                [attr.y2]="edge.toY"
                stroke="#58a6ff"
                [attr.stroke-dasharray]="edge.rotatesRound ? '4 4' : null"
                stroke-width="1.5"
                marker-end="url(#arrow)"/>
              <text
                [attr.x]="(edge.fromX + edge.toX) / 2"
                [attr.y]="(edge.fromY + edge.toY) / 2 - 6"
                fill="#c9d1d9"
                font-size="10"
                text-anchor="middle">
                {{ edge.label }}
              </text>
            }
            @for (node of graphNodes(); track node.node.id) {
              <g>
                <circle
                  [attr.cx]="node.x"
                  [attr.cy]="node.y"
                  [attr.r]="node.radius"
                  [attr.fill]="fillFor(node.node.kind)"
                  stroke="#c9d1d9" stroke-width="1.5"/>
                <text
                  [attr.x]="node.x"
                  [attr.y]="node.y + 2"
                  text-anchor="middle"
                  fill="#0f172a"
                  font-weight="600"
                  font-size="10">
                  {{ labelFor(node.node) }}
                </text>
              </g>
            }
          </svg>
        }
      </section>

      <section class="card">
        <h3>Nodes</h3>
        <table class="nodes">
          <thead><tr><th>Kind</th><th>Agent</th><th>Version</th><th>Output ports</th></tr></thead>
          <tbody>
            @for (node of wf.nodes; track node.id) {
              <tr>
                <td><span class="tag" [class]="'kind-' + node.kind.toLowerCase()">{{ node.kind }}</span></td>
                <td>{{ node.agentKey ?? '—' }}</td>
                <td>{{ node.agentVersion ?? '—' }}</td>
                <td class="mono small">{{ node.outputPorts.join(', ') || '—' }}</td>
              </tr>
            }
          </tbody>
        </table>
      </section>

      <section class="card">
        <h3>Edges</h3>
        <table class="edges">
          <thead><tr><th>From</th><th>Port</th><th>To</th><th>Rotates</th></tr></thead>
          <tbody>
            @for (edge of wf.edges; track $index) {
              <tr>
                <td class="mono small">{{ labelForNode(edge.fromNodeId) }}</td>
                <td><span class="tag accent">{{ edge.fromPort }}</span></td>
                <td class="mono small">{{ labelForNode(edge.toNodeId) }}</td>
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
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 1.5rem; }
    .muted { color: var(--color-muted); }
    .graph-svg { width: 100%; height: 400px; background: var(--color-bg); border: 1px solid var(--color-border); border-radius: 6px; }
    table.edges, table.nodes, table.inputs { width: 100%; border-collapse: collapse; }
    table th, table td { padding: 0.4rem; border-bottom: 1px solid var(--color-border); text-align: left; vertical-align: top; }
    table th { color: var(--color-muted); text-transform: uppercase; font-size: 0.75rem; }
    .small { font-size: 0.8rem; }
    .mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
    .kind-start { background: rgba(63, 185, 80, 0.2); color: #3fb950; }
    .kind-agent { background: rgba(88, 166, 255, 0.2); color: #58a6ff; }
    .kind-logic { background: rgba(210, 153, 34, 0.2); color: #d29922; }
    .kind-hitl { background: rgba(188, 140, 255, 0.2); color: #bc8cff; }
    .kind-escalation { background: rgba(248, 81, 73, 0.2); color: #f85149; }
  `]
})
export class WorkflowDetailComponent implements OnInit {
  private readonly api = inject(WorkflowsApi);

  readonly key = input.required<string>();
  readonly workflow = signal<WorkflowDetail | null>(null);

  readonly graphNodes = computed<GraphNode[]>(() => {
    const wf = this.workflow();
    if (!wf || wf.nodes.length === 0) return [];
    const n = wf.nodes.length;
    const width = 800;
    const height = 400;
    const radius = Math.min(width, height) / 2.6;
    return wf.nodes.map((node, index) => {
      const angle = (index / n) * 2 * Math.PI - Math.PI / 2;
      return {
        node,
        x: width / 2 + radius * Math.cos(angle),
        y: height / 2 + radius * Math.sin(angle),
        radius: 40
      };
    });
  });

  readonly graphEdges = computed<GraphEdge[]>(() => {
    const wf = this.workflow();
    if (!wf) return [];
    const byId = new Map(this.graphNodes().map(g => [g.node.id, g]));
    return wf.edges.map(edge => {
      const from = byId.get(edge.fromNodeId);
      const to = byId.get(edge.toNodeId);
      if (!from || !to) return null;
      return {
        fromX: from.x,
        fromY: from.y,
        toX: to.x,
        toY: to.y,
        label: edge.fromPort,
        rotatesRound: edge.rotatesRound
      };
    }).filter((x): x is GraphEdge => x !== null);
  });

  readonly viewBox = signal('0 0 800 400');

  ngOnInit(): void {
    this.api.getLatest(this.key()).subscribe({
      next: wf => this.workflow.set(wf)
    });
  }

  labelFor(node: WorkflowNode): string {
    switch (node.kind) {
      case 'Start': return 'Start';
      case 'Agent': return node.agentKey ?? 'agent';
      case 'Logic': return 'Logic';
      case 'Hitl': return 'HITL';
      case 'Escalation': return 'Esc';
    }
  }

  labelForNode(nodeId: string): string {
    const wf = this.workflow();
    if (!wf) return nodeId;
    const node = wf.nodes.find(n => n.id === nodeId);
    return node ? this.labelFor(node) : nodeId;
  }

  fillFor(kind: WorkflowNode['kind']): string {
    switch (kind) {
      case 'Start': return '#3fb950';
      case 'Agent': return '#58a6ff';
      case 'Logic': return '#d29922';
      case 'Hitl': return '#bc8cff';
      case 'Escalation': return '#f85149';
    }
  }
}
