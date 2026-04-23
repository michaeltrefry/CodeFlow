import { Component, OnInit, inject, input, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { WorkflowsApi } from '../../core/workflows.api';
import { WorkflowDetail, WorkflowNode } from '../../core/models';
import { WorkflowReadonlyCanvasComponent } from './editor/workflow-readonly-canvas.component';

@Component({
  selector: 'cf-workflow-detail',
  standalone: true,
  imports: [CommonModule, RouterLink, DatePipe, WorkflowReadonlyCanvasComponent],
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
        @if (wf.nodes.length === 0) {
          <p class="muted">No nodes configured.</p>
        } @else {
          <div class="graph-host">
            <cf-workflow-readonly-canvas [workflow]="wf"></cf-workflow-readonly-canvas>
          </div>
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
    .graph-host { height: 500px; }
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
    .kind-subflow { background: rgba(46, 163, 242, 0.2); color: #2ea3f2; }
  `]
})
export class WorkflowDetailComponent implements OnInit {
  private readonly api = inject(WorkflowsApi);

  readonly key = input.required<string>();
  readonly workflow = signal<WorkflowDetail | null>(null);

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
      case 'Subflow': return `Subflow → ${node.subflowKey ?? '?'}`;
      case 'ReviewLoop': return `ReviewLoop ×${node.reviewMaxRounds ?? '?'} → ${node.subflowKey ?? '?'}`;
    }
  }

  labelForNode(nodeId: string): string {
    const wf = this.workflow();
    if (!wf) return nodeId;
    const node = wf.nodes.find(n => n.id === nodeId);
    return node ? this.labelFor(node) : nodeId;
  }
}
