import { ChangeDetectionStrategy, ChangeDetectorRef, Component, HostBinding, Input, OnChanges } from '@angular/core';
import { CommonModule, KeyValue } from '@angular/common';
import { RefDirective, ImpureKeyvaluePipe } from 'rete-angular-plugin/20';
import { ClassicPreset } from 'rete';
import { WorkflowEditorNode, WorkflowNodeTokenOverlay } from './workflow-node-schemes';

type NodeExtraData = { width?: number; height?: number };
type SortValue = (ClassicPreset.Node['controls'] | ClassicPreset.Node['inputs'] | ClassicPreset.Node['outputs'])[string];

@Component({
  selector: 'cf-workflow-node',
  standalone: true,
  imports: [CommonModule, RefDirective, ImpureKeyvaluePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { 'data-testid': 'node' },
  template: `
    <div class="wf-node"
         [attr.data-kind]="kindLower"
         [attr.data-selected]="selected ? 'true' : null"
         [attr.data-state]="data.traceState === 'active' ? 'active' : (data.traceState === 'dimmed' ? 'dimmed' : null)">
      <div class="wf-node-head">
        <span class="wf-node-kind">{{ kindLabel }}</span>
        <span class="wf-node-title" data-testid="title">{{ data.label }}</span>
        <span *ngIf="isFork"
              class="wf-node-fork-badge"
              title="Workflow-scoped fork"
              data-testid="node-fork-badge">fork</span>
        <span *ngIf="hasScript"
              class="wf-node-script-badge"
              title="Routing script attached"
              data-testid="node-script-badge">{{ '{ }' }}</span>
        <span *ngIf="data.tokenUsageOverlay as tu"
              class="wf-node-token-badge"
              [attr.data-rolled-up]="tu.rolledUp ? 'true' : null"
              [title]="tokenOverlayHover(tu)"
              data-testid="node-token-badge">
          ↑{{ formatCount(tu.inputTokens) }} ↓{{ formatCount(tu.outputTokens) }}<span class="wf-node-token-calls" *ngIf="tu.callCount > 1">·{{ tu.callCount }}</span>
        </span>
      </div>

      <div class="wf-node-body">
        <div class="port-cols">
          <div class="port-col">
            <div class="wf-node-row input"
                 *ngFor="let input of data.inputs | keyvalueimpure: sortByIndex"
                 [attr.data-testid]="'input-' + input.key">
              <div class="wf-port-wrap"
                   refComponent
                   [data]="{ type: 'socket', side: 'input', key: input.key, nodeId: data.id, payload: input.value?.socket, seed: seed }"
                   [emit]="emit"
                   data-testid="input-socket"></div>
              <span class="wf-port-label" *ngIf="!input.value?.control || !input.value?.showControl">{{ input.value?.label }}</span>
            </div>
          </div>

          <div class="port-col outputs">
            <div class="wf-node-row output"
                 *ngFor="let output of data.outputs | keyvalueimpure: sortByIndex"
                 [class.implicit-failed]="output.key === 'Failed'"
                 [class.wired]="output.key === 'Failed' && data.failedHasConnection"
                 [attr.data-testid]="'output-' + output.key">
              <span class="wf-port-label output">{{ output.value?.label }}</span>
              <div class="wf-port-wrap"
                   refComponent
                   [data]="{ type: 'socket', side: 'output', key: output.key, nodeId: data.id, payload: output.value?.socket, seed: seed }"
                   [emit]="emit"
                   data-testid="output-socket"></div>
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host {
      display: block;
      user-select: none;
      line-height: initial;
      font-family: var(--font-sans);
    }
    .wf-node {
      position: relative;
    }
    .port-cols {
      display: grid;
      grid-template-columns: 1fr 1fr;
    }
    .port-col { display: flex; flex-direction: column; padding: 2px 0; }
    .port-col.outputs .wf-node-row { justify-content: flex-end; }
    .wf-port-wrap {
      display: inline-flex;
      align-items: center;
    }
    .wf-node-row.input .wf-port-wrap { margin-left: -7px; }
    .wf-node-row.output .wf-port-wrap { margin-right: -7px; }
    .port-col.outputs:empty, .port-col:empty { min-height: 24px; }
    /* Implicit Failed port: italic + danger when unwired-and-revealed; normal when wired. */
    .wf-node-row.output.implicit-failed:not(.wired) .wf-port-label {
      color: var(--color-danger, #d04848);
      opacity: 0.55;
      font-style: italic;
    }
    .wf-node-row.output.implicit-failed:not(.wired) .wf-port-wrap {
      opacity: 0.55;
    }
    .wf-node-row.output.implicit-failed.wired .wf-port-label {
      color: var(--color-danger, #d04848);
    }
    /* Token Usage Tracking [Slice 7] - compact per-node badge in the node head.
       Direct per-node totals get the accent variant; rolled-up totals (from a
       descendant child saga of a Subflow / ReviewLoop / Swarm) get a muted dashed
       treatment so the two are distinguishable at a glance. */
    .wf-node-token-badge {
      margin-left: auto;
      padding: 1px 6px;
      border-radius: var(--radius-sm, 4px);
      background: color-mix(in oklab, var(--accent, #4a8fdb) 18%, transparent);
      color: var(--accent, #4a8fdb);
      font-family: var(--font-mono);
      font-size: var(--fs-xs, 11px);
      font-weight: 600;
      letter-spacing: 0.01em;
      white-space: nowrap;
      display: inline-flex;
      align-items: center;
      gap: 2px;
    }
    .wf-node-token-badge[data-rolled-up="true"] {
      background: color-mix(in oklab, var(--muted, #a0a0a0) 22%, transparent);
      color: var(--muted, #a0a0a0);
      border: 1px dashed color-mix(in oklab, var(--muted, #a0a0a0) 40%, transparent);
      padding: 0 5px;
    }
    .wf-node-token-calls { opacity: 0.75; margin-left: 2px; }
  `]
})
export class WorkflowNodeComponent implements OnChanges {
  @Input() data!: WorkflowEditorNode & NodeExtraData;
  @Input() emit!: (data: unknown) => void;
  @Input() rendered!: () => void;

  seed = 0;

  @HostBinding('style.width.px') get width() { return this.data?.width ?? 220; }
  @HostBinding('style.height.px') get height() { return this.data?.height; }
  get selected(): boolean | undefined {
    return (this.data as ClassicPreset.Node & { selected?: boolean }).selected;
  }

  get kindLower(): string {
    return this.data.kind.toLowerCase();
  }

  get kindLabel(): string {
    return this.data.kind === 'Hitl' ? 'HITL' : this.data.kind;
  }

  get hasScript(): boolean {
    const out = this.data.outputScript;
    const inp = this.data.inputScript;
    return (typeof out === 'string' && out.trim().length > 0)
      || (typeof inp === 'string' && inp.trim().length > 0);
  }

  get isFork(): boolean {
    return typeof this.data.agentKey === 'string' && this.data.agentKey.startsWith('__fork_');
  }

  constructor(private readonly cdr: ChangeDetectorRef) {
    this.cdr.detach();
  }

  ngOnChanges(): void {
    this.cdr.detectChanges();
    requestAnimationFrame(() => this.rendered?.());
    this.seed++;
  }

  sortByIndex(a: KeyValue<string, SortValue>, b: KeyValue<string, SortValue>): number {
    const ai = (a.value as { index?: number } | undefined)?.index ?? 0;
    const bi = (b.value as { index?: number } | undefined)?.index ?? 0;
    return ai - bi;
  }

  /** Compact integer formatter for the in-graph token badge - small counts
   *  exact, larger counts truncated to k/M so the badge stays readable inside
   *  a node head. Mirrors the shared timeline's formatter. */
  formatCount(value: number): string {
    if (!Number.isFinite(value) || value === 0) return '0';
    const abs = Math.abs(value);
    if (abs < 1000) return String(value);
    if (abs < 1_000_000) return (value / 1000).toFixed(value >= 10_000 ? 0 : 1) + 'k';
    return (value / 1_000_000).toFixed(1) + 'M';
  }

  tokenOverlayHover(tu: WorkflowNodeTokenOverlay): string {
    const callsLabel = tu.callCount + ' ' + (tu.callCount === 1 ? 'call' : 'calls');
    const provenance = tu.rolledUp ? 'descendant scope total' : 'direct on this node';
    return tu.inputTokens.toLocaleString() + ' input · '
      + tu.outputTokens.toLocaleString() + ' output · '
      + callsLabel + ' (' + provenance + ')';
  }
}
