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
  WorkflowEditorConnection,
  WorkflowEditorNode,
  WorkflowNodeTokenOverlay,
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
      background: var(--bg);
      background-image: radial-gradient(circle at center, color-mix(in oklab, var(--muted) 22%, transparent) 1px, transparent 1.5px);
      background-size: 22px 22px;
      border: 1px solid var(--border);
      border-radius: var(--radius-lg);
      overflow: hidden;
    }
    :host-context([data-theme="light"]) .readonly-canvas {
      background-image: radial-gradient(circle at center, color-mix(in oklab, var(--muted) 25%, transparent) 1px, transparent 1.5px);
    }
  `]
})
export class WorkflowReadonlyCanvasComponent implements AfterViewInit, OnChanges, OnDestroy {
  private readonly injector = inject(Injector);

  @ViewChild('canvasHost', { static: true }) canvasHost!: ElementRef<HTMLDivElement>;

  readonly workflow = input<WorkflowDetail | null>(null);
  /** When non-null, highlights the listed node ids and dims every other node. */
  readonly highlightedNodeIds = input<string[] | null>(null);
  /**
   * When non-null, dims every wire whose `<sourceNodeId>::<sourceOutputPort>` key is NOT in
   * the set. Mirrors the node dimming so the live trace view shows the path through the
   * workflow on both nodes and the wires connecting them. Null = no wire dimming (full
   * opacity for every edge), which is what the editor / static-display callers want.
   */
  readonly highlightedEdgeKeys = input<string[] | null>(null);
  /**
   * Token Usage Tracking [Slice 7]: per-node token-usage overlays. The trace
   * detail page builds this map by combining slice 5's per-node rollups with the
   * descendant-saga rollups that belong to Subflow / ReviewLoop / Swarm nodes
   * (those nodes don't issue LLM calls themselves — they spawn child sagas, and
   * the rolled-up total comes from descendants). When non-null, each editor
   * node's `tokenUsageOverlay` is hydrated from this map so the node template
   * renders the in-graph badge.
   */
  readonly tokenUsageByNodeId = input<Map<string, WorkflowNodeTokenOverlay> | null>(null);

  private editor?: NodeEditor<WorkflowSchemes>;
  private area?: AreaPlugin<WorkflowSchemes, WorkflowAreaExtra>;
  private initialized = false;
  private loading = false;
  private wheelHandler?: (event: WheelEvent) => void;
  /** Tracks the rendered DOM element for each connection so we can re-apply opacity styling
   *  when `highlightedEdgeKeys` changes without re-rendering the wire. Populated by the
   *  area-pipe `render` hook below and cleared on `unmount`. */
  private readonly connectionElements = new Map<string, HTMLElement>();

  async ngAfterViewInit(): Promise<void> {
    await this.initialize();
    await this.loadCurrent();
    this.applyHighlight();
    this.applyEdgeHighlight();
  }

  async ngOnChanges(changes: SimpleChanges): Promise<void> {
    if (!this.initialized) return;
    if (changes['workflow']) {
      await this.loadCurrent();
      this.applyHighlight();
      this.applyEdgeHighlight();
    } else {
      if (changes['highlightedNodeIds'] || changes['tokenUsageByNodeId']) {
        // Highlight + token-usage overlays both ride the same per-node update path,
        // so a change to either re-applies both in one pass.
        this.applyHighlight();
      }
      if (changes['highlightedEdgeKeys']) {
        this.applyEdgeHighlight();
      }
    }
  }

  ngOnDestroy(): void {
    if (this.wheelHandler) {
      this.canvasHost.nativeElement.removeEventListener('wheel', this.wheelHandler, { capture: true });
      this.wheelHandler = undefined;
    }
    this.connectionElements.clear();
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

    // Track connection elements as rete renders them so `applyEdgeHighlight` can apply opacity
    // without re-rendering the wire. Mirror the editor canvas's bind/release pattern, minus the
    // click-to-select handler (this canvas is read-only).
    area.addPipe(context => {
      if (context.type === 'render' && context.data.type === 'connection') {
        const conn = context.data.payload as WorkflowEditorConnection;
        this.connectionElements.set(conn.id, context.data.element);
        // Defer one frame so the SVG path exists inside the wrapper before we touch styles.
        requestAnimationFrame(() => this.applyEdgeOpacity(conn.id));
      } else if (context.type === 'unmount') {
        for (const [id, el] of this.connectionElements.entries()) {
          if (el === context.data.element) {
            this.connectionElements.delete(id);
            break;
          }
        }
      }
      return context;
    });

    // Swallow wheel events before they reach the rete area plugin so trackpad / wheel scrolling
    // over the readonly canvas scrolls the page instead of zooming the graph. Capture phase +
    // stopPropagation prevents rete's bubble-phase listeners from firing; we deliberately do
    // NOT call preventDefault so the browser's native page-scroll still works when the pointer
    // is over the canvas. The zoom-to-fit on load uses AreaExtensions.zoomAt programmatically,
    // which doesn't go through the DOM wheel pipeline, so it still works.
    this.wheelHandler = (event: WheelEvent) => {
      event.stopPropagation();
    };
    this.canvasHost.nativeElement.addEventListener('wheel', this.wheelHandler, {
      capture: true,
      passive: true,
    });

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
      // The unmount pipe clears entries one-by-one as rete tears wires down, but a stale
      // entry would still cause `applyEdgeOpacity` to mis-style a freshly-rendered wire that
      // happened to reuse an id. Hard-reset to make the post-load state unambiguous.
      this.connectionElements.clear();

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
    const tokenOverlay = this.tokenUsageByNodeId();

    for (const node of this.editor.getNodes()) {
      const editorNode = node as WorkflowEditorNode;
      if (highlighted === null) {
        editorNode.traceState = null;
      } else if (highlighted.has(editorNode.nodeId)) {
        editorNode.traceState = 'active';
      } else {
        editorNode.traceState = 'dimmed';
      }
      // Token-usage overlay: read by node id from the map. Null map clears every
      // node's overlay; missing-id leaves the node without a badge (e.g., Logic /
      // Transform nodes that never issue LLM calls and have no descendant saga).
      editorNode.tokenUsageOverlay = tokenOverlay?.get(editorNode.nodeId) ?? null;
      void this.area.update('node', editorNode.id);
    }
  }

  /** Re-applies opacity to every currently-rendered connection. Called when
   *  `highlightedEdgeKeys` flips (live trace tick) or after the workflow finishes loading. */
  private applyEdgeHighlight(): void {
    if (!this.editor || !this.area) return;
    const keys = this.highlightedEdgeKeys();
    const keySet = keys ? new Set(keys) : null;
    for (const id of this.connectionElements.keys()) {
      this.applyEdgeOpacity(id, keySet);
    }
  }

  /** Dims a single wire when the executed-key set is non-null and its source-port pair isn't
   *  a member. Restores full opacity when the set is null (editor / static-display mode) or
   *  when the wire is in the set. */
  private applyEdgeOpacity(connectionId: string, keySet?: Set<string> | null): void {
    const element = this.connectionElements.get(connectionId);
    if (!element || !this.editor) return;
    const conn = this.editor.getConnection(connectionId) as WorkflowEditorConnection | undefined;
    if (!conn) return;

    element.style.transition = 'opacity 120ms ease';

    // Single-call path (from the render pipe) didn't pre-build the set; resolve it lazily.
    const set = keySet === undefined
      ? (this.highlightedEdgeKeys() ? new Set(this.highlightedEdgeKeys()!) : null)
      : keySet;

    if (set === null) {
      element.style.opacity = '';
      return;
    }

    const sourceNode = this.editor.getNode(conn.source) as WorkflowEditorNode | undefined;
    if (!sourceNode) return;

    const key = `${sourceNode.nodeId}::${conn.sourceOutput}`;
    element.style.opacity = set.has(key) ? '' : '0.25';
  }
}
