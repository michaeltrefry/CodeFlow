import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  OnDestroy,
  ViewChild,
  computed,
  inject,
  signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ClassicPreset, NodeEditor } from 'rete';
import { AreaExtensions, AreaPlugin } from 'rete-area-plugin';
import { ConnectionPlugin, Presets as ConnectionPresets } from 'rete-connection-plugin';
import { AngularPlugin, Presets as AngularPresets } from 'rete-angular-plugin/20';
import { AgentsApi } from '../../../core/agents.api';
import { AgentSummary, WorkflowInput, WorkflowNodeKind } from '../../../core/models';
import { WorkflowsApi } from '../../../core/workflows.api';
import {
  WorkflowAreaExtra,
  WorkflowEditorConnection,
  WorkflowEditorNode,
  WorkflowSchemes
} from './workflow-node-schemes';
import {
  DEFAULT_AGENT_OUTPUT_PORTS,
  defaultOutputPortsFor,
  emptyModel,
  labelFor,
  loadIntoEditor,
  serializeEditor,
  workflowDetailToModel
} from './workflow-serialization';

interface SelectedNode {
  editor: WorkflowEditorNode;
}

@Component({
  selector: 'app-workflow-canvas',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.Default,
  template: `
    <div class="editor-layout">
      <aside class="palette">
        <div class="panel-title">Add node</div>
        <button type="button" class="palette-item start" (click)="addPaletteNode('Start')">Start</button>
        <button type="button" class="palette-item agent" (click)="addPaletteNode('Agent')">Agent</button>
        <button type="button" class="palette-item logic" (click)="addPaletteNode('Logic')">Logic</button>
        <button type="button" class="palette-item hitl" (click)="addPaletteNode('Hitl')">HITL</button>
        <button type="button" class="palette-item escalation" (click)="addPaletteNode('Escalation')">Escalation</button>
      </aside>

      <main class="canvas-wrapper">
        <div class="toolbar">
          <div class="toolbar-left">
            <label>
              Key
              <input type="text" [(ngModel)]="workflowKey" [disabled]="hasExistingKey()" placeholder="my-workflow" />
            </label>
            <label>
              Name
              <input type="text" [(ngModel)]="workflowName" placeholder="My workflow" />
            </label>
            <label>
              Max rounds
              <input type="number" [(ngModel)]="maxRoundsPerRound" min="1" max="50" />
            </label>
          </div>
          <div class="toolbar-right">
            <a routerLink="/workflows" class="secondary">Cancel</a>
            <button type="button" (click)="save()" [disabled]="saving()">
              {{ saving() ? 'Saving…' : 'Save version' }}
            </button>
          </div>
        </div>
        @if (error()) {
          <div class="banner error">{{ error() }}</div>
        }
        @if (statusMessage()) {
          <div class="banner success">{{ statusMessage() }}</div>
        }
        <div #canvasHost class="canvas"></div>
      </main>

      <aside class="inspector">
        <div class="panel-title">Inspector</div>
        @if (selectedNode(); as sel) {
          <div class="inspector-section">
            <div class="muted small">Node</div>
            <div class="inspector-kind {{ sel.editor.kind | lowercase }}">{{ sel.editor.kind }}</div>
            <div class="muted small">ID</div>
            <code class="mono">{{ sel.editor.nodeId }}</code>
          </div>

          @if (sel.editor.kind === 'Agent' || sel.editor.kind === 'Hitl' || sel.editor.kind === 'Start' || sel.editor.kind === 'Escalation') {
            <div class="inspector-section">
              <label>
                Agent
                <select [ngModel]="sel.editor.agentKey ?? ''" (ngModelChange)="onAgentChanged(sel.editor, $event)">
                  <option value="">(pick agent)</option>
                  @for (agent of agents(); track agent.key) {
                    <option [value]="agent.key">{{ agent.key }}</option>
                  }
                </select>
              </label>
              <label>
                Pin version
                <input type="number" [ngModel]="sel.editor.agentVersion ?? null"
                       (ngModelChange)="sel.editor.agentVersion = $event" min="1" />
              </label>
            </div>
          }

          @if (sel.editor.kind === 'Logic') {
            <div class="inspector-section">
              <label>
                Script (JavaScript)
                <textarea rows="12" class="mono"
                          [ngModel]="sel.editor.script ?? ''"
                          (ngModelChange)="sel.editor.script = $event"
                          placeholder="if (input.kind === 'X') setNodePath('A'); else setNodePath('B');"></textarea>
              </label>
              <button type="button" (click)="validateLogicScript(sel.editor)">Validate</button>
              @if (scriptValidationError()) {
                <div class="tag error">{{ scriptValidationError() }}</div>
              } @else if (scriptValidationOk()) {
                <div class="tag success">Script parses OK</div>
              }

              <div class="muted small">Output ports (one per line)</div>
              <textarea rows="4"
                        [ngModel]="logicPortsText()"
                        (ngModelChange)="onLogicPortsChanged(sel.editor, $event)"></textarea>
            </div>
          }
        } @else {
          <div class="inspector-section">
            <p class="muted small">Select a node to edit. Drag from the palette to add nodes. Click and drag between port handles to connect.</p>
            <div class="inspector-section">
              <div class="panel-title">Workflow inputs</div>
              @for (input of inputs(); track input.key) {
                <div class="input-row">
                  <input type="text" [ngModel]="input.key" (ngModelChange)="updateInput(input, { key: $event })" placeholder="key" />
                  <input type="text" [ngModel]="input.displayName" (ngModelChange)="updateInput(input, { displayName: $event })" placeholder="Display name" />
                  <select [ngModel]="input.kind" (ngModelChange)="updateInput(input, { kind: $event })">
                    <option value="Text">Text</option>
                    <option value="Json">Json</option>
                  </select>
                  <label class="checkbox">
                    <input type="checkbox" [ngModel]="input.required" (ngModelChange)="updateInput(input, { required: $event })" />
                    required
                  </label>
                  <button type="button" class="icon-button" (click)="removeInput(input)">×</button>
                </div>
              }
              <button type="button" (click)="addInput()">+ Add input</button>
            </div>
          </div>
        }
      </aside>
    </div>
  `,
  styles: [`
    :host { display: block; height: 100%; }
    .editor-layout {
      display: grid;
      grid-template-columns: 180px 1fr 320px;
      grid-template-rows: 100%;
      gap: 0;
      height: calc(100vh - var(--header-height, 64px));
    }
    .palette, .inspector {
      background: var(--color-surface);
      border-right: 1px solid var(--color-border);
      padding: 1rem;
      overflow-y: auto;
    }
    .inspector { border-right: none; border-left: 1px solid var(--color-border); }
    .canvas-wrapper { display: flex; flex-direction: column; min-width: 0; }
    .canvas {
      flex: 1;
      min-height: 0;
      background: var(--color-surface-2, #0d1117);
      position: relative;
      overflow: hidden;
    }
    .toolbar {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 0.75rem 1rem;
      border-bottom: 1px solid var(--color-border);
      gap: 1rem;
      flex-wrap: wrap;
    }
    .toolbar-left { display: flex; gap: 0.75rem; flex-wrap: wrap; }
    .toolbar-right { display: flex; gap: 0.5rem; align-items: center; }
    .toolbar label {
      display: flex;
      flex-direction: column;
      font-size: 0.75rem;
      color: var(--color-muted);
      gap: 0.25rem;
    }
    .toolbar input {
      padding: 0.35rem 0.5rem;
      border-radius: 4px;
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      color: inherit;
    }
    .palette-item {
      display: block;
      width: 100%;
      padding: 0.5rem 0.75rem;
      margin-bottom: 0.5rem;
      border-radius: 4px;
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      text-align: left;
      cursor: grab;
    }
    .palette-item:hover { border-color: var(--color-accent); }
    .palette-item.start { border-left: 4px solid #3fb950; }
    .palette-item.agent { border-left: 4px solid #58a6ff; }
    .palette-item.logic { border-left: 4px solid #d29922; }
    .palette-item.hitl { border-left: 4px solid #bc8cff; }
    .palette-item.escalation { border-left: 4px solid #f85149; }
    .panel-title {
      font-size: 0.8rem;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--color-muted);
      margin-bottom: 0.5rem;
    }
    .inspector-section {
      border-top: 1px solid var(--color-border);
      padding: 0.75rem 0;
    }
    .inspector-section:first-of-type { border-top: none; padding-top: 0; }
    .inspector-section label {
      display: block;
      font-size: 0.8rem;
      color: var(--color-muted);
      margin-bottom: 0.5rem;
    }
    .inspector-section input, .inspector-section select, .inspector-section textarea {
      width: 100%;
      padding: 0.3rem 0.5rem;
      border-radius: 4px;
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      color: inherit;
      font-family: inherit;
    }
    .inspector-section textarea.mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 0.85rem; }
    .inspector-kind {
      font-weight: 600;
      padding: 0.2rem 0.4rem;
      border-radius: 3px;
      display: inline-block;
      margin-bottom: 0.5rem;
    }
    .inspector-kind.start { background: rgba(63, 185, 80, 0.2); color: #3fb950; }
    .inspector-kind.agent { background: rgba(88, 166, 255, 0.2); color: #58a6ff; }
    .inspector-kind.logic { background: rgba(210, 153, 34, 0.2); color: #d29922; }
    .inspector-kind.hitl { background: rgba(188, 140, 255, 0.2); color: #bc8cff; }
    .inspector-kind.escalation { background: rgba(248, 81, 73, 0.2); color: #f85149; }
    .mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 0.75rem; }
    .muted { color: var(--color-muted); }
    .small { font-size: 0.75rem; }
    .banner {
      padding: 0.5rem 1rem;
      font-size: 0.85rem;
    }
    .banner.error { background: rgba(248, 81, 73, 0.15); color: #f85149; }
    .banner.success { background: rgba(63, 185, 80, 0.15); color: #3fb950; }
    .input-row {
      display: grid;
      grid-template-columns: 1fr 1fr 80px auto auto;
      gap: 0.25rem;
      margin-bottom: 0.4rem;
      align-items: center;
    }
    .input-row input, .input-row select { padding: 0.2rem 0.35rem; font-size: 0.8rem; }
    .checkbox {
      display: flex !important;
      align-items: center;
      gap: 0.2rem;
      font-size: 0.75rem;
      white-space: nowrap;
    }
    .icon-button {
      width: 24px;
      height: 24px;
      padding: 0;
      border-radius: 50%;
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      cursor: pointer;
    }
    .tag.error { background: rgba(248, 81, 73, 0.15); color: #f85149; padding: 0.25rem 0.5rem; border-radius: 3px; display: inline-block; margin-top: 0.5rem; }
    .tag.success { background: rgba(63, 185, 80, 0.15); color: #3fb950; padding: 0.25rem 0.5rem; border-radius: 3px; display: inline-block; margin-top: 0.5rem; }
  `]
})
export class WorkflowCanvasComponent implements AfterViewInit, OnDestroy {
  private readonly api = inject(WorkflowsApi);
  private readonly agentsApi = inject(AgentsApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  @ViewChild('canvasHost', { static: true }) canvasHost!: ElementRef<HTMLDivElement>;

  private editor?: NodeEditor<WorkflowSchemes>;
  private area?: AreaPlugin<WorkflowSchemes, WorkflowAreaExtra>;
  private readonly selectedNodeId = signal<string | null>(null);

  readonly agents = signal<AgentSummary[]>([]);
  readonly workflowKey = signal<string>('');
  readonly workflowName = signal<string>('');
  readonly maxRoundsPerRound = signal<number>(3);
  readonly inputs = signal<WorkflowInput[]>([]);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly statusMessage = signal<string | null>(null);
  readonly hasExistingKey = signal(false);
  readonly scriptValidationError = signal<string | null>(null);
  readonly scriptValidationOk = signal(false);

  readonly selectedNode = computed<SelectedNode | null>(() => {
    const id = this.selectedNodeId();
    if (!id || !this.editor) return null;
    const node = this.editor.getNode(id) as WorkflowEditorNode | undefined;
    return node ? { editor: node } : null;
  });

  readonly logicPortsText = computed(() => {
    const sel = this.selectedNode();
    if (!sel) return '';
    return sel.editor.outputPortNames.join('\n');
  });

  async ngAfterViewInit(): Promise<void> {
    this.agentsApi.list().subscribe({
      next: agents => this.agents.set(agents),
      error: err => this.error.set(`Failed to load agents: ${err.message ?? err}`)
    });

    const editor = new NodeEditor<WorkflowSchemes>();
    const area = new AreaPlugin<WorkflowSchemes, WorkflowAreaExtra>(this.canvasHost.nativeElement);
    const connection = new ConnectionPlugin<WorkflowSchemes, WorkflowAreaExtra>();
    const angularRender = new AngularPlugin<WorkflowSchemes, WorkflowAreaExtra>({ injector: this.injector });

    AreaExtensions.selectableNodes(area, AreaExtensions.selector(), {
      accumulating: AreaExtensions.accumulateOnCtrl()
    });

    angularRender.addPreset(AngularPresets.classic.setup());
    connection.addPreset(ConnectionPresets.classic.setup());

    editor.use(area);
    area.use(connection);
    area.use(angularRender);

    area.addPipe(context => {
      if (context.type === 'nodepicked') {
        this.selectedNodeId.set(context.data.id);
        this.scriptValidationError.set(null);
        this.scriptValidationOk.set(false);
      }
      return context;
    });

    editor.addPipe(context => {
      if (context.type === 'connectioncreate') {
        const { data } = context;
        // enforce at-most-one outgoing edge per output port
        const existing = editor
          .getConnections()
          .find(c => c.source === data.source && c.sourceOutput === data.sourceOutput);
        if (existing) {
          return undefined;
        }
      }
      return context;
    });

    this.editor = editor;
    this.area = area;

    const key = this.route.snapshot.paramMap.get('key');
    if (key) {
      this.hasExistingKey.set(true);
      this.workflowKey.set(key);
      this.api.getLatest(key).subscribe({
        next: async detail => {
          this.workflowName.set(detail.name);
          this.maxRoundsPerRound.set(detail.maxRoundsPerRound);
          this.inputs.set(detail.inputs.slice().sort((a, b) => a.ordinal - b.ordinal));
          await loadIntoEditor(workflowDetailToModel(detail), editor, area);
          AreaExtensions.zoomAt(area, editor.getNodes());
        },
        error: err => this.error.set(`Failed to load workflow: ${err.message ?? err}`)
      });
    } else {
      const initialModel = emptyModel();
      await loadIntoEditor(initialModel, editor, area);
    }
  }

  ngOnDestroy(): void {
    this.area?.destroy();
  }

  async addPaletteNode(kind: WorkflowNodeKind): Promise<void> {
    if (!this.editor || !this.area) return;

    if (kind === 'Start' && this.editor.getNodes().some(n => n.kind === 'Start')) {
      this.error.set('A workflow may only contain one Start node.');
      return;
    }
    if (kind === 'Escalation' && this.editor.getNodes().some(n => n.kind === 'Escalation')) {
      this.error.set('A workflow may only contain one Escalation node.');
      return;
    }

    const node = new WorkflowEditorNode({
      nodeId: crypto.randomUUID(),
      kind,
      label: labelFor({ kind, agentKey: null }),
      outputPorts: defaultOutputPortsFor(kind)
    });

    await this.editor.addNode(node);

    // Place near origin with a small offset per-node so they don't stack.
    const existingCount = this.editor.getNodes().length - 1;
    await this.area.translate(node.id, { x: 80 + (existingCount % 4) * 40, y: 80 + existingCount * 20 });

    this.error.set(null);
  }

  onAgentChanged(node: WorkflowEditorNode, value: string): void {
    node.agentKey = value || null;
    node.label = labelFor(node);
    this.selectedNodeId.set(this.selectedNodeId()); // re-trigger selected signal
  }

  onLogicPortsChanged(node: WorkflowEditorNode, value: string): void {
    const next = value
      .split(/\r?\n/)
      .map(line => line.trim())
      .filter(line => line.length > 0);

    // Remove ports not in new list; add ports that are new.
    const current = new Set(node.outputPortNames);
    const desired = new Set(next);

    for (const port of current) {
      if (!desired.has(port)) {
        // Remove any connections on this port.
        this.editor?.getConnections()
          .filter(c => c.source === node.id && c.sourceOutput === port)
          .forEach(c => this.editor?.removeConnection(c.id));
        node.removeOutput(port);
      }
    }

    for (const port of next) {
      if (!current.has(port)) {
        node.addOutput(port, new ClassicPreset.Output(new ClassicPreset.Socket('port'), port));
      }
    }

    this.area?.update('node', node.id);
  }

  addInput(): void {
    const existing = this.inputs();
    const nextKey = `input${existing.length + 1}`;
    this.inputs.set([
      ...existing,
      {
        key: nextKey,
        displayName: nextKey,
        kind: 'Text',
        required: false,
        defaultValueJson: null,
        description: null,
        ordinal: existing.length
      }
    ]);
  }

  removeInput(input: WorkflowInput): void {
    this.inputs.set(this.inputs().filter(i => i !== input));
  }

  updateInput(input: WorkflowInput, patch: Partial<WorkflowInput>): void {
    this.inputs.set(this.inputs().map(i => (i === input ? { ...i, ...patch } : i)));
  }

  validateLogicScript(node: WorkflowEditorNode): void {
    if (!node.script) {
      this.scriptValidationError.set('Script is empty.');
      this.scriptValidationOk.set(false);
      return;
    }

    this.api.validateScript({ script: node.script, declaredPorts: node.outputPortNames }).subscribe({
      next: result => {
        if (result.ok) {
          this.scriptValidationError.set(null);
          this.scriptValidationOk.set(true);
        } else {
          this.scriptValidationError.set(result.errors.map(e => e.message).join('; '));
          this.scriptValidationOk.set(false);
        }
      },
      error: err => {
        this.scriptValidationError.set(`Validation failed: ${err.message ?? err}`);
        this.scriptValidationOk.set(false);
      }
    });
  }

  save(): void {
    if (!this.editor || !this.area) return;

    const key = this.workflowKey().trim();
    if (!key) {
      this.error.set('Workflow key is required.');
      return;
    }
    if (!this.workflowName().trim()) {
      this.error.set('Workflow name is required.');
      return;
    }

    const payload = serializeEditor(this.editor, this.area, {
      key,
      name: this.workflowName().trim(),
      maxRoundsPerRound: this.maxRoundsPerRound(),
      inputs: this.inputs()
    });

    this.saving.set(true);
    this.error.set(null);
    this.statusMessage.set(null);

    const request$ = this.hasExistingKey()
      ? this.api.addVersion(key, payload)
      : this.api.create(payload);

    request$.subscribe({
      next: result => {
        this.saving.set(false);
        this.statusMessage.set(`Saved ${result.key} v${result.version}`);
        if (!this.hasExistingKey()) {
          this.hasExistingKey.set(true);
          this.router.navigate(['/workflows', result.key, 'edit']);
        }
      },
      error: err => {
        this.saving.set(false);
        this.error.set(err?.error?.errors?.workflow?.[0] ?? err?.error?.title ?? err.message ?? 'Save failed');
      }
    });
  }
}
