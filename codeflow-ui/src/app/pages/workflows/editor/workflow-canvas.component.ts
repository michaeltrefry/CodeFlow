import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
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
  defaultOutputPortsFor,
  emptyModel,
  labelFor,
  loadIntoEditor,
  serializeEditor,
  workflowDetailToModel
} from './workflow-serialization';
import { tidyLayout } from './auto-layout';
import { WorkflowNodeComponent } from './workflow-node.component';

interface SelectedNode {
  editor: WorkflowEditorNode;
}

const DEFAULT_INPUT_KEY = 'input';

function defaultStartInput(): WorkflowInput {
  return {
    key: DEFAULT_INPUT_KEY,
    displayName: 'Input',
    kind: 'Text',
    required: true,
    defaultValueJson: null,
    description: 'The text payload delivered to the Start agent as its input artifact.',
    ordinal: 0
  };
}

@Component({
  selector: 'app-workflow-canvas',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.Default,
  template: `
    <div class="editor-layout">
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
            <button type="button" class="secondary" (click)="tidy()">Tidy up</button>
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

      <aside class="sidebar">
        <div class="sidebar-section">
          <div class="panel-title">Add node</div>
          <div class="palette">
            <button type="button" class="palette-item start" (click)="addPaletteNode('Start')">Start</button>
            <button type="button" class="palette-item agent" (click)="addPaletteNode('Agent')">Agent</button>
            <button type="button" class="palette-item logic" (click)="addPaletteNode('Logic')">Logic</button>
            <button type="button" class="palette-item hitl" (click)="addPaletteNode('Hitl')">HITL</button>
            <button type="button" class="palette-item escalation" (click)="addPaletteNode('Escalation')">Escalation</button>
          </div>
        </div>

        <div class="sidebar-section inspector">
          <div class="panel-title">Inspector</div>
          @if (selectedNode(); as sel) {
            <div class="inspector-section">
              <div class="row-spread">
                <div class="inspector-kind {{ sel.editor.kind | lowercase }}">{{ sel.editor.kind }}</div>
                <button type="button" class="danger small" (click)="removeSelectedNode()">Delete node</button>
              </div>
              <div class="muted xsmall">ID <code class="mono">{{ sel.editor.nodeId }}</code></div>
            </div>

            @if (sel.editor.kind === 'Agent' || sel.editor.kind === 'Hitl' || sel.editor.kind === 'Start' || sel.editor.kind === 'Escalation') {
              <div class="inspector-section">
                <label class="field">
                  <span>Agent</span>
                  <select [ngModel]="sel.editor.agentKey ?? ''" (ngModelChange)="onAgentChanged(sel.editor, $event)">
                    <option value="">(pick agent)</option>
                    @for (agent of agents(); track agent.key) {
                      <option [value]="agent.key">{{ agent.key }}</option>
                    }
                  </select>
                </label>
                <label class="field">
                  <span>Pin version <span class="muted xsmall">(blank = latest)</span></span>
                  <input type="number" [ngModel]="sel.editor.agentVersion ?? null"
                         (ngModelChange)="sel.editor.agentVersion = $event" min="1" />
                </label>
                <label class="field">
                  <span>Output ports <span class="muted xsmall">(one per line)</span></span>
                  <textarea rows="3" class="mono"
                            [ngModel]="outputPortsText()"
                            (ngModelChange)="onOutputPortsChanged(sel.editor, $event)"></textarea>
                  <span class="muted xsmall">
                    Ports should match the decision kinds this agent returns. Baseline agents emit <code>Completed</code> or <code>Failed</code>.
                  </span>
                </label>
              </div>
            }

            @if (sel.editor.kind === 'Logic') {
              <div class="inspector-section">
                <label class="field">
                  <span>Script (JavaScript)</span>
                  <textarea rows="12" class="mono"
                            [ngModel]="sel.editor.script ?? ''"
                            (ngModelChange)="sel.editor.script = $event"
                            placeholder="if (input.kind === 'X') setNodePath('A'); else setNodePath('B');"></textarea>
                </label>
                <div class="row">
                  <button type="button" (click)="validateLogicScript(sel.editor)">Validate</button>
                  @if (scriptValidationError()) {
                    <span class="tag error">{{ scriptValidationError() }}</span>
                  } @else if (scriptValidationOk()) {
                    <span class="tag success">Script parses OK</span>
                  }
                </div>

                <label class="field">
                  <span>Output ports <span class="muted xsmall">(one per line)</span></span>
                  <textarea rows="4" class="mono"
                            [ngModel]="outputPortsText()"
                            (ngModelChange)="onOutputPortsChanged(sel.editor, $event)"></textarea>
                </label>
              </div>
            }
          } @else {
            <div class="inspector-section">
              <p class="muted xsmall">
                Select a node to edit its settings. Drag between port handles to wire nodes. Press Delete or use the Delete button to remove a node.
              </p>
            </div>

            <div class="inspector-section">
              <div class="panel-title-inline">Workflow inputs</div>
              <p class="muted xsmall">
                Declare the <em>names</em> of inputs the workflow needs. At runtime (API call or the web UI launch form)
                the caller supplies concrete values. The first Text input is always <code>input</code> — its value is
                handed to the Start agent.
              </p>
              <div class="inputs-list">
                @for (input of inputs(); track input.key; let i = $index) {
                  <div class="input-card">
                    <div class="row-spread">
                      <strong class="mono">{{ input.key }}</strong>
                      @if (isDefaultInput(input)) {
                        <span class="tag small">required · start</span>
                      } @else {
                        <button type="button" class="icon-button" (click)="removeInput(input)" title="Remove input">×</button>
                      }
                    </div>
                    <label class="field">
                      <span>Key</span>
                      <input type="text"
                             [disabled]="isDefaultInput(input)"
                             [ngModel]="input.key"
                             (ngModelChange)="updateInput(input, { key: $event })" />
                    </label>
                    <label class="field">
                      <span>Display name</span>
                      <input type="text"
                             [ngModel]="input.displayName"
                             (ngModelChange)="updateInput(input, { displayName: $event })" />
                    </label>
                    <label class="field">
                      <span>Kind</span>
                      <select [disabled]="isDefaultInput(input)"
                              [ngModel]="input.kind"
                              (ngModelChange)="updateInput(input, { kind: $event })">
                        <option value="Text">Text</option>
                        <option value="Json">Json</option>
                      </select>
                    </label>
                    <label class="field row-inline">
                      <input type="checkbox"
                             [disabled]="isDefaultInput(input)"
                             [ngModel]="input.required"
                             (ngModelChange)="updateInput(input, { required: $event })" />
                      <span>Required at launch</span>
                    </label>
                    <label class="field">
                      <span>Description <span class="muted xsmall">(optional)</span></span>
                      <textarea rows="2"
                                [ngModel]="input.description ?? ''"
                                (ngModelChange)="updateInput(input, { description: $event })"
                                placeholder="What should the caller provide here?"></textarea>
                    </label>
                    <label class="field">
                      <span>Default value <span class="muted xsmall">(JSON; optional)</span></span>
                      <textarea rows="2" class="mono"
                                [ngModel]="input.defaultValueJson ?? ''"
                                (ngModelChange)="updateInput(input, { defaultValueJson: $event || null })"
                                [placeholder]="input.kind === 'Text' ? '&quot;example text&quot;' : '{&quot;example&quot;: true}'"></textarea>
                    </label>
                  </div>
                }
              </div>
              <button type="button" class="secondary" (click)="addInput()">+ Add input</button>
            </div>
          }
        </div>
      </aside>
    </div>
  `,
  styles: [`
    :host { display: block; height: 100%; }
    .editor-layout {
      display: grid;
      grid-template-columns: 1fr 360px;
      grid-template-rows: 100%;
      gap: 0;
      height: calc(100vh - var(--header-height, 64px));
    }
    .sidebar {
      background: var(--color-surface);
      border-left: 1px solid var(--color-border);
      overflow-y: auto;
      display: flex;
      flex-direction: column;
    }
    .sidebar-section {
      padding: 1rem;
      border-bottom: 1px solid var(--color-border);
    }
    .sidebar-section:last-child { border-bottom: none; flex: 1; }
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
    .palette {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 0.4rem;
    }
    .palette-item {
      display: block;
      padding: 0.5rem 0.6rem;
      border-radius: 4px;
      border: 1px solid var(--color-border);
      background: rgba(255, 255, 255, 0.03);
      color: var(--color-text, #c9d1d9);
      text-align: left;
      font-weight: 500;
      cursor: grab;
    }
    .palette-item:hover { border-color: var(--color-accent); background: rgba(255, 255, 255, 0.06); }
    .palette-item.start { border-left: 4px solid #3fb950; }
    .palette-item.agent { border-left: 4px solid #58a6ff; }
    .palette-item.logic { border-left: 4px solid #d29922; }
    .palette-item.hitl { border-left: 4px solid #bc8cff; }
    .palette-item.escalation { border-left: 4px solid #f85149; }
    .panel-title {
      font-size: 0.75rem;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--color-muted);
      margin-bottom: 0.75rem;
    }
    .panel-title-inline {
      font-size: 0.85rem;
      font-weight: 600;
      margin-bottom: 0.4rem;
    }
    .inspector-section {
      border-top: 1px solid var(--color-border);
      padding: 0.75rem 0;
    }
    .inspector-section:first-of-type { border-top: none; padding-top: 0; }
    .field {
      display: block;
      margin-bottom: 0.75rem;
    }
    .field > span {
      display: block;
      font-size: 0.75rem;
      color: var(--color-muted);
      margin-bottom: 0.25rem;
    }
    .field.row-inline {
      display: flex;
      align-items: center;
      gap: 0.4rem;
      font-size: 0.85rem;
    }
    .field.row-inline > span { display: inline; margin: 0; color: inherit; }
    .field input[type='text'], .field input[type='number'], .field select, .field textarea {
      width: 100%;
      padding: 0.35rem 0.5rem;
      border-radius: 4px;
      border: 1px solid var(--color-border);
      background: var(--color-surface-2, #0d1117);
      color: inherit;
      font-family: inherit;
    }
    .field textarea.mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 0.8rem; }
    .inputs-list { display: flex; flex-direction: column; gap: 0.75rem; margin-bottom: 0.75rem; }
    .input-card {
      padding: 0.75rem;
      border: 1px solid var(--color-border);
      border-radius: 4px;
      background: rgba(255, 255, 255, 0.02);
    }
    .inspector-kind {
      font-weight: 600;
      padding: 0.2rem 0.5rem;
      border-radius: 3px;
      display: inline-block;
      font-size: 0.85rem;
    }
    .inspector-kind.start { background: rgba(63, 185, 80, 0.2); color: #3fb950; }
    .inspector-kind.agent { background: rgba(88, 166, 255, 0.2); color: #58a6ff; }
    .inspector-kind.logic { background: rgba(210, 153, 34, 0.2); color: #d29922; }
    .inspector-kind.hitl { background: rgba(188, 140, 255, 0.2); color: #bc8cff; }
    .inspector-kind.escalation { background: rgba(248, 81, 73, 0.2); color: #f85149; }
    .row-spread { display: flex; justify-content: space-between; align-items: center; gap: 0.5rem; margin-bottom: 0.4rem; }
    .row { display: flex; gap: 0.5rem; align-items: center; margin-bottom: 0.5rem; flex-wrap: wrap; }
    .mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 0.75rem; }
    .muted { color: var(--color-muted); }
    .xsmall { font-size: 0.72rem; }
    .small { font-size: 0.8rem; }
    button.small { padding: 0.2rem 0.5rem; font-size: 0.75rem; }
    button.danger { background: transparent; border: 1px solid #f85149; color: #f85149; }
    button.danger:hover { background: rgba(248, 81, 73, 0.15); }
    .banner { padding: 0.5rem 1rem; font-size: 0.85rem; }
    .banner.error { background: rgba(248, 81, 73, 0.15); color: #f85149; }
    .banner.success { background: rgba(63, 185, 80, 0.15); color: #3fb950; }
    .icon-button {
      width: 22px;
      height: 22px;
      padding: 0;
      border-radius: 50%;
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      cursor: pointer;
      color: inherit;
    }
    .icon-button:hover { border-color: #f85149; color: #f85149; }
    .tag.error { background: rgba(248, 81, 73, 0.15); color: #f85149; padding: 0.2rem 0.4rem; border-radius: 3px; font-size: 0.75rem; }
    .tag.success { background: rgba(63, 185, 80, 0.15); color: #3fb950; padding: 0.2rem 0.4rem; border-radius: 3px; font-size: 0.75rem; }
    .tag.small { font-size: 0.7rem; }
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
  private readonly portsRevision = signal(0);

  readonly agents = signal<AgentSummary[]>([]);
  readonly workflowKey = signal<string>('');
  readonly workflowName = signal<string>('');
  readonly maxRoundsPerRound = signal<number>(3);
  readonly inputs = signal<WorkflowInput[]>([defaultStartInput()]);
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

  readonly outputPortsText = computed(() => {
    // Read portsRevision so the computed recomputes when ports mutate.
    this.portsRevision();
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

    angularRender.addPreset(AngularPresets.classic.setup({
      customize: {
        node: () => WorkflowNodeComponent
      }
    }));
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
        const existing = editor
          .getConnections()
          .find(c => c.source === data.source && c.sourceOutput === data.sourceOutput);
        if (existing) {
          return undefined;
        }
      }
      if (context.type === 'noderemoved') {
        if (this.selectedNodeId() === context.data.id) {
          this.selectedNodeId.set(null);
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
          const loadedInputs = detail.inputs.slice().sort((a, b) => a.ordinal - b.ordinal);
          this.inputs.set(loadedInputs.length === 0 ? [defaultStartInput()] : loadedInputs);
          await loadIntoEditor(workflowDetailToModel(detail), editor, area);
          if (editor.getNodes().length > 0) {
            AreaExtensions.zoomAt(area, editor.getNodes());
          }
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

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Delete' && event.key !== 'Backspace') return;
    const target = event.target as HTMLElement | null;
    if (target) {
      const tag = target.tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || target.isContentEditable) {
        return;
      }
    }
    if (!this.selectedNodeId()) return;
    event.preventDefault();
    void this.removeSelectedNode();
  }

  async tidy(): Promise<void> {
    if (!this.editor || !this.area) return;
    await tidyLayout(this.editor, this.area);
    if (this.editor.getNodes().length > 0) {
      AreaExtensions.zoomAt(this.area, this.editor.getNodes());
    }
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

    const existingCount = this.editor.getNodes().length - 1;
    await this.area.translate(node.id, { x: 80 + (existingCount % 4) * 40, y: 80 + existingCount * 20 });

    this.error.set(null);
  }

  async removeSelectedNode(): Promise<void> {
    if (!this.editor) return;
    const id = this.selectedNodeId();
    if (!id) return;

    // Remove any connections referencing this node first.
    const touching = this.editor.getConnections()
      .filter(c => c.source === id || c.target === id);
    for (const conn of touching) {
      await this.editor.removeConnection(conn.id);
    }
    await this.editor.removeNode(id);
    this.selectedNodeId.set(null);
  }

  onAgentChanged(node: WorkflowEditorNode, value: string): void {
    node.agentKey = value || null;
    node.label = labelFor(node);
    this.selectedNodeId.set(this.selectedNodeId());
  }

  onOutputPortsChanged(node: WorkflowEditorNode, value: string): void {
    const next = value
      .split(/\r?\n/)
      .map(line => line.trim())
      .filter(line => line.length > 0);

    const current = new Set(node.outputPortNames);
    const desired = new Set(next);

    for (const port of current) {
      if (!desired.has(port)) {
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
    this.portsRevision.update(v => v + 1);
  }

  addInput(): void {
    const existing = this.inputs();
    let index = existing.length + 1;
    let key = `extra${index}`;
    while (existing.some(i => i.key === key)) {
      index += 1;
      key = `extra${index}`;
    }
    this.inputs.set([
      ...existing,
      {
        key,
        displayName: '',
        kind: 'Text',
        required: false,
        defaultValueJson: null,
        description: null,
        ordinal: existing.length
      }
    ]);
  }

  removeInput(input: WorkflowInput): void {
    if (this.isDefaultInput(input)) return;
    this.inputs.set(this.inputs().filter(i => i !== input));
  }

  updateInput(input: WorkflowInput, patch: Partial<WorkflowInput>): void {
    if (this.isDefaultInput(input)) {
      // Protect the key/kind/required flag on the baked-in `input` input.
      const { key: _, kind: __, required: ___, ...safe } = patch;
      patch = safe;
    }
    this.inputs.set(this.inputs().map(i => (i === input ? { ...i, ...patch } : i)));
  }

  isDefaultInput(input: WorkflowInput): boolean {
    return input.key === DEFAULT_INPUT_KEY;
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
