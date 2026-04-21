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
    <div class="node"
         [class.selected]="selected"
         [class.trace-active]="data.traceState === 'active'"
         [class.trace-dimmed]="data.traceState === 'dimmed'"
         [attr.data-kind]="kindLower">
      <div class="header">
        <span class="kind-badge">{{ kindLabel }}</span>
        <span class="title" data-testid="title">{{ data.label }}</span>
      </div>

      <div class="ports">
        <div class="inputs">
          <div class="input"
               *ngFor="let input of data.inputs | keyvalueimpure: sortByIndex"
               [attr.data-testid]="'input-' + input.key">
            <div class="input-socket"
                 refComponent
                 [data]="{ type: 'socket', side: 'input', key: input.key, nodeId: data.id, payload: input.value?.socket, seed: seed }"
                 [emit]="emit"
                 data-testid="input-socket"></div>
            <span class="input-title" *ngIf="!input.value?.control || !input.value?.showControl">{{ input.value?.label }}</span>
          </div>
        </div>

        <div class="outputs">
          <div class="output"
               *ngFor="let output of data.outputs | keyvalueimpure: sortByIndex"
               [attr.data-testid]="'output-' + output.key">
            <span class="output-title">{{ output.value?.label }}</span>
            <div class="output-socket"
                 refComponent
                 [data]="{ type: 'socket', side: 'output', key: output.key, nodeId: data.id, payload: output.value?.socket, seed: seed }"
                 [emit]="emit"
                 data-testid="output-socket"></div>
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
      font-family: inherit;
    }
    .node {
      background: #1b2130;
      border: 2px solid #2d3748;
      border-radius: 8px;
      color: #e5e9f0;
      min-width: 200px;
      box-sizing: border-box;
      overflow: hidden;
      box-shadow: 0 2px 6px rgba(0, 0, 0, 0.25);
      cursor: pointer;
    }
    .node.selected {
      border-color: #fbbf24;
      box-shadow: 0 0 0 2px rgba(251, 191, 36, 0.35), 0 2px 6px rgba(0, 0, 0, 0.25);
    }
    .node.trace-active {
      box-shadow: 0 0 0 3px rgba(88, 166, 255, 0.45), 0 2px 10px rgba(0, 0, 0, 0.35);
    }
    .node.trace-dimmed {
      opacity: 0.35;
    }

    .header {
      padding: 0.45rem 0.75rem;
      display: flex;
      align-items: center;
      gap: 0.5rem;
      font-weight: 600;
      color: #f8f8f2;
    }
    .kind-badge {
      font-size: 0.65rem;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      padding: 0.1rem 0.4rem;
      border-radius: 3px;
      background: rgba(255, 255, 255, 0.12);
    }
    .title {
      flex: 1;
      overflow: hidden;
      white-space: nowrap;
      text-overflow: ellipsis;
      font-size: 0.9rem;
    }

    /* kind-specific header colors */
    .node[data-kind='start'] { border-left: 6px solid #3fb950; }
    .node[data-kind='start'] .header { background: linear-gradient(90deg, rgba(63,185,80,0.35), rgba(63,185,80,0.08)); }
    .node[data-kind='agent'] { border-left: 6px solid #58a6ff; }
    .node[data-kind='agent'] .header { background: linear-gradient(90deg, rgba(88,166,255,0.35), rgba(88,166,255,0.08)); }
    .node[data-kind='logic'] { border-left: 6px solid #d29922; }
    .node[data-kind='logic'] .header { background: linear-gradient(90deg, rgba(210,153,34,0.35), rgba(210,153,34,0.08)); }
    .node[data-kind='hitl'] { border-left: 6px solid #bc8cff; }
    .node[data-kind='hitl'] .header { background: linear-gradient(90deg, rgba(188,140,255,0.35), rgba(188,140,255,0.08)); }
    .node[data-kind='escalation'] { border-left: 6px solid #f85149; }
    .node[data-kind='escalation'] .header { background: linear-gradient(90deg, rgba(248,81,73,0.35), rgba(248,81,73,0.08)); }

    .ports {
      display: grid;
      grid-template-columns: 1fr 1fr;
      padding: 0.25rem 0;
    }
    .inputs, .outputs {
      display: flex;
      flex-direction: column;
      gap: 0.35rem;
      padding: 0.35rem 0;
    }
    .input { display: flex; align-items: center; gap: 0.4rem; }
    .output { display: flex; align-items: center; justify-content: flex-end; gap: 0.4rem; }

    .input-socket {
      margin-left: -10px;
      display: inline-block;
    }
    .output-socket {
      margin-right: -10px;
      display: inline-block;
    }

    .input-title, .output-title {
      font-size: 0.78rem;
      color: #e5e9f0;
      padding: 0.15rem 0.5rem;
    }
    .output-title { text-align: right; }
    .input-title { text-align: left; }

    /* ensure empty column doesn't collapse edge placement */
    .outputs:empty, .inputs:empty { min-height: 24px; }
  `]
})
export class WorkflowNodeComponent implements OnChanges {
  @Input() data!: WorkflowEditorNode & NodeExtraData;
  @Input() emit!: (data: unknown) => void;
  @Input() rendered!: () => void;

  seed = 0;

  @HostBinding('style.width.px') get width() { return this.data?.width; }
  @HostBinding('style.height.px') get height() { return this.data?.height; }
  @HostBinding('class.selected') get selected() { return (this.data as ClassicPreset.Node & { selected?: boolean }).selected; }

  get kindLower(): string {
    return this.data.kind.toLowerCase();
  }

  get kindLabel(): string {
    return this.data.kind === 'Hitl' ? 'HITL' : this.data.kind;
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
