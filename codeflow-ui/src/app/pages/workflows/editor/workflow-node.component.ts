import { ChangeDetectionStrategy, ChangeDetectorRef, Component, HostBinding, Input, OnChanges } from '@angular/core';
import { CommonModule, KeyValue } from '@angular/common';
import { RefDirective, ImpureKeyvaluePipe } from 'rete-angular-plugin/20';
import { ClassicPreset } from 'rete';
import { WorkflowEditorNode } from './workflow-node-schemes';

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
        <span *ngIf="hasScript"
              class="wf-node-script-badge"
              title="Routing script attached"
              data-testid="node-script-badge">{{ '{ }' }}</span>
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
    const script = this.data.script;
    return typeof script === 'string' && script.trim().length > 0;
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
}
