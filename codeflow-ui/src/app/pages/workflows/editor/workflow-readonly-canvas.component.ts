import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  OnChanges,
  OnDestroy,
  SimpleChanges,
  ViewChild,
  inject,
  input
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { NodeEditor } from 'rete';
import { AreaExtensions, AreaPlugin } from 'rete-area-plugin';
import { AngularPlugin, Presets as AngularPresets } from 'rete-angular-plugin/20';
import { WorkflowDetail } from '../../../core/models';
import {
  WorkflowAreaExtra,
  WorkflowEditorNode,
  WorkflowSchemes
} from './workflow-node-schemes';
import { loadIntoEditor, workflowDetailToModel } from './workflow-serialization';
import { tidyLayout } from './auto-layout';
import { WorkflowNodeComponent } from './workflow-node.component';

@Component({
  selector: 'cf-workflow-readonly-canvas',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="readonly-wrapper">
      <div #canvasHost class="readonly-canvas"></div>
    </div>
  `,
  styles: [`
    :host { display: block; height: 100%; }
    .readonly-wrapper { height: 100%; position: relative; }
    .readonly-canvas {
      width: 100%;
      height: 100%;
      background: var(--color-surface-2, #0d1117);
      border: 1px solid var(--color-border);
      border-radius: 6px;
      overflow: hidden;
    }
  `]
})
export class WorkflowReadonlyCanvasComponent implements AfterViewInit, OnChanges, OnDestroy {
  private readonly injector = inject(Injector);

  @ViewChild('canvasHost', { static: true }) canvasHost!: ElementRef<HTMLDivElement>;

  readonly workflow = input<WorkflowDetail | null>(null);
  /** When non-null, highlights the listed node ids and dims every other node. */
  readonly highlightedNodeIds = input<string[] | null>(null);

  private editor?: NodeEditor<WorkflowSchemes>;
  private area?: AreaPlugin<WorkflowSchemes, WorkflowAreaExtra>;
  private initialized = false;
  private loading = false;

  async ngAfterViewInit(): Promise<void> {
    await this.initialize();
    await this.loadCurrent();
    this.applyHighlight();
  }

  async ngOnChanges(changes: SimpleChanges): Promise<void> {
    if (!this.initialized) return;
    if (changes['workflow']) {
      await this.loadCurrent();
      this.applyHighlight();
    } else if (changes['highlightedNodeIds']) {
      this.applyHighlight();
    }
  }

  ngOnDestroy(): void {
    this.area?.destroy();
  }

  private async initialize(): Promise<void> {
    const editor = new NodeEditor<WorkflowSchemes>();
    const area = new AreaPlugin<WorkflowSchemes, WorkflowAreaExtra>(this.canvasHost.nativeElement);
    const angularRender = new AngularPlugin<WorkflowSchemes, WorkflowAreaExtra>({ injector: this.injector });

    AreaExtensions.selectableNodes(area, AreaExtensions.selector(), {
      accumulating: AreaExtensions.accumulateOnCtrl()
    });

    angularRender.addPreset(AngularPresets.classic.setup({
      customize: {
        node: () => WorkflowNodeComponent
      }
    }));

    editor.use(area);
    area.use(angularRender);

    // Block user edits.
    editor.addPipe(context => {
      if (
        context.type === 'nodecreate' ||
        context.type === 'noderemove' ||
        context.type === 'connectioncreate' ||
        context.type === 'connectionremove'
      ) {
        if (this.loading) {
          return context;
        }
        return undefined;
      }
      return context;
    });

    this.editor = editor;
    this.area = area;
    this.initialized = true;
  }

  private async loadCurrent(): Promise<void> {
    if (!this.editor || !this.area) return;
    const wf = this.workflow();

    this.loading = true;
    try {
      for (const conn of [...this.editor.getConnections()]) {
        await this.editor.removeConnection(conn.id);
      }
      for (const node of [...this.editor.getNodes()]) {
        await this.editor.removeNode(node.id);
      }

      if (!wf) return;

      await loadIntoEditor(workflowDetailToModel(wf), this.editor, this.area);

      const needsLayout = wf.nodes.every(n => (n.layoutX === 0 && n.layoutY === 0));
      if (needsLayout) {
        await tidyLayout(this.editor, this.area);
      }

      if (this.editor.getNodes().length > 0) {
        AreaExtensions.zoomAt(this.area, this.editor.getNodes());
      }
    } finally {
      this.loading = false;
    }
  }

  private applyHighlight(): void {
    if (!this.editor || !this.area) return;
    const ids = this.highlightedNodeIds();
    const highlighted = ids ? new Set(ids) : null;

    for (const node of this.editor.getNodes()) {
      const editorNode = node as WorkflowEditorNode;
      if (highlighted === null) {
        editorNode.traceState = null;
      } else if (highlighted.has(editorNode.nodeId)) {
        editorNode.traceState = 'active';
      } else {
        editorNode.traceState = 'dimmed';
      }
      void this.area.update('node', editorNode.id);
    }
  }
}
