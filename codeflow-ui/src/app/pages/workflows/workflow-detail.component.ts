import { Component, OnInit, inject, input, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { WorkflowsApi } from '../../core/workflows.api';
import { WorkflowDetail, WorkflowNode } from '../../core/models';
import { WorkflowReadonlyCanvasComponent } from './editor/workflow-readonly-canvas.component';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent, ChipVariant } from '../../ui/chip.component';
import { CardComponent } from '../../ui/card.component';

const KIND_CHIP: Record<string, ChipVariant> = {
  Start: 'ok',
  Agent: 'accent',
  Logic: 'warn',
  Hitl: 'accent',
  Escalation: 'err',
  Subflow: 'accent',
  ReviewLoop: 'accent',
};

@Component({
  selector: 'cf-workflow-detail',
  standalone: true,
  imports: [
    CommonModule, RouterLink, DatePipe,
    WorkflowReadonlyCanvasComponent,
    PageHeaderComponent, ButtonComponent, ChipComponent, CardComponent,
  ],
  template: `
    <div class="page">
      @if (workflow(); as wf) {
        <cf-page-header [title]="wf.name">
          <button type="button" cf-button (click)="downloadPackage(wf)">Export</button>
          <a [routerLink]="['/workflows', key(), 'edit']">
            <button type="button" cf-button>Edit</button>
          </a>
          <a routerLink="/traces/new" [queryParams]="{ workflow: key() }">
            <button type="button" cf-button variant="primary">Submit run</button>
          </a>
          <div page-header-body>
            <div class="trace-header-meta">
              <cf-chip mono>{{ wf.key }}</cf-chip>
              <cf-chip mono>v{{ wf.version }}</cf-chip>
              <cf-chip mono>max rounds: {{ wf.maxRoundsPerRound }}</cf-chip>
              <cf-chip>{{ wf.nodes.length }} nodes</cf-chip>
              <cf-chip>{{ wf.edges.length }} edges</cf-chip>
              @if (wf.inputs.length > 0) {
                <cf-chip>{{ wf.inputs.length }} inputs</cf-chip>
              }
              <cf-chip>created {{ wf.createdAtUtc | date:'medium' }}</cf-chip>
            </div>
          </div>
        </cf-page-header>

        @if (exportError()) {
          <cf-card><cf-chip variant="err" dot>{{ exportError() }}</cf-chip></cf-card>
        }

        @if (wf.inputs.length > 0) {
          <cf-card title="Workflow inputs" flush>
            <table class="table">
              <thead><tr><th>Key</th><th>Display name</th><th>Kind</th><th>Required</th><th>Default</th></tr></thead>
              <tbody>
                @for (input of wf.inputs; track input.key) {
                  <tr>
                    <td class="mono" style="font-weight: 500">{{ input.key }}</td>
                    <td>{{ input.displayName }}</td>
                    <td><cf-chip mono>{{ input.kind }}</cf-chip></td>
                    <td>{{ input.required ? 'yes' : 'no' }}</td>
                    <td class="mono small muted">{{ input.defaultValueJson ?? '—' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </cf-card>
        }

        <cf-card title="Graph">
          @if (wf.nodes.length === 0) {
            <p class="muted">No nodes configured.</p>
          } @else {
            <div class="graph-host">
              <cf-workflow-readonly-canvas [workflow]="wf"></cf-workflow-readonly-canvas>
            </div>
          }
        </cf-card>

        <cf-card title="Nodes" flush>
          <table class="table">
            <thead><tr><th>Kind</th><th>Agent</th><th>Version</th><th>Output ports</th></tr></thead>
            <tbody>
              @for (node of wf.nodes; track node.id) {
                <tr>
                  <td><cf-chip [variant]="kindVariant(node.kind)" mono>{{ node.kind }}</cf-chip></td>
                  <td class="mono">{{ node.agentKey ?? '—' }}</td>
                  <td class="mono muted">{{ node.agentVersion ?? '—' }}</td>
                  <td class="mono small">{{ node.outputPorts.join(', ') || '—' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </cf-card>

        <cf-card title="Edges" flush>
          <table class="table">
            <thead><tr><th>From</th><th>Port</th><th>To</th><th>Rotates</th></tr></thead>
            <tbody>
              @for (edge of wf.edges; track $index) {
                <tr>
                  <td class="mono small">{{ labelForNode(edge.fromNodeId) }}</td>
                  <td><cf-chip variant="accent" mono>{{ edge.fromPort }}</cf-chip></td>
                  <td class="mono small">{{ labelForNode(edge.toNodeId) }}</td>
                  <td>{{ edge.rotatesRound ? 'yes' : 'no' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </cf-card>
      } @else {
        <cf-card><div class="muted">Loading workflow…</div></cf-card>
      }
    </div>
  `,
  styles: [`
    .graph-host { height: 500px; }
  `]
})
export class WorkflowDetailComponent implements OnInit {
  private readonly api = inject(WorkflowsApi);

  readonly key = input.required<string>();
  readonly workflow = signal<WorkflowDetail | null>(null);
  readonly exportError = signal<string | null>(null);

  ngOnInit(): void {
    this.api.getLatest(this.key()).subscribe({
      next: wf => this.workflow.set(wf)
    });
  }

  kindVariant(kind: string): ChipVariant {
    return KIND_CHIP[kind] ?? 'default';
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

  downloadPackage(workflow: WorkflowDetail): void {
    this.exportError.set(null);

    this.api.downloadPackage(workflow.key, workflow.version).subscribe({
      next: response => this.saveBlob(
        response.body,
        this.fileNameFromResponse(response.headers.get('content-disposition'))
          ?? `${workflow.key}-v${workflow.version}-package.json`),
      error: err => this.exportError.set(err?.message ?? 'Failed to export workflow package.')
    });
  }

  private saveBlob(blob: Blob | null, fileName: string): void {
    if (!blob) {
      return;
    }

    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
    URL.revokeObjectURL(url);
  }

  private fileNameFromResponse(disposition: string | null): string | null {
    if (!disposition) {
      return null;
    }

    const match = /filename\*?=(?:UTF-8''|")?([^";]+)/i.exec(disposition);
    return match ? decodeURIComponent(match[1]) : null;
  }
}
