import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  Injector,
  OnDestroy,
  ViewChild,
  computed,
  effect,
  inject,
  runInInjectionContext,
  signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { PageContextService } from '../../../core/page-context.service';
import { ClassicPreset, NodeEditor } from 'rete';
import { AreaExtensions, AreaPlugin } from 'rete-area-plugin';
import { ConnectionPlugin, Presets as ConnectionPresets } from 'rete-connection-plugin';
import { AngularPlugin, Presets as AngularPresets } from 'rete-angular-plugin/20';
import { AgentsApi } from '../../../core/agents.api';
import {
  AgentConfig,
  AgentSummary,
  MAX_WORKFLOW_TAGS,
  WORKFLOW_CATEGORIES,
  WorkflowCategory,
  WorkflowInput,
  WorkflowNodeKind,
  WorkflowSummary,
  WorkflowSwarmProtocol,
  WorkflowTransformOutputType
} from '../../../core/models';
import { NodeDataflowScope, WorkflowDataflowSnapshot, WorkflowsApi } from '../../../core/workflows.api';
import { ButtonComponent } from '../../../ui/button.component';
import { ChipComponent } from '../../../ui/chip.component';
import { TagInputComponent } from '../../../ui/tag-input.component';
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
import { MonacoAmbientLib, MonacoMarker, MonacoScriptEditorComponent } from './monaco-script-editor.component';
import { NodeContextMenuComponent, NodeContextMenuItem } from './node-context-menu.component';
import { buildScriptAmbientLibs } from './script-ambient-libs';
import {
  AgentInPlaceEditDialogComponent,
  InPlaceEditResult
} from './agent-in-place-edit-dialog.component';
import {
  PublishForkDialogComponent,
  PublishForkResult
} from './publish-fork-dialog.component';
import {
  VersionUpdateDialogComponent,
  VersionUpdateResult
} from './version-update-dialog.component';
import { WorkflowVersionHistoryDialogComponent } from './workflow-version-history-dialog.component';
import { WorkflowCanvasDialogOrchestrator } from './workflow-canvas-dialog-orchestrator.service';
import { WorkflowBackedgeAnalyzer } from './workflow-backedge-analyzer';
import { declaredOutputPorts, derivePortRows, hasPortDrift } from './workflow-port-rows';
import type { DerivedPortRow } from './workflow-port-rows';

const AGENT_BEARING_KINDS: ReadonlySet<WorkflowNodeKind> = new Set([
  'Agent',
  'Hitl',
  'Start'
] as WorkflowNodeKind[]);

const FORK_KEY_PREFIX = '__fork_';

interface NodeContextMenuState {
  nodeId: string;
  x: number;
  y: number;
  items: NodeContextMenuItem[];
}

interface SelectedNode {
  editor: WorkflowEditorNode;
}

interface SelectedConnection {
  editor: WorkflowEditorConnection;
}

interface SelectedAgentDocs {
  nodeEditorId: string;
  agentKey: string;
  agentVersion: number;
  config: AgentConfig;
}

interface PortReferenceRow {
  port: string;
  source: 'payload' | 'template' | 'blank';
  content: string;
}

const DEFAULT_INPUT_KEY = 'input';

/** `{{ workflow.X }}` / `{{ context.X }}` reference scanner used by the VZ1 inspector to
 *  flag reads that have no upstream writer. Mirrors the backend regex in
 *  `WorkflowVarDeclarationRule` so the editor surfaces the same coupling the validator does. */
const WORKFLOW_REF_TEMPLATE = /\{\{\s*workflow\.([A-Za-z_][A-Za-z0-9_]*)/g;
const CONTEXT_REF_TEMPLATE = /\{\{\s*context\.([A-Za-z_][A-Za-z0-9_]*)/g;
/** JS read in scripts: `workflow.X`, `workflow['X']`, `workflow["X"]`. */
const WORKFLOW_REF_SCRIPT_DOT = /\bworkflow\.([A-Za-z_][A-Za-z0-9_]*)/g;
const WORKFLOW_REF_SCRIPT_BRACKET = /\bworkflow\[\s*['"]([^'"]+)['"]\s*\]/g;
const CONTEXT_REF_SCRIPT_DOT = /\bcontext\.([A-Za-z_][A-Za-z0-9_]*)/g;
const CONTEXT_REF_SCRIPT_BRACKET = /\bcontext\[\s*['"]([^'"]+)['"]\s*\]/g;

function extractMatches(pattern: RegExp, text: string, sink: Set<string>): void {
  pattern.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = pattern.exec(text)) !== null) sink.add(m[1]);
}

function extractWorkflowAndContextRefs(text: string, wf: Set<string>, ctx: Set<string>): void {
  if (!text) return;
  extractMatches(WORKFLOW_REF_TEMPLATE, text, wf);
  extractMatches(CONTEXT_REF_TEMPLATE, text, ctx);
}

function extractWorkflowAndContextRefsFromScript(text: string, wf: Set<string>, ctx: Set<string>): void {
  if (!text) return;
  extractMatches(WORKFLOW_REF_SCRIPT_DOT, text, wf);
  extractMatches(WORKFLOW_REF_SCRIPT_BRACKET, text, wf);
  extractMatches(CONTEXT_REF_SCRIPT_DOT, text, ctx);
  extractMatches(CONTEXT_REF_SCRIPT_BRACKET, text, ctx);
}

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
  selector: 'cf-workflow-canvas',
  standalone: true,
  imports: [CommonModule, FormsModule, MonacoScriptEditorComponent, TagInputComponent, ButtonComponent, ChipComponent, NodeContextMenuComponent, AgentInPlaceEditDialogComponent, PublishForkDialogComponent, VersionUpdateDialogComponent, WorkflowVersionHistoryDialogComponent],
  providers: [WorkflowCanvasDialogOrchestrator],
  changeDetection: ChangeDetectionStrategy.Default,
  template: `
    <div class="editor-layout">
      <main class="canvas-wrapper" [class.show-implicit-failed]="showImplicitFailed()">
        <div class="toolbar">
          <div class="toolbar-actions">
            <button type="button" cf-button variant="default" size="sm"
                    [active]="showImplicitFailed()"
                    (click)="toggleImplicitFailed()"
                    title="Show implicit Failed port on every node (Shift+F)"
                    accesskey="f">
              {{ showImplicitFailed() ? 'Hide failed ports' : 'Show failed ports' }}
            </button>
            <button type="button" cf-button variant="default" size="sm"
                    (click)="dialogs.openHistory()"
                    [disabled]="!hasExistingKey()"
                    title="Compare versions of this workflow">
              History
            </button>
            <button type="button" cf-button variant="default" size="sm" (click)="tidy()">Tidy up</button>
            <button type="button" cf-button variant="default" size="sm" (click)="cancel()">Cancel</button>
            <button type="button" cf-button variant="primary" size="sm" (click)="save()" [disabled]="saving()">
              {{ saving() ? 'Saving…' : 'Save version' }}
            </button>
          </div>
          <div class="toolbar-fields">
            <label class="tb-field">
              <span class="tb-label">Name</span>
              <input type="text" [(ngModel)]="workflowName" placeholder="My workflow" />
            </label>
            <label class="tb-field">
              <span class="tb-label">Key</span>
              <input type="text" [(ngModel)]="workflowKey" [disabled]="hasExistingKey()" placeholder="my-workflow" />
            </label>
            <label class="tb-field tb-narrow">
              <span class="tb-label">Category</span>
              <select [ngModel]="workflowCategory()" (ngModelChange)="workflowCategory.set($event)">
                @for (opt of categoryOptions; track opt) {
                  <option [ngValue]="opt">{{ opt }}</option>
                }
              </select>
            </label>
            <label class="tb-field tb-narrow">
              <span class="tb-label">Max rounds</span>
              <input type="number" [(ngModel)]="maxRoundsPerRound" min="1" max="50" />
            </label>
            <label class="tb-field tb-tags">
              <span class="tb-label">Tags <span class="tb-hint">up to {{ maxTags }}</span></span>
              <cf-tag-input
                [tags]="workflowTags()"
                [suggestions]="tagSuggestions()"
                [maxTags]="maxTags"
                [showCounter]="false"
                placeholder="Add tag…"
                (tagsChange)="workflowTags.set($event)"></cf-tag-input>
            </label>
          </div>
        </div>
        @if (error()) {
          <div class="banner error">{{ error() }}</div>
        }
        @if (statusMessage()) {
          <div class="banner success">{{ statusMessage() }}</div>
        }
        <div #canvasHost class="canvas" (contextmenu)="onCanvasContextMenu($event)"></div>
        <section class="port-reference-drawer" [class.open]="selectedNode()">
          @if (selectedNode(); as sel) {
            <div class="port-reference-head">
              <div>
                <div class="panel-title-inline">Port payload reference</div>
                <div class="muted xsmall">
                  <span class="mono">{{ sel.editor.label }}</span>
                  @if (selectedAgentDocs(); as docs) {
                    <span> · {{ docs.agentKey }} v{{ docs.agentVersion }}</span>
                  }
                </div>
              </div>
            </div>
            <div class="port-reference-body">
              @if (selectedAgentDocsLoading()) {
                <div class="muted xsmall">Loading agent port examples…</div>
              } @else if (selectedAgentDocsError()) {
                <cf-chip variant="err" dot>{{ selectedAgentDocsError() }}</cf-chip>
              } @else {
                @for (row of selectedPortReferences(); track row.port) {
                  <article class="port-reference-row" [class.blank]="row.source === 'blank'">
                    <div class="port-reference-meta">
                      <span class="mono port-name">{{ row.port }}</span>
                      @if (row.source === 'payload') {
                        <cf-chip variant="ok" mono>payload example</cf-chip>
                      } @else if (row.source === 'template') {
                        <cf-chip mono>decision template</cf-chip>
                      }
                    </div>
                    @if (row.content) {
                      <pre>{{ row.content }}</pre>
                    }
                  </article>
                }
              }
            </div>
          }
        </section>
      </main>

      <aside class="sidebar">
        <div class="sidebar-section">
          <div class="panel-title">Add node</div>
          <div class="palette">
            <button type="button" class="palette-item start" (click)="addPaletteNode('Start')">Start</button>
            <button type="button" class="palette-item agent" (click)="addPaletteNode('Agent')">Agent</button>
            <button type="button" class="palette-item logic" (click)="addPaletteNode('Logic')">Logic</button>
            <button type="button" class="palette-item transform" (click)="addPaletteNode('Transform')">Transform</button>
            <button type="button" class="palette-item hitl" (click)="addPaletteNode('Hitl')">HITL</button>
            <button type="button" class="palette-item subflow" (click)="addPaletteNode('Subflow')">Subflow</button>
            <button type="button" class="palette-item reviewloop" (click)="addPaletteNode('ReviewLoop')">Review Loop</button>
            <button type="button" class="palette-item swarm" (click)="addPaletteNode('Swarm')">Swarm</button>
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

            @if (sel.editor.kind === 'Agent' || sel.editor.kind === 'Hitl' || sel.editor.kind === 'Start') {
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
                         (ngModelChange)="onAgentVersionChanged(sel.editor, $event)" min="1" />
                </label>
                <div class="field">
                  <span class="field-label">Output ports <span class="muted xsmall">(derived from agent's declared outputs)</span></span>
                  @if (derivedPortRows().length > 0) {
                    <ul class="port-list mono">
                      @for (p of derivedPortRows(); track p.name + ':' + p.status) {
                        <li [attr.data-port-status]="p.status">
                          <code>{{ p.name }}</code>
                          @if (p.status === 'stale') {
                            <cf-chip variant="err" mono title="Wired on this node but the pinned agent can't submit it. Agents reaching this port at runtime would crash.">stale (not declared by agent)</cf-chip>
                          } @else if (p.status === 'missing') {
                            <cf-chip variant="warn" mono title="Declared by the pinned agent but missing from this node. Submissions to this port would route nowhere (dead branch).">missing on node</cf-chip>
                          }
                        </li>
                      }
                      <li class="implicit">
                        <code>Failed</code> <span class="muted xsmall">(implicit, always wirable)</span>
                      </li>
                    </ul>
                    @if (selectedNodeHasPortDrift()) {
                      <div class="row" style="gap: 8px; align-items: center; margin-top: 8px">
                        <button type="button" cf-button variant="default" size="sm" (click)="syncPortsFromAgent(sel.editor)">
                          Sync from agent
                        </button>
                        <span class="muted xsmall">Replaces this node's ports with the agent's declared outputs. Wires on still-declared ports are preserved.</span>
                      </div>
                    }
                  } @else {
                    <p class="muted xsmall">
                      @if (sel.editor.agentKey) {
                        The pinned agent declares no outputs yet. Add an entry to the agent's <code>outputs</code> list — the picker will refresh on next selection.
                      } @else {
                        Pick an agent above to see its declared output ports.
                      }
                      Authors no longer hand-edit ports on Agent/Hitl/Start nodes — port names come from the pinned agent's <code>outputs</code>. The implicit <code>Failed</code> port is always wirable for error recovery.
                    </p>
                  }
                  @if (brokenEdgesForSelected().length > 0) {
                    <ul class="muted xsmall broken-edges">
                      @for (msg of brokenEdgesForSelected(); track msg) {
                        <li class="error">{{ msg }}</li>
                      }
                    </ul>
                  }
                </div>
              </div>

              <div class="inspector-section">
                <div class="field">
                  <span class="field-label">Input script <span class="muted xsmall">(optional)</span></span>
                  <p class="muted xsmall">
                    Runs <em>before</em> this node receives its input. Sees <code>input</code> (the upstream artifact) and <code>context</code>/<code>workflow</code>. Call <code>setInput('…')</code> to rewrite what this node receives. May also <code>setContext('key', value)</code>. Leave blank to pass the upstream artifact through unchanged.
                  </p>
                  <cf-monaco-script-editor
                    class="script-editor"
                    [value]="sel.editor.inputScript ?? ''"
                    [markers]="inputScriptMarkers()"
                    [ambientLibs]="inputScriptAmbientLibs()"
                    snippetKind="input-script"
                    [snippetInLoop]="selectedNodeInLoop()"
                    (valueChange)="onNodeScriptChanged(sel.editor, 'input', $event)"></cf-monaco-script-editor>
                </div>
                <div class="row">
                  <button type="button" (click)="validateNodeScript(sel.editor, 'input')">Validate</button>
                  @if (inputScriptValidationError()) {
                    <cf-chip variant="err" dot>{{ inputScriptValidationError() }}</cf-chip>
                  } @else if (inputScriptValidationOk()) {
                    <cf-chip variant="ok" dot>Script parses OK</cf-chip>
                  }
                </div>
              </div>

              <div class="inspector-section">
                <div class="field">
                  <span class="field-label">Output script <span class="muted xsmall">(optional)</span></span>
                  <p class="muted xsmall">
                    Runs <em>after</em> the agent completes. Sees <code>output</code> (the agent's output with <code>output.decision</code> and <code>output.decisionPayload</code> attached) and <code>context</code>, and calls <code>setNodePath('…')</code> to choose an outgoing port. May also <code>setOutput('…')</code> to rewrite the downstream artifact, or <code>setContext('key', value)</code> to accumulate workflow context. Leave blank to route by the emitted decision kind.
                  </p>
                  <cf-monaco-script-editor
                    class="script-editor"
                    [value]="sel.editor.outputScript ?? ''"
                    [markers]="scriptMarkers()"
                    [ambientLibs]="outputScriptAmbientLibs()"
                    snippetKind="output-script"
                    [snippetInLoop]="selectedNodeInLoop()"
                    (valueChange)="onNodeScriptChanged(sel.editor, 'output', $event)"></cf-monaco-script-editor>
                </div>
                <div class="row">
                  <button type="button" (click)="validateNodeScript(sel.editor, 'output')">Validate</button>
                  @if (scriptValidationError()) {
                    <cf-chip variant="err" dot>{{ scriptValidationError() }}</cf-chip>
                  } @else if (scriptValidationOk()) {
                    <cf-chip variant="ok" dot>Script parses OK</cf-chip>
                  }
                </div>
              </div>
            }

            @if (sel.editor.kind === 'Subflow') {
              <div class="inspector-section">
                <label class="field">
                  <span>Workflow <span class="muted xsmall">(the subflow to invoke; ports preview at latest version)</span></span>
                  <select [ngModel]="sel.editor.subflowKey ?? ''"
                          (ngModelChange)="onSubflowKeyChanged(sel.editor, $event)">
                    <option value="">(pick workflow)</option>
                    @for (wf of availableSubflowTargets(); track wf.key) {
                      <option [value]="wf.key">{{ subflowPickerLabel(wf) }}</option>
                    }
                  </select>
                  @if (sel.editor.subflowKey && sel.editor.subflowKey === workflowKey()) {
                    <cf-chip variant="err" dot>Self-reference — save will be rejected.</cf-chip>
                  }
                </label>
                <label class="field">
                  <span>Version <span class="muted xsmall">(blank = latest at save)</span></span>
                  <input type="number" min="1"
                         [ngModel]="sel.editor.subflowVersion ?? null"
                         (ngModelChange)="onSubflowVersionChanged(sel.editor, $event)" />
                  <span class="muted xsmall">
                    Leave blank to pin to the then-current latest when this workflow is saved. Re-saving re-resolves it. Enter a specific integer to pin permanently.
                  </span>
                </label>
                @if (selectedSubflowDetail(); as detail) {
                  <div class="field">
                    <span class="field-label">Child workflow outline</span>
                    <div class="subflow-outline">
                      <div class="row-spread">
                        <strong class="mono small">{{ detail.name }}</strong>
                        <span class="muted xsmall">v{{ detail.version }} · {{ detail.nodes.length }} nodes</span>
                      </div>
                      <ul class="subflow-nodes">
                        @for (n of detail.nodes; track n.id) {
                          <li><cf-chip mono>{{ n.kind }}</cf-chip> <span class="mono xsmall">{{ labelForOutline(n) }}</span></li>
                        }
                      </ul>
                    </div>
                  </div>
                }
                <div class="field">
                  <span class="field-label">Output ports <span class="muted xsmall">(inherited from child workflow's terminal ports)</span></span>
                  @if (sel.editor.outputPortNames.length > 0) {
                    <ul class="port-list mono">
                      @for (p of sel.editor.outputPortNames; track p) {
                        <li><code>{{ p }}</code></li>
                      }
                      <li class="implicit">
                        <code>Failed</code> <span class="muted xsmall">(implicit, always wirable)</span>
                      </li>
                    </ul>
                  } @else {
                    <p class="muted xsmall">
                      @if (sel.editor.subflowKey) {
                        The pinned child workflow has no terminal ports yet — wire its terminal nodes' outputs through to a designed exit port.
                      } @else {
                        Pick a child workflow above to see its terminal output ports.
                      }
                      Subflow nodes inherit ports from the pinned child workflow's terminal ports — authors don't hand-edit them. The implicit <code>Failed</code> port is always wirable.
                    </p>
                  }
                  @if (brokenEdgesForSelected().length > 0) {
                    <ul class="muted xsmall broken-edges">
                      @for (msg of brokenEdgesForSelected(); track msg) {
                        <li class="error">{{ msg }}</li>
                      }
                    </ul>
                  }
                </div>
              </div>
            }

            @if (sel.editor.kind === 'ReviewLoop') {
              <div class="inspector-section">
                <label class="field">
                  <span>Child workflow <span class="muted xsmall">(re-invoked every round; ports preview at latest version)</span></span>
                  <select [ngModel]="sel.editor.subflowKey ?? ''"
                          (ngModelChange)="onSubflowKeyChanged(sel.editor, $event)">
                    <option value="">(pick workflow)</option>
                    @for (wf of availableSubflowTargets(); track wf.key) {
                      <option [value]="wf.key">{{ subflowPickerLabel(wf) }}</option>
                    }
                  </select>
                  @if (sel.editor.subflowKey && sel.editor.subflowKey === workflowKey()) {
                    <cf-chip variant="err" dot>Self-reference — save will be rejected.</cf-chip>
                  }
                </label>
                <label class="field">
                  <span>Version <span class="muted xsmall">(blank = latest at save)</span></span>
                  <input type="number" min="1"
                         [ngModel]="sel.editor.subflowVersion ?? null"
                         (ngModelChange)="onSubflowVersionChanged(sel.editor, $event)" />
                </label>
                <label class="field">
                  <span>Max rounds <span class="muted xsmall">(1–10)</span></span>
                  <input type="number" min="1" max="10"
                         [ngModel]="sel.editor.reviewMaxRounds ?? null"
                         (ngModelChange)="onReviewMaxRoundsChanged(sel.editor, $event)" />
                  <span class="muted xsmall">
                    Number of produce→review→revise iterations before the loop gives up. If the child returns the loop decision on the final round, the loop exits the <code>Exhausted</code> port.
                  </span>
                </label>
                <label class="field">
                  <span>Loop decision <span class="muted xsmall">(default <code>Rejected</code>)</span></span>
                  <input type="text"
                         [ngModel]="sel.editor.loopDecision ?? ''"
                         (ngModelChange)="onLoopDecisionChanged(sel.editor, $event)"
                         placeholder="Rejected" />
                  <span class="muted xsmall">
                    The port name on the child workflow's terminal node that triggers another iteration. Matches case-sensitively against the child's effective port (routing-script result or decision kind). Change to <code>Answered</code>, etc., for non-standard loop triggers. <code>Failed</code> and <code>Escalated</code> are reserved.
                  </span>
                </label>
                @if (selectedSubflowDetail(); as detail) {
                  <div class="field">
                    <span class="field-label">Child workflow outline</span>
                    <div class="subflow-outline">
                      <div class="row-spread">
                        <strong class="mono small">{{ detail.name }}</strong>
                        <span class="muted xsmall">v{{ detail.version }} · {{ detail.nodes.length }} nodes</span>
                      </div>
                      <ul class="subflow-nodes">
                        @for (n of detail.nodes; track n.id) {
                          <li><cf-chip mono>{{ n.kind }}</cf-chip> <span class="mono xsmall">{{ labelForOutline(n) }}</span></li>
                        }
                      </ul>
                    </div>
                  </div>
                }
                <div class="field">
                  <span class="field-label">Output ports <span class="muted xsmall">(child terminal ports ∖ loopDecision, plus synthesized <code>Exhausted</code>)</span></span>
                  @if (sel.editor.outputPortNames.length > 0) {
                    <ul class="port-list mono">
                      @for (p of sel.editor.outputPortNames; track p) {
                        <li>
                          <code>{{ p }}</code>
                          @if (p === 'Exhausted') {
                            <span class="muted xsmall">(synthesized when round budget is reached)</span>
                          }
                        </li>
                      }
                      <li class="implicit">
                        <code>Failed</code> <span class="muted xsmall">(implicit, always wirable)</span>
                      </li>
                    </ul>
                  } @else {
                    <p class="muted xsmall">
                      Pick a child workflow above to see its terminal ports. ReviewLoop nodes inherit terminal ports from the child, exclude the configured <code>loopDecision</code> (which iterates instead of exiting), and add a synthesized <code>Exhausted</code> port.
                    </p>
                  }
                  @if (brokenEdgesForSelected().length > 0) {
                    <ul class="muted xsmall broken-edges">
                      @for (msg of brokenEdgesForSelected(); track msg) {
                        <li class="error">{{ msg }}</li>
                      }
                    </ul>
                  }
                </div>
                <p class="muted xsmall">
                  The child workflow can read <code>{{ '{{round}}' }}</code>, <code>{{ '{{maxRounds}}' }}</code>, <code>{{ '{{isLastRound}}' }}</code> from prompts and scripts.
                </p>
              </div>
            }

            @if (sel.editor.kind === 'Logic') {
              <div class="inspector-section">
                <div class="field">
                  <span class="field-label">Script (JavaScript)</span>
                  <cf-monaco-script-editor
                    class="script-editor"
                    [value]="sel.editor.outputScript ?? ''"
                    [markers]="scriptMarkers()"
                    [ambientLibs]="logicScriptAmbientLibs()"
                    snippetKind="logic-script"
                    [snippetInLoop]="selectedNodeInLoop()"
                    (valueChange)="onNodeScriptChanged(sel.editor, 'output', $event)"></cf-monaco-script-editor>
                </div>
                <div class="row">
                  <button type="button" (click)="validateNodeScript(sel.editor, 'output')">Validate</button>
                  @if (scriptValidationError()) {
                    <cf-chip variant="err" dot>{{ scriptValidationError() }}</cf-chip>
                  } @else if (scriptValidationOk()) {
                    <cf-chip variant="ok" dot>Script parses OK</cf-chip>
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

            @if (sel.editor.kind === 'Transform') {
              <div class="inspector-section">
                <div class="field">
                  <span class="field-label">Template (Scriban)</span>
                  <p class="muted xsmall">
                    Renders against <code>input.*</code>, <code>context.*</code>, and
                    <code>workflow.*</code>. Output flows on the synthesized <code>Out</code>
                    port; render errors route to <code>Failed</code>.
                  </p>
                  <cf-monaco-script-editor
                    class="script-editor"
                    language="plaintext"
                    [value]="sel.editor.template ?? ''"
                    [templateCompletion]="true"
                    (valueChange)="onTransformTemplateChanged(sel.editor, $event)"></cf-monaco-script-editor>
                </div>
                <div class="field">
                  <span class="field-label">Output type</span>
                  <div class="radio-row">
                    <label class="radio-option">
                      <input type="radio" name="transform-output-type"
                             [checked]="sel.editor.outputType === 'string'"
                             (change)="onTransformOutputTypeChanged(sel.editor, 'string')" />
                      <span><strong>String</strong> — rendered text becomes the artifact verbatim.</span>
                    </label>
                    <label class="radio-option">
                      <input type="radio" name="transform-output-type"
                             [checked]="sel.editor.outputType === 'json'"
                             (change)="onTransformOutputTypeChanged(sel.editor, 'json')" />
                      <span><strong>JSON</strong> — rendered text must parse as JSON; invalid output routes to <code>Failed</code>.</span>
                    </label>
                  </div>
                </div>

                <div class="field transform-preview-block">
                  <span class="field-label">Sample input <span class="muted xsmall">(JSON, exposed as <code>input.*</code>)</span></span>
                  <textarea class="mono transform-fixture-textarea" rows="4" spellcheck="false"
                            [value]="transformPreviewFixture()"
                            (input)="onTransformPreviewFixtureChanged($any($event.target).value)"
                            placeholder='{ "example": 1 }'></textarea>
                  @if (transformPreviewFixtureError(); as err) {
                    <cf-chip class="transform-preview-fixture-error" variant="err" dot>{{ err }}</cf-chip>
                  }
                </div>

                <div class="field transform-preview-block">
                  <div class="row-spread">
                    <span class="field-label" style="margin-bottom: 0;">Preview</span>
                    @if (transformPreviewPending()) {
                      <span class="muted xsmall">rendering…</span>
                    }
                  </div>
                  @if (transformPreviewError(); as err) {
                    <cf-chip variant="err" dot>Render error — would route to <code>Failed</code>:</cf-chip>
                    <pre class="transform-preview-output error">{{ err }}</pre>
                  } @else if (transformPreviewRendered() === null) {
                    <p class="muted xsmall">Enter a template above to see the rendered output.</p>
                  } @else if (sel.editor.outputType === 'json') {
                    @if (transformPreviewJsonParseError(); as parseErr) {
                      <cf-chip variant="err" dot>JSON parse error — would route to <code>Failed</code>:</cf-chip>
                      <pre class="transform-preview-output error">{{ parseErr }}</pre>
                      <p class="muted xsmall transform-preview-subhead">Raw render:</p>
                      <pre class="transform-preview-output">{{ transformPreviewRendered() }}</pre>
                    } @else {
                      <p class="muted xsmall transform-preview-subhead">Parsed JSON:</p>
                      <pre class="transform-preview-output">{{ transformPreviewParsedPretty() }}</pre>
                    }
                  } @else {
                    <pre class="transform-preview-output">{{ transformPreviewRendered() }}</pre>
                  }
                </div>
              </div>
            }

            @if (sel.editor.kind === 'Swarm') {
              <div class="inspector-section">
                <p class="muted xsmall">
                  A Swarm node fans out to <code>n</code> contributor agents under the chosen protocol, then
                  hands their drafts to a synthesizer agent that emits the node's terminal output. Swarm nodes
                  are <strong>non-replayable</strong> — replay re-runs the whole node fresh.
                </p>

                <label class="field">
                  <span>Protocol</span>
                  <select [ngModel]="sel.editor.swarmProtocol ?? 'Sequential'"
                          (ngModelChange)="onSwarmProtocolChanged(sel.editor, $event)">
                    <option value="Sequential">Sequential — each contributor sees prior drafts</option>
                    <option value="Coordinator">Coordinator — agent 0 plans + assigns roles, contributors run in parallel</option>
                  </select>
                </label>

                <label class="field">
                  <span>n — contributor count <span class="muted xsmall">(1–16)</span></span>
                  <input type="number" min="1" max="16"
                         [ngModel]="sel.editor.swarmN ?? null"
                         (ngModelChange)="onSwarmNChanged(sel.editor, $event)" />
                </label>

                <div class="field">
                  <span class="field-label">Contributor agent <span class="muted xsmall">(invoked n times)</span></span>
                  <select [ngModel]="sel.editor.contributorAgentKey ?? ''"
                          (ngModelChange)="onSwarmAgentKeyChanged(sel.editor, 'contributor', $event)">
                    <option value="">(pick agent)</option>
                    @for (agent of agents(); track agent.key) {
                      <option [value]="agent.key">{{ agent.key }}</option>
                    }
                  </select>
                  <input type="number" min="1" placeholder="version (required)"
                         [ngModel]="sel.editor.contributorAgentVersion ?? null"
                         (ngModelChange)="onSwarmAgentVersionChanged(sel.editor, 'contributor', $event)" />
                  <span class="muted xsmall">Version must be pinned. Latest-version resolution happens at parent-workflow save time.</span>
                </div>

                <div class="field">
                  <span class="field-label">Synthesizer agent <span class="muted xsmall">(emits the node's terminal output)</span></span>
                  <select [ngModel]="sel.editor.synthesizerAgentKey ?? ''"
                          (ngModelChange)="onSwarmAgentKeyChanged(sel.editor, 'synthesizer', $event)">
                    <option value="">(pick agent)</option>
                    @for (agent of agents(); track agent.key) {
                      <option [value]="agent.key">{{ agent.key }}</option>
                    }
                  </select>
                  <input type="number" min="1" placeholder="version (required)"
                         [ngModel]="sel.editor.synthesizerAgentVersion ?? null"
                         (ngModelChange)="onSwarmAgentVersionChanged(sel.editor, 'synthesizer', $event)" />
                </div>

                @if (sel.editor.swarmProtocol === 'Coordinator') {
                  <div class="field">
                    <span class="field-label">Coordinator agent <span class="muted xsmall">(plans + assigns; Coordinator-only)</span></span>
                    <select [ngModel]="sel.editor.coordinatorAgentKey ?? ''"
                            (ngModelChange)="onSwarmAgentKeyChanged(sel.editor, 'coordinator', $event)">
                      <option value="">(pick agent)</option>
                      @for (agent of agents(); track agent.key) {
                        <option [value]="agent.key">{{ agent.key }}</option>
                      }
                    </select>
                    <input type="number" min="1" placeholder="version (required)"
                           [ngModel]="sel.editor.coordinatorAgentVersion ?? null"
                           (ngModelChange)="onSwarmAgentVersionChanged(sel.editor, 'coordinator', $event)" />
                  </div>
                }

                <label class="field">
                  <span>Token budget <span class="muted xsmall">(optional; > 0 when set, blank for unbounded)</span></span>
                  <input type="number" min="1"
                         [ngModel]="sel.editor.swarmTokenBudget ?? null"
                         (ngModelChange)="onSwarmTokenBudgetChanged(sel.editor, $event)" />
                </label>

                <div class="field">
                  <span class="field-label">Output ports <span class="muted xsmall">(derived from synthesizer's declared outputs)</span></span>
                  @if (derivedSwarmPortRows().length > 0) {
                    <ul class="port-list mono">
                      @for (p of derivedSwarmPortRows(); track p.name + ':' + p.status) {
                        <li [attr.data-port-status]="p.status">
                          <code>{{ p.name }}</code>
                          @if (p.status === 'stale') {
                            <cf-chip variant="err" mono title="Wired on this node but the synthesizer agent can't submit it. Synthesizer reaching this port at runtime would crash.">stale (not declared by synthesizer)</cf-chip>
                          } @else if (p.status === 'missing') {
                            <cf-chip variant="warn" mono title="Declared by the synthesizer agent but missing from this node. Submissions to this port would route nowhere (dead branch).">missing on node</cf-chip>
                          }
                        </li>
                      }
                      <li class="implicit">
                        <code>Failed</code> <span class="muted xsmall">(implicit, always wirable)</span>
                      </li>
                    </ul>
                    @if (selectedSwarmHasPortDrift()) {
                      <div class="row" style="gap: 8px; align-items: center; margin-top: 8px">
                        <button type="button" cf-button variant="default" size="sm" (click)="syncPortsFromSynthesizer(sel.editor)">
                          Sync from synthesizer
                        </button>
                        <span class="muted xsmall">Replaces this node's ports with the synthesizer agent's declared outputs. Wires on still-declared ports are preserved.</span>
                      </div>
                    }
                  } @else if (sel.editor.outputPortNames.length > 0) {
                    <ul class="port-list mono">
                      @for (p of sel.editor.outputPortNames; track p) {
                        <li><code>{{ p }}</code></li>
                      }
                      <li class="implicit">
                        <code>Failed</code> <span class="muted xsmall">(implicit, always wirable)</span>
                      </li>
                    </ul>
                    <p class="muted xsmall">
                      @if (sel.editor.synthesizerAgentKey) {
                        Synthesizer agent doc still loading — drift badges will appear once the agent's outputs are known.
                      } @else {
                        Pick a synthesizer agent above to derive ports from its declared outputs. Defaults to <code>Synthesized</code> until then.
                      }
                    </p>
                  }
                </div>
              </div>
            }

            <div class="inspector-section dataflow-section">
              <div class="row-spread">
                <div class="panel-title-inline">Data flow</div>
                @if (dataflowVersion(); as v) {
                  <span class="muted xsmall" [class.dirty]="dataflowDirty()">
                    based on saved v{{ v }}@if (dataflowDirty()) { · unsaved edits }
                  </span>
                }
              </div>
              @if (dataflowLoading()) {
                <p class="muted xsmall">Analyzing data flow…</p>
              } @else if (dataflowError(); as err) {
                <cf-chip variant="err" dot>{{ err }}</cf-chip>
              } @else if (!dataflowSnapshot()) {
                <p class="muted xsmall">Save this workflow to enable data-flow analysis.</p>
              } @else if (selectedNodeDataflow(); as scope) {
                <div class="df-group">
                  <div class="df-group-title">
                    Workflow variables in scope
                    <span class="muted xsmall">— what upstream nodes have written</span>
                  </div>
                  @if (scope.workflowVariables.length === 0 && selectedNodeUndeclaredReads().workflow.length === 0) {
                    <p class="muted xsmall">No upstream <code>setWorkflow</code> writes reach this node.</p>
                  } @else {
                    <ul class="df-list">
                      @for (v of scope.workflowVariables; track v.key) {
                        <li>
                          <code class="mono">{{ v.key }}</code>
                          <cf-chip mono
                                [variant]="v.confidence === 'Definite' ? 'ok' : 'warn'"
                                [title]="v.confidence === 'Definite' ? 'Every upstream path writes this key.' : 'At least one upstream path writes this key — others may not.'">
                            {{ v.confidence === 'Definite' ? 'definite' : 'conditional' }}
                          </cf-chip>
                          @if (v.sources.length > 0) {
                            <span class="muted xsmall">from</span>
                            @for (src of v.sources; track src.nodeId + ':' + src.scriptKind; let last = $last) {
                              <button type="button" class="link mono xsmall" (click)="navigateToSourceNode(src.nodeId)"
                                      [title]="'Navigate to ' + labelForSource(src.nodeId)">
                                {{ labelForSource(src.nodeId) }}<span class="muted">.{{ src.scriptKind }}</span></button>@if (!last) {<span class="muted">,</span>}
                            }
                          }
                        </li>
                      }
                      @for (k of selectedNodeUndeclaredReads().workflow; track k) {
                        <li class="undeclared">
                          <code class="mono">{{ k }}</code>
                          <cf-chip variant="err" mono title="This key is referenced by this node but no upstream node writes it.">no writer found</cf-chip>
                        </li>
                      }
                    </ul>
                  }
                </div>

                <div class="df-group">
                  <div class="df-group-title">
                    Context keys in scope
                    <span class="muted xsmall">— per-saga, written via <code>setContext</code></span>
                  </div>
                  @if (scope.contextKeys.length === 0 && selectedNodeUndeclaredReads().context.length === 0) {
                    <p class="muted xsmall">No upstream <code>setContext</code> writes reach this node.</p>
                  } @else {
                    <ul class="df-list">
                      @for (v of scope.contextKeys; track v.key) {
                        <li>
                          <code class="mono">{{ v.key }}</code>
                          <cf-chip mono [variant]="v.confidence === 'Definite' ? 'ok' : 'warn'">
                            {{ v.confidence === 'Definite' ? 'definite' : 'conditional' }}
                          </cf-chip>
                          @if (v.sources.length > 0) {
                            <span class="muted xsmall">from</span>
                            @for (src of v.sources; track src.nodeId + ':' + src.scriptKind; let last = $last) {
                              <button type="button" class="link mono xsmall" (click)="navigateToSourceNode(src.nodeId)"
                                      [title]="'Navigate to ' + labelForSource(src.nodeId)">
                                {{ labelForSource(src.nodeId) }}<span class="muted">.{{ src.scriptKind }}</span></button>@if (!last) {<span class="muted">,</span>}
                            }
                          }
                        </li>
                      }
                      @for (k of selectedNodeUndeclaredReads().context; track k) {
                        <li class="undeclared">
                          <code class="mono">{{ k }}</code>
                          <cf-chip variant="err" mono title="This key is referenced by this node but no upstream node writes it.">no writer found</cf-chip>
                        </li>
                      }
                    </ul>
                  }
                </div>

                <div class="df-group">
                  <div class="df-group-title">Expected input artifact</div>
                  @if (scope.inputSource) {
                    <p class="xsmall">
                      from
                      <button type="button" class="link mono xsmall" (click)="navigateToSourceNode(scope.inputSource.nodeId)"
                              [title]="'Navigate to ' + labelForSource(scope.inputSource.nodeId)">
                        {{ labelForSource(scope.inputSource.nodeId) }}<span class="muted">.{{ scope.inputSource.port }}</span>
                      </button>
                    </p>
                  } @else {
                    <p class="muted xsmall">
                      @if (sel.editor.kind === 'Start') {
                        Start nodes receive the workflow input directly.
                      } @else {
                        No upstream node found — wire an inbound edge.
                      }
                    </p>
                  }
                </div>

                @if (scope.loopBindings; as lb) {
                  <div class="df-group">
                    <div class="df-group-title">
                      Loop bindings
                      <span class="muted xsmall">— available as <code>{{ '{{round}}' }}</code> / <code>{{ '{{maxRounds}}' }}</code> / <code>{{ '{{isLastRound}}' }}</code></span>
                    </div>
                    <ul class="df-list">
                      <li>
                        <code class="mono">round</code>
                        @if (lb.staticRound !== null) {
                          <cf-chip variant="ok" mono>= {{ lb.staticRound }}</cf-chip>
                        } @else {
                          <cf-chip mono>1..{{ lb.maxRounds }}</cf-chip>
                        }
                      </li>
                      <li><code class="mono">maxRounds</code> <cf-chip variant="ok" mono>= {{ lb.maxRounds }}</cf-chip></li>
                      <li><code class="mono">isLastRound</code> <span class="muted xsmall">true on round {{ lb.maxRounds }}</span></li>
                    </ul>
                  </div>
                }

                @if (selectedNodeAutoInjectedPartials().length > 0) {
                  <div class="df-group">
                    <div class="df-group-title">
                      Auto-injected partials
                      <span class="muted xsmall">— added by the runtime; pin explicitly to opt out</span>
                    </div>
                    <ul class="df-list">
                      @for (p of selectedNodeAutoInjectedPartials(); track p) {
                        <li>
                          <code class="mono">{{ p }}</code>
                          <cf-chip mono>[auto-injected]</cf-chip>
                        </li>
                      }
                    </ul>
                  </div>
                }
              } @else {
                <p class="muted xsmall">No data-flow scope for this node — its persistence id isn't in the saved snapshot. Save the workflow to refresh.</p>
              }
            </div>
          } @else if (selectedConnection(); as sel) {
            <div class="inspector-section">
              <div class="row-spread">
                <div class="inspector-kind connection">Wire</div>
                <button type="button" class="danger small" (click)="removeSelectedConnection()">Delete wire</button>
              </div>
              <div class="muted xsmall">
                <code class="mono">{{ connectionSummary(sel.editor) }}</code>
              </div>
              @if (selectedConnectionBackedge(); as bk) {
                <div class="backedge-card" [class.dismissed]="bk.intentional">
                  <div class="row-spread">
                    <cf-chip variant="warn" dot>Backedge</cf-chip>
                    @if (bk.intentional) {
                      <span class="muted xsmall">Marked intentional</span>
                    }
                  </div>
                  <p class="muted xsmall">
                    This edge creates a cycle. ReviewLoop iteration handles loops natively, so
                    accidental backedges are usually a mistake. Confirm intent or remove the
                    edge.
                  </p>
                  @if (bk.cycle.length > 0) {
                    <div class="df-group">
                      <div class="df-group-title">Cycle</div>
                      <div class="muted xsmall mono">{{ bk.cycle.join(' → ') }} → {{ bk.cycle[0] }}</div>
                    </div>
                  }
                  <label class="field row-inline">
                    <input type="checkbox"
                           [checked]="bk.intentional"
                           (change)="toggleSelectedConnectionIntentional($any($event.target).checked)" />
                    <span>Yes, intentional — dismiss this warning</span>
                  </label>
                </div>
              }
            </div>
          } @else {
            <div class="inspector-section">
              <p class="muted xsmall">
                Select a node to edit its settings. Drag between port handles to wire nodes. Select a wire and press Delete or Backspace to remove it.
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
                @for (input of inputs(); track i; let i = $index) {
                  <div class="input-card">
                    <div class="row-spread">
                      <strong class="mono">{{ input.key }}</strong>
                      @if (isDefaultInput(input)) {
                        <cf-chip mono>required · start</cf-chip>
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

    @if (contextMenu(); as menu) {
      <cf-node-context-menu
        [open]="true"
        [x]="menu.x"
        [y]="menu.y"
        [items]="menu.items"
        (pickItem)="onContextMenuPick($event)"
        (close)="closeContextMenu()"></cf-node-context-menu>
    }

    <cf-agent-in-place-edit-dialog
      [target]="dialogs.editTarget()"
      [suppressWarning]="dialogs.warningSuppressed()"
      (close)="dialogs.closeEditInPlace()"
      (saved)="onEditInPlaceSaved($event)"
      (warningSuppressed)="dialogs.suppressWarning()"></cf-agent-in-place-edit-dialog>

    <cf-publish-fork-dialog
      [target]="dialogs.publishTarget()"
      (close)="dialogs.closePublishFork()"
      (published)="onForkPublished($event)"></cf-publish-fork-dialog>

    <cf-version-update-dialog
      [target]="dialogs.versionUpdateTarget()"
      (confirmed)="onVersionUpdateConfirmed($event)"
      (cancelled)="dialogs.cancelVersionUpdate()"></cf-version-update-dialog>

    <cf-workflow-version-history-dialog
      [open]="dialogs.historyOpen()"
      [workflowKey]="workflowKey()"
      (closed)="dialogs.closeHistory()"></cf-workflow-version-history-dialog>
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
      background: var(--surface);
      border-left: 1px solid var(--border);
      overflow-y: auto;
      display: flex;
      flex-direction: column;
    }
    .sidebar-section {
      padding: 1rem;
      border-bottom: 1px solid var(--border);
    }
    .sidebar-section:last-child { border-bottom: none; flex: 1; }
    .canvas-wrapper { display: flex; flex-direction: column; min-width: 0; }
    .canvas {
      flex: 1;
      min-height: 0;
      background: var(--bg);
      background-image: radial-gradient(circle at center, color-mix(in oklab, var(--muted) 22%, transparent) 1px, transparent 1.5px);
      background-size: 22px 22px;
      position: relative;
      overflow: hidden;
    }
    [data-theme="light"] .canvas {
      background-image: radial-gradient(circle at center, color-mix(in oklab, var(--muted) 25%, transparent) 1px, transparent 1.5px);
    }
    .port-reference-drawer {
      max-height: 0;
      overflow: hidden;
      border-top: 1px solid transparent;
      background: var(--surface);
      transition: max-height 180ms ease, border-color 180ms ease;
    }
    .port-reference-drawer.open {
      max-height: 260px;
      border-top-color: var(--border);
    }
    .port-reference-head {
      padding: 0.75rem 1rem 0.5rem;
      border-bottom: 1px solid var(--border);
    }
    .port-reference-body {
      display: grid;
      grid-auto-flow: column;
      grid-auto-columns: minmax(240px, 1fr);
      gap: 0.75rem;
      overflow-x: auto;
      padding: 0.75rem 1rem 1rem;
      min-height: 132px;
    }
    .port-reference-row {
      border: 1px solid var(--border);
      border-radius: 6px;
      background: rgba(255, 255, 255, 0.02);
      min-width: 0;
      display: flex;
      flex-direction: column;
    }
    .port-reference-row.blank {
      background: transparent;
      border-style: dashed;
      opacity: 0.7;
    }
    .port-reference-meta {
      display: flex;
      align-items: center;
      gap: 0.45rem;
      min-height: 34px;
      padding: 0.45rem 0.55rem;
      border-bottom: 1px solid var(--border);
    }
    .port-reference-row.blank .port-reference-meta { border-bottom: none; }
    .port-name {
      font-size: 0.78rem;
      font-weight: 600;
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .port-reference-row pre {
      margin: 0;
      padding: 0.65rem;
      max-height: 150px;
      overflow: auto;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 0.75rem;
      line-height: 1.45;
      color: var(--text);
    }
    .toolbar {
      display: flex;
      flex-direction: column;
      gap: 0.6rem;
      padding: 0.65rem 1rem 0.75rem;
      border-bottom: 1px solid var(--border);
    }
    .toolbar-actions {
      display: flex;
      gap: 0.4rem;
      align-items: center;
      justify-content: flex-end;
      flex-wrap: wrap;
    }
    .toolbar-fields {
      display: flex;
      gap: 0.5rem;
      flex-wrap: wrap;
      align-items: flex-end;
      min-width: 0;
    }
    .tb-field {
      display: flex;
      flex-direction: column;
      gap: 0.2rem;
      min-width: 0;
      flex: 1 1 110px;
    }
    .tb-field.tb-narrow { flex: 0 1 100px; min-width: 0; max-width: 110px; }
    .tb-field.tb-tags { flex: 2 1 180px; min-width: 0; }
    .tb-label {
      font-size: 0.72rem;
      font-weight: 500;
      color: var(--muted);
      letter-spacing: 0.01em;
    }
    .tb-hint {
      color: var(--muted);
      font-weight: 400;
      opacity: 0.75;
      margin-left: 0.25rem;
    }
    .toolbar input, .toolbar select {
      padding: 0.4rem 0.55rem;
      border-radius: 6px;
      border: 1px solid var(--border);
      background: var(--surface);
      color: inherit;
      height: 32px;
      box-sizing: border-box;
    }
    .toolbar input:disabled { opacity: 0.65; cursor: not-allowed; }
    .palette {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 0.4rem;
    }
    .palette-item {
      display: block;
      padding: 0.5rem 0.6rem;
      border-radius: 4px;
      border: 1px solid var(--border);
      background: rgba(255, 255, 255, 0.03);
      color: var(--text);
      text-align: left;
      font-weight: 500;
      cursor: grab;
    }
    .palette-item:hover { border-color: var(--accent); background: rgba(255, 255, 255, 0.06); }
    .palette-item.start { border-left: 4px solid #3fb950; }
    .palette-item.agent { border-left: 4px solid #58a6ff; }
    .palette-item.logic { border-left: 4px solid #d29922; }
    .palette-item.transform { border-left: 4px solid #06d6a0; }
    .palette-item.hitl { border-left: 4px solid #bc8cff; }
    .palette-item.escalation { border-left: 4px solid #f85149; }
    .palette-item.subflow { border-left: 4px solid #2ea3f2; }
    .palette-item.reviewloop { border-left: 4px solid #f5a623; }
    .palette-item.swarm { border-left: 4px solid #ff7eb6; }
    .panel-title {
      font-size: 0.75rem;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--muted);
      margin-bottom: 0.75rem;
    }
    .panel-title-inline {
      font-size: 0.85rem;
      font-weight: 600;
      margin-bottom: 0.4rem;
    }
    .inspector-section {
      border-top: 1px solid var(--border);
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
      color: var(--muted);
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
      border: 1px solid var(--border);
      background: var(--surface-2);
      color: inherit;
      font-family: inherit;
    }
    .field textarea.mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 0.8rem; }
    .field-label {
      display: block;
      font-size: 0.75rem;
      color: var(--muted);
      margin-bottom: 0.25rem;
    }
    .script-editor {
      display: block;
      min-height: 320px;
    }
    .inputs-list { display: flex; flex-direction: column; gap: 0.75rem; margin-bottom: 0.75rem; }
    .input-card {
      padding: 0.75rem;
      border: 1px solid var(--border);
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
    .inspector-kind.transform { background: rgba(6, 214, 160, 0.2); color: #06d6a0; }
    .inspector-kind.hitl { background: rgba(188, 140, 255, 0.2); color: #bc8cff; }
    .inspector-kind.escalation { background: rgba(248, 81, 73, 0.2); color: #f85149; }
    .inspector-kind.subflow { background: rgba(46, 163, 242, 0.2); color: #2ea3f2; }
    .inspector-kind.connection { background: rgba(255, 209, 102, 0.16); color: #ffd166; }
    .subflow-outline {
      padding: 0.5rem;
      border: 1px solid var(--border);
      border-radius: 4px;
      background: rgba(255, 255, 255, 0.02);
    }
    .subflow-nodes {
      list-style: none;
      padding: 0;
      margin: 0.4rem 0 0 0;
      display: flex;
      flex-direction: column;
      gap: 0.2rem;
    }
    .subflow-nodes li { display: flex; gap: 0.4rem; align-items: center; }
    .row-spread { display: flex; justify-content: space-between; align-items: center; gap: 0.5rem; margin-bottom: 0.4rem; }
    .row { display: flex; gap: 0.5rem; align-items: center; margin-bottom: 0.5rem; flex-wrap: wrap; }
    .radio-row { display: flex; flex-direction: column; gap: 0.3rem; }
    .radio-option { display: flex; gap: 0.5rem; align-items: flex-start; cursor: pointer; font-size: 0.8rem; }
    .radio-option input[type="radio"] { margin-top: 0.15rem; }
    .transform-fixture-textarea {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 0.78rem;
      line-height: 1.35;
      resize: vertical;
      min-height: 4.5rem;
    }
    .transform-preview-output {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 0.78rem;
      line-height: 1.35;
      white-space: pre-wrap;
      word-break: break-word;
      padding: 0.5rem 0.6rem;
      border: 1px solid var(--border);
      border-radius: 4px;
      background: var(--surface-2);
      max-height: 18rem;
      overflow: auto;
      margin: 0;
    }
    .transform-preview-output.error {
      color: #f85149;
      border-color: rgba(248, 81, 73, 0.4);
      background: rgba(248, 81, 73, 0.05);
    }
    .transform-preview-subhead { margin: 0.4rem 0 0.2rem 0; }
    .transform-preview-fixture-error { margin-top: 0.3rem; display: inline-block; }
    .mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 0.75rem; }
    .muted { color: var(--muted); }
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
      border: 1px solid var(--border);
      background: var(--surface);
      cursor: pointer;
      color: inherit;
    }
    .icon-button:hover { border-color: #f85149; color: #f85149; }
    .dataflow-section .row-spread { margin-bottom: 0.5rem; }
    .dataflow-section .dirty { color: #f5b84c; }
    .df-group { margin-bottom: 0.6rem; }
    .df-group:last-child { margin-bottom: 0; }
    .df-group-title {
      font-size: 0.72rem;
      color: var(--muted);
      margin-bottom: 0.25rem;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .df-group-title .muted { text-transform: none; letter-spacing: 0; }
    .df-list {
      list-style: none;
      padding: 0;
      margin: 0;
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
    }
    .df-list > li {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.3rem;
      font-size: 0.75rem;
    }
    .df-list > li.undeclared code { color: #f85149; }
    button.link {
      background: transparent;
      border: none;
      padding: 0;
      color: #58a6ff;
      cursor: pointer;
      text-decoration: underline dotted;
      text-underline-offset: 2px;
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 0.72rem;
    }
    button.link:hover { color: #ffd166; }
    .backedge-card {
      margin-top: 0.75rem;
      padding: 0.5rem 0.6rem;
      border: 1px dashed #f5b84c;
      border-radius: 4px;
      background: rgba(245, 184, 76, 0.06);
    }
    .backedge-card.dismissed {
      border-style: solid;
      border-color: var(--border);
      background: rgba(255, 255, 255, 0.02);
    }
    .backedge-card .row-spread { margin-bottom: 0.4rem; }
    .backedge-card p { margin: 0 0 0.4rem 0; }
    .backedge-card .field.row-inline { margin-top: 0.4rem; gap: 0.4rem; }
  `]
})
export class WorkflowCanvasComponent implements AfterViewInit, OnDestroy {
  private readonly api = inject(WorkflowsApi);
  private readonly agentsApi = inject(AgentsApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageContext = inject(PageContextService);
  readonly dialogs = inject(WorkflowCanvasDialogOrchestrator);

  constructor() {
    // Keep the assistant sidebar's PageContext in sync with the workflow key + selected node.
    // The canvas's selectedNodeId signal updates on canvas click / delete / etc; this effect
    // re-registers each change so chip rules ("workflow-editor + selectedNodeId") stay accurate.
    // For new workflows (no key yet) we leave registration to the route fallback (homepage scope).
    effect(() => {
      const key = this.workflowKey();
      if (!key) return;
      const selected = this.selectedNodeId();
      this.pageContext.set({
        kind: 'workflow-editor',
        workflowId: key,
        selectedNodeId: selected ?? undefined,
      });
    });
  }

  @ViewChild('canvasHost', { static: true }) canvasHost!: ElementRef<HTMLDivElement>;

  private editor?: NodeEditor<WorkflowSchemes>;
  private area?: AreaPlugin<WorkflowSchemes, WorkflowAreaExtra>;
  private readonly connectionElements = new Map<string, { element: HTMLElement; onClick: (event: MouseEvent) => void }>();
  private readonly selectedNodeId = signal<string | null>(null);
  private readonly selectedConnectionId = signal<string | null>(null);
  private readonly portsRevision = signal(0);

  readonly agents = signal<AgentSummary[]>([]);
  readonly workflows = signal<WorkflowSummary[]>([]);
  readonly workflowKey = signal<string>('');
  readonly workflowName = signal<string>('');
  readonly maxRoundsPerRound = signal<number>(3);
  readonly workflowCategory = signal<WorkflowCategory>('Workflow');
  readonly workflowTags = signal<string[]>([]);
  readonly inputs = signal<WorkflowInput[]>([defaultStartInput()]);

  readonly categoryOptions = WORKFLOW_CATEGORIES;
  readonly maxTags = MAX_WORKFLOW_TAGS;

  readonly tagSuggestions = computed(() => {
    const seen = new Set<string>();
    const result: string[] = [];
    for (const wf of this.workflows()) {
      for (const tag of wf.tags ?? []) {
        const normalized = tag.trim();
        if (!normalized) continue;
        const key = normalized.toLowerCase();
        if (seen.has(key)) continue;
        seen.add(key);
        result.push(normalized);
      }
    }
    return result.sort((a, b) => a.localeCompare(b));
  });
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly statusMessage = signal<string | null>(null);
  readonly hasExistingKey = signal(false);
  readonly scriptValidationError = signal<string | null>(null);
  readonly scriptValidationOk = signal(false);
  readonly scriptMarkers = signal<MonacoMarker[]>([]);
  readonly inputScriptValidationError = signal<string | null>(null);
  readonly inputScriptValidationOk = signal(false);
  readonly inputScriptMarkers = signal<MonacoMarker[]>([]);
  readonly selectedSubflowDetail = signal<import('../../../core/models').WorkflowDetail | null>(null);
  readonly contextMenu = signal<NodeContextMenuState | null>(null);
  readonly selectedAgentDocs = signal<SelectedAgentDocs | null>(null);
  readonly selectedAgentDocsLoading = signal(false);
  readonly selectedAgentDocsError = signal<string | null>(null);
  // Swarm-only: docs for the synthesizer agent. Kept separate from selectedAgentDocs so
  // the contributor / coordinator slots can't clobber the cache the AGENT_BEARING_KINDS
  // path relies on, and so the synthesizer's outputs drive the Swarm node's terminal ports
  // independently of any contributor lookups.
  readonly selectedSynthesizerDocs = signal<SelectedAgentDocs | null>(null);
  readonly selectedSynthesizerDocsLoading = signal(false);
  readonly selectedSynthesizerDocsError = signal<string | null>(null);

  /** VZ1: F2 dataflow snapshot for the workflow's current saved version. Null until first
   *  load completes (or for unsaved new workflows). The snapshot is the on-disk truth, so
   *  unsaved canvas edits aren't reflected until the next save round-trips through F2. */
  readonly dataflowSnapshot = signal<WorkflowDataflowSnapshot | null>(null);
  readonly dataflowVersion = signal<number | null>(null);
  readonly dataflowLoading = signal(false);
  readonly dataflowError = signal<string | null>(null);
  readonly dataflowDirty = signal(false);

  /** TN-6: Transform-node live preview state. Per-component (not per-node) so the fixture text
   *  carries across selections when the author is iterating, but reset when the selection
   *  switches off Transform. The preview re-renders on debounced template / outputType /
   *  fixture changes; client-side fixture-JSON validation gates the call to avoid round-trips
   *  that the backend would just reject as malformed input. */
  readonly transformPreviewFixture = signal<string>('{}');
  readonly transformPreviewRendered = signal<string | null>(null);
  readonly transformPreviewParsedPretty = signal<string | null>(null);
  readonly transformPreviewJsonParseError = signal<string | null>(null);
  readonly transformPreviewError = signal<string | null>(null);
  readonly transformPreviewPending = signal(false);
  readonly transformPreviewFixtureError = signal<string | null>(null);
  private transformPreviewTimer?: ReturnType<typeof setTimeout>;
  private transformPreviewSelectedNodeId: string | null = null;

  /** VZ5: when true, every node renders its implicit Failed port (faded) so authors can wire
   *  recovery edges. When false (default), only Failed ports that already have an edge are
   *  rendered — keeps the canvas uncluttered. Per-author preference, not persisted on the
   *  workflow JSON (lives in localStorage so it survives page reloads). */
  readonly showImplicitFailed = signal(false);
  private static readonly ShowImplicitFailedStorageKey = 'cf:editor:showImplicitFailed';

  /** VZ6: per-candidate terminal-port cache for the Subflow / ReviewLoop pickers. Keyed by
   *  workflow key, value is the candidate's terminal port list at its latest version (the
   *  picker shows latest; once an author pins a specific version the inspector loads the
   *  effective ports separately). Populated eagerly when the workflows list loads. */
  readonly terminalPortsByKey = signal<Record<string, string[]>>({});

  /** VZ7: backedge detection — connection ids of edges whose target is reachable from the
   *  source via the forward graph (DFS-tree backedges). Mirrors the V6 BackedgeRule. */
  private readonly backedgeIds = new Set<string>();
  private readonly backedgeCycleByConnId = new Map<string, string[]>();
  private readonly backedgeRevision = signal(0);

  readonly availableSubflowTargets = computed<WorkflowSummary[]>(() => {
    const currentKey = this.workflowKey().trim();
    return this.workflows().filter(w => w.key !== currentKey);
  });

  readonly selectedNode = computed<SelectedNode | null>(() => {
    const id = this.selectedNodeId();
    if (!id || !this.editor) return null;
    const node = this.editor.getNode(id) as WorkflowEditorNode | undefined;
    return node ? { editor: node } : null;
  });

  readonly selectedConnection = computed<SelectedConnection | null>(() => {
    const id = this.selectedConnectionId();
    if (!id || !this.editor) return null;
    const connection = this.editor.getConnection(id) as WorkflowEditorConnection | undefined;
    return connection ? { editor: connection } : null;
  });

  /** VZ7: backedge metadata for the currently-selected connection. Null if the connection
   *  isn't a backedge. Re-evaluates on every backedge re-analysis or intentional toggle. */
  readonly selectedConnectionBackedge = computed<{ intentional: boolean; cycle: string[] } | null>(() => {
    this.backedgeRevision();
    const id = this.selectedConnectionId();
    if (!id || !this.editor) return null;
    if (!this.backedgeIds.has(id)) return null;
    const connection = this.editor.getConnection(id) as WorkflowEditorConnection | undefined;
    if (!connection) return null;
    return {
      intentional: connection.intentionalBackedge,
      cycle: this.backedgeCycleByConnId.get(id) ?? []
    };
  });

  readonly outputPortsText = computed(() => {
    // Read portsRevision so the computed recomputes when ports mutate.
    this.portsRevision();
    const sel = this.selectedNode();
    if (!sel) return '';
    return sel.editor.outputPortNames.join('\n');
  });

  readonly selectedPortReferences = computed<PortReferenceRow[]>(() => {
    // Read portsRevision so the computed recomputes when ports mutate.
    this.portsRevision();
    const sel = this.selectedNode();
    if (!sel) return [];

    const docs = this.selectedAgentDocs();
    const outputs = Array.isArray(docs?.config.outputs) ? docs.config.outputs : [];
    const templates = this.asTemplateMap(docs?.config.decisionOutputTemplates);

    return sel.editor.outputPortNames.map(port => {
      const output = outputs.find(o => o.kind === port);
      if (output && output.payloadExample !== null && output.payloadExample !== undefined) {
        const content = this.formatPayloadExample(output.payloadExample);
        if (content.length > 0) return { port, source: 'payload', content };
      }

      const template = templates[port]?.trim();
      if (template) return { port, source: 'template', content: template };

      return { port, source: 'blank', content: '' };
    });
  });

  /** Per-port rows for the inspector's "derived from agent outputs" listing.
   *  - `status: 'ok'` — port present on the node and declared by the agent.
   *  - `status: 'stale'` (red) — port wired on the node but the agent can no longer submit it.
   *  - `status: 'missing'` (orange) — port declared by the agent but not yet on the node. */
  readonly derivedPortRows = computed<DerivedPortRow[]>(() => {
    this.portsRevision();
    const sel = this.selectedNode();
    if (!sel) return [];
    if (!AGENT_BEARING_KINDS.has(sel.editor.kind)) return [];

    return derivePortRows(sel.editor, this.selectedAgentDocs()?.config);
  });

  /** True when the selected agent-bearing node's ports drift from its pinned agent's declared
   *  outputs in either direction (stale or missing). Drives the "Sync from agent" affordance. */
  readonly selectedNodeHasPortDrift = computed(() => {
    return hasPortDrift(this.derivedPortRows());
  });

  /** Swarm-only counterpart to derivedPortRows. The Swarm node's terminal ports follow the
   *  *synthesizer* agent's declared outputs (contributor + coordinator outputs are internal
   *  to the swarm and don't appear on the node). Until a synthesizer is picked, the row set
   *  is empty and the existing default `Synthesized` port stays in place. */
  readonly derivedSwarmPortRows = computed<DerivedPortRow[]>(() => {
    this.portsRevision();
    const sel = this.selectedNode();
    if (!sel || sel.editor.kind !== 'Swarm') return [];

    return derivePortRows(sel.editor, this.selectedSynthesizerDocs()?.config);
  });

  readonly selectedSwarmHasPortDrift = computed(() => {
    return hasPortDrift(this.derivedSwarmPortRows());
  });

  /** E1: ambient TS declarations for the input-script editor on the selected node.
   *  Narrows workflow / context to F2-detected keys; gates `input` + `setInput` to this slot. */
  readonly inputScriptAmbientLibs = computed<MonacoAmbientLib[]>(() => {
    const scope = this.selectedNodeDataflow();
    return buildScriptAmbientLibs(
      'input-script',
      (scope?.workflowVariables ?? []).map(v => v.key),
      (scope?.contextKeys ?? []).map(v => v.key),
      !!scope?.loopBindings
    );
  });

  /** E1: ambient TS declarations for the output-script editor on the selected node. */
  readonly outputScriptAmbientLibs = computed<MonacoAmbientLib[]>(() => {
    const scope = this.selectedNodeDataflow();
    return buildScriptAmbientLibs(
      'output-script',
      (scope?.workflowVariables ?? []).map(v => v.key),
      (scope?.contextKeys ?? []).map(v => v.key),
      !!scope?.loopBindings
    );
  });

  /** E1: ambient TS declarations for the Logic-node script editor. */
  readonly logicScriptAmbientLibs = computed<MonacoAmbientLib[]>(() => {
    const scope = this.selectedNodeDataflow();
    return buildScriptAmbientLibs(
      'logic-script',
      (scope?.workflowVariables ?? []).map(v => v.key),
      (scope?.contextKeys ?? []).map(v => v.key),
      !!scope?.loopBindings
    );
  });

  /** E2: whether the selected node is inside a ReviewLoop child. Drives which snippets the
   *  completion provider offers — loop-binding-dependent snippets stay hidden outside loops. */
  readonly selectedNodeInLoop = computed<boolean>(() => !!this.selectedNodeDataflow()?.loopBindings);

  /** VZ1: scope row shown in the data-flow inspector panel. */
  readonly selectedNodeDataflow = computed<NodeDataflowScope | null>(() => {
    const sel = this.selectedNode();
    const snap = this.dataflowSnapshot();
    if (!sel || !snap) return null;
    const nodeId = sel.editor.nodeId;
    return snap.scopesByNode[nodeId.toLowerCase()]
      ?? snap.scopesByNode[nodeId.toUpperCase()]
      ?? snap.scopesByNode[nodeId]
      ?? null;
  });

  /** VZ1: workflow / context keys this node's prompts or scripts reference but no upstream
   *  node writes. Acceptance: keys with no writer render in red with "no writer found". */
  readonly selectedNodeUndeclaredReads = computed<{ workflow: string[]; context: string[] }>(() => {
    const sel = this.selectedNode();
    if (!sel) return { workflow: [], context: [] };

    const wf = new Set<string>();
    const ctx = new Set<string>();

    const docs = this.selectedAgentDocs();
    if (docs?.config) {
      const cfg = docs.config as Record<string, unknown>;
      const collect = (text: unknown) => {
        if (typeof text === 'string') extractWorkflowAndContextRefs(text, wf, ctx);
      };
      collect(cfg['systemPrompt']);
      collect(cfg['promptTemplate']);
      collect(cfg['outputTemplate']);
    }
    extractWorkflowAndContextRefsFromScript(sel.editor.inputScript ?? '', wf, ctx);
    extractWorkflowAndContextRefsFromScript(sel.editor.outputScript ?? '', wf, ctx);

    const scope = this.selectedNodeDataflow();
    const wfKnown = new Set((scope?.workflowVariables ?? []).map(v => v.key));
    const ctxKnown = new Set((scope?.contextKeys ?? []).map(v => v.key));

    return {
      workflow: [...wf].filter(k => !wfKnown.has(k) && !k.startsWith('__loop')).sort(),
      context: [...ctx].filter(k => !ctxKnown.has(k)).sort(),
    };
  });

  /** VZ1: partials that the runtime auto-injects when this node executes. Today only the
   *  P2 last-round-reminder is auto-injected — when the node sits inside a ReviewLoop and
   *  the agent doesn't already pin `@codeflow/last-round-reminder`. The opt-out flag isn't
   *  exposed in the editor yet (the workflow draft serializer doesn't carry it), so this is
   *  best-effort: if the JSON has been hand-edited to opt out, the panel will still show
   *  the partial. */
  readonly selectedNodeAutoInjectedPartials = computed<string[]>(() => {
    const sel = this.selectedNode();
    const scope = this.selectedNodeDataflow();
    if (!sel || !scope || !scope.loopBindings) return [];
    if (!AGENT_BEARING_KINDS.has(sel.editor.kind)) return [];

    const docs = this.selectedAgentDocs();
    const pins = (docs?.config as Record<string, unknown> | undefined)?.['partialPins'];
    if (Array.isArray(pins)) {
      const explicitlyPinned = pins.some(p =>
        typeof p === 'object' && p !== null
        && (p as Record<string, unknown>)['key'] === '@codeflow/last-round-reminder'
      );
      if (explicitlyPinned) return [];
    }
    return ['@codeflow/last-round-reminder'];
  });

  /** Inline warnings for edges whose source port is no longer declared by the source node. */
  readonly brokenEdgesForSelected = computed<string[]>(() => {
    this.portsRevision();
    const sel = this.selectedNode();
    if (!sel || !this.editor) return [];
    const node = sel.editor;
    const allowed = new Set(node.allOutputPortNames); // declared + implicit Failed
    return this.editor.getConnections()
      .filter(c => c.source === node.id && !allowed.has(c.sourceOutput))
      .map(c => `Edge from port '${c.sourceOutput}' is broken — that port is no longer declared.`);
  });

  private asTemplateMap(value: unknown): Record<string, string> {
    if (!value || typeof value !== 'object' || Array.isArray(value)) return {};
    return Object.entries(value as Record<string, unknown>).reduce<Record<string, string>>((acc, [key, template]) => {
      if (typeof template === 'string') acc[key] = template;
      return acc;
    }, {});
  }

  private formatPayloadExample(value: unknown): string {
    if (typeof value === 'string') return value.trim();
    return JSON.stringify(value, null, 2);
  }

  private clearSelectedAgentDocs(): void {
    this.selectedAgentDocs.set(null);
    this.selectedAgentDocsLoading.set(false);
    this.selectedAgentDocsError.set(null);
  }

  private clearSelectedSynthesizerDocs(): void {
    this.selectedSynthesizerDocs.set(null);
    this.selectedSynthesizerDocsLoading.set(false);
    this.selectedSynthesizerDocsError.set(null);
  }

  /** Swarm port derivation: load the synthesizer agent's config so the inspector can render
   *  derived ports + stale/missing drift badges. Mirrors loadAgentDocsForNode but writes to
   *  the parallel selectedSynthesizerDocs* signals so it doesn't fight with the
   *  AGENT_BEARING_KINDS path. */
  private loadSynthesizerDocsForNode(node: WorkflowEditorNode | null): void {
    if (!node || node.kind !== 'Swarm' || !node.synthesizerAgentKey) {
      this.clearSelectedSynthesizerDocs();
      return;
    }

    const nodeEditorId = node.id;
    const agentKey = node.synthesizerAgentKey;
    const agentVersion = node.synthesizerAgentVersion;
    const request$ = agentVersion && agentVersion > 0
      ? this.agentsApi.getVersion(agentKey, agentVersion)
      : this.agentsApi.getLatest(agentKey);

    this.selectedSynthesizerDocs.set(null);
    this.selectedSynthesizerDocsLoading.set(true);
    this.selectedSynthesizerDocsError.set(null);

    request$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: version => {
        const selected = this.selectedNode();
        if (!selected || selected.editor.id !== nodeEditorId
            || selected.editor.synthesizerAgentKey !== agentKey) return;

        this.selectedSynthesizerDocs.set({
          nodeEditorId,
          agentKey: version.key,
          agentVersion: version.version,
          config: version.config ?? {}
        });
        this.selectedSynthesizerDocsLoading.set(false);
      },
      error: err => {
        const selected = this.selectedNode();
        if (!selected || selected.editor.id !== nodeEditorId
            || selected.editor.synthesizerAgentKey !== agentKey) return;

        this.selectedSynthesizerDocs.set(null);
        this.selectedSynthesizerDocsLoading.set(false);
        this.selectedSynthesizerDocsError.set(`Failed to load synthesizer agent: ${err?.message ?? err}`);
      }
    });
  }

  /** Reconcile a Swarm node's output ports with its synthesizer agent's declared outputs.
   *  Mirrors syncPortsFromAgent — stale ports drop with their wires, missing ports appear,
   *  surviving ports keep their wires. Implicit Failed is unaffected. */
  syncPortsFromSynthesizer(node: WorkflowEditorNode): void {
    if (node.kind !== 'Swarm') return;
    const declared = declaredOutputPorts(this.selectedSynthesizerDocs()?.config);
    if (!declared) return;
    this.applyNodePorts(node, declared);
  }

  private loadAgentDocsForNode(node: WorkflowEditorNode | null): void {
    if (!node?.agentKey || !AGENT_BEARING_KINDS.has(node.kind)) {
      this.clearSelectedAgentDocs();
      return;
    }

    const nodeEditorId = node.id;
    const agentKey = node.agentKey;
    const request$ = node.agentVersion && node.agentVersion > 0
      ? this.agentsApi.getVersion(agentKey, node.agentVersion)
      : this.agentsApi.getLatest(agentKey);

    this.selectedAgentDocs.set(null);
    this.selectedAgentDocsLoading.set(true);
    this.selectedAgentDocsError.set(null);

    request$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: version => {
        const selected = this.selectedNode();
        if (!selected || selected.editor.id !== nodeEditorId || selected.editor.agentKey !== agentKey) return;

        this.selectedAgentDocs.set({
          nodeEditorId,
          agentKey: version.key,
          agentVersion: version.version,
          config: version.config ?? {}
        });
        this.selectedAgentDocsLoading.set(false);
      },
      error: err => {
        const selected = this.selectedNode();
        if (!selected || selected.editor.id !== nodeEditorId || selected.editor.agentKey !== agentKey) return;

        this.selectedAgentDocs.set(null);
        this.selectedAgentDocsLoading.set(false);
        this.selectedAgentDocsError.set(`Failed to load agent examples: ${err?.message ?? err}`);
      }
    });
  }

  async ngAfterViewInit(): Promise<void> {
    // VZ5: hydrate the implicit-Failed visibility toggle from per-author preference.
    try {
      const stored = localStorage.getItem(WorkflowCanvasComponent.ShowImplicitFailedStorageKey);
      if (stored === '1') this.showImplicitFailed.set(true);
    } catch {
      // localStorage unavailable; default off.
    }

    this.agentsApi.list().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: agents => this.agents.set(agents),
      error: err => this.error.set(`Failed to load agents: ${err.message ?? err}`)
    });
    this.api.list().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: workflows => {
        this.workflows.set(workflows);
        this.preloadTerminalPortsForCandidates(workflows);
      },
      error: err => this.error.set(`Failed to load workflows: ${err.message ?? err}`)
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
        this.selectConnection(null);
        this.selectedNodeId.set(context.data.id);
        this.scriptValidationError.set(null);
        this.scriptValidationOk.set(false);
        this.scriptMarkers.set([]);
        this.inputScriptValidationError.set(null);
        this.inputScriptValidationOk.set(false);
        this.inputScriptMarkers.set([]);
        this.selectedSubflowDetail.set(null);
        const picked = this.editor?.getNode(context.data.id) as WorkflowEditorNode | undefined;
        this.loadAgentDocsForNode(picked ?? null);
        this.loadSynthesizerDocsForNode(picked ?? null);
        if ((picked?.kind === 'Subflow' || picked?.kind === 'ReviewLoop') && picked.subflowKey) {
          this.api.getLatest(picked.subflowKey).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
            next: detail => this.selectedSubflowDetail.set(detail),
            error: () => this.selectedSubflowDetail.set(null)
          });
        }
      }
      if (context.type === 'render' && context.data.type === 'connection') {
        this.bindConnectionElement(context.data.payload as WorkflowEditorConnection, context.data.element);
      }
      if (context.type === 'unmount') {
        this.releaseConnectionElement(context.data.element);
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
        this.bindConnectionInteraction(data);
      }
      if (context.type === 'nodecreated') {
        // VZ5: every freshly-added node (palette drop or load) gets the toggle accessor wired
        // so its template binding can react to the canvas-level "Show implicit Failed ports"
        // signal without depending on Angular DI through Rete's render boundary.
        const created = context.data as WorkflowEditorNode;
        created.showImplicitFailed = () => this.showImplicitFailed();
      }
      if (context.type === 'connectioncreated' || context.type === 'connectionremoved' || context.type === 'noderemoved' || context.type === 'nodecreated') {
        // VZ1: any structural change can shift the dataflow scope; mark the cached snapshot
        // stale so the inspector can hint that a save is needed for an accurate read.
        if (this.dataflowSnapshot()) this.dataflowDirty.set(true);
        // VZ7: re-detect backedges. Defer until the editor finishes processing the event so
        // getConnections()/getNodes() reflect the post-change graph.
        queueMicrotask(() => this.recomputeBackedges());
        // VZ5: mirror per-node Failed-port wiring state for the implicit-Failed visibility toggle.
        queueMicrotask(() => this.recomputeFailedConnections());
      }
      if (context.type === 'noderemoved') {
        if (this.selectedNodeId() === context.data.id) {
          this.selectedNodeId.set(null);
          this.clearSelectedAgentDocs();
        }
      }
      if (context.type === 'connectionremoved') {
        if (this.selectedConnectionId() === context.data.id) {
          this.selectedConnectionId.set(null);
        }
      }
      return context;
    });

    this.editor = editor;
    this.area = area;

    runInInjectionContext(this.injector, () => {
      effect(() => {
        const sel = this.selectedNode();
        if (!sel || sel.editor.kind !== 'Transform') {
          if (this.transformPreviewSelectedNodeId !== null) {
            this.transformPreviewSelectedNodeId = null;
            this.cancelTransformPreview();
            this.resetTransformPreviewSignals();
          }
          return;
        }
        if (this.transformPreviewSelectedNodeId === sel.editor.id) return;
        this.transformPreviewSelectedNodeId = sel.editor.id;
        this.scheduleTransformPreview();
      });
    });

    const key = this.route.snapshot.paramMap.get('key');
    if (key) {
      this.hasExistingKey.set(true);
      this.workflowKey.set(key);
      this.api.getLatest(key).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: async detail => {
          this.workflowName.set(detail.name);
          this.maxRoundsPerRound.set(detail.maxRoundsPerRound);
          this.workflowCategory.set(detail.category ?? 'Workflow');
          this.workflowTags.set(detail.tags ?? []);
          const loadedInputs = detail.inputs.slice().sort((a, b) => a.ordinal - b.ordinal);
          this.inputs.set(loadedInputs.length === 0 ? [defaultStartInput()] : loadedInputs);
          await loadIntoEditor(workflowDetailToModel(detail), editor, area);
          if (editor.getNodes().length > 0) {
            AreaExtensions.zoomAt(area, editor.getNodes());
          }
          this.loadDataflow(detail.key, detail.version);
          this.recomputeBackedges();
          this.recomputeFailedConnections();
        },
        error: err => this.error.set(`Failed to load workflow: ${err.message ?? err}`)
      });
    } else {
      const initialModel = emptyModel();
      await loadIntoEditor(initialModel, editor, area);
    }
  }

  ngOnDestroy(): void {
    for (const { element, onClick } of this.connectionElements.values()) {
      element.removeEventListener('click', onClick);
    }
    this.connectionElements.clear();
    this.cancelTransformPreview();
    this.area?.destroy();
    this.pageContext.clear();
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
    if (!this.selectedConnectionId() && !this.selectedNodeId()) return;
    event.preventDefault();
    if (this.selectedConnectionId()) {
      void this.removeSelectedConnection();
      return;
    }
    void this.removeSelectedNode();
  }

  toggleImplicitFailed(): void {
    const next = !this.showImplicitFailed();
    this.showImplicitFailed.set(next);
    try {
      localStorage.setItem(WorkflowCanvasComponent.ShowImplicitFailedStorageKey, next ? '1' : '0');
    } catch {
      // localStorage may be unavailable (private mode); the toggle still works in-session.
    }
    // Visibility is driven by the .show-implicit-failed class on the canvas wrapper (see
    // globals.scss). Angular re-renders that class binding when the signal flips, so each
    // node's implicit-Failed row toggles via CSS without needing per-node re-renders here
    // (which Angular Elements wouldn't honor on a same-reference setInput anyway).
  }

  /**
   * VZ5: walk current connections and mark each node's `failedHasConnection` so the node
   * component knows whether to render the implicit Failed row even when the toggle is off.
   * Called on every structural change.
   */
  private recomputeFailedConnections(): void {
    if (!this.editor || !this.area) return;
    const wired = new Set<string>();
    for (const c of this.editor.getConnections() as WorkflowEditorConnection[]) {
      // Rete connection has `sourceOutput`, the port key on the source node.
      const sourceOutput = (c as unknown as { sourceOutput?: string }).sourceOutput;
      if (sourceOutput === 'Failed') wired.add(c.source);
    }
    for (const node of this.editor.getNodes() as WorkflowEditorNode[]) {
      const has = wired.has(node.id);
      if (node.failedHasConnection !== has) {
        node.failedHasConnection = has;
        void this.area.update('node', node.id);
      }
    }
  }

  async tidy(): Promise<void> {
    if (!this.editor || !this.area) return;
    await tidyLayout(this.editor, this.area);
    if (this.editor.getNodes().length > 0) {
      AreaExtensions.zoomAt(this.area, this.editor.getNodes());
    }
  }

  cancel(): void {
    this.router.navigate(['/workflows']);
  }

  async addPaletteNode(kind: WorkflowNodeKind): Promise<void> {
    if (!this.editor || !this.area) return;

    if (kind === 'Start' && this.editor.getNodes().some(n => n.kind === 'Start')) {
      this.error.set('A workflow may only contain one Start node.');
      return;
    }

    // Sensible defaults for Swarm so the inspector lands in a renderable state and the
    // node label reads "Swarm Sequential ×3" instead of "Swarm ? ×?". Validator's [1, 16]
    // range allows 3 as a reasonable starting fan-out; author refines in the inspector.
    const swarmDefaults = kind === 'Swarm'
      ? { swarmProtocol: 'Sequential' as const, swarmN: 3 }
      : {};

    const node = new WorkflowEditorNode({
      nodeId: crypto.randomUUID(),
      kind,
      label: labelFor({ kind, agentKey: null, ...swarmDefaults }),
      outputPorts: defaultOutputPortsFor(kind),
      ...swarmDefaults
    });

    await this.editor.addNode(node);

    const existingCount = this.editor.getNodes().length - 1;
    await this.area.translate(node.id, { x: 80 + (existingCount % 4) * 40, y: 80 + existingCount * 20 });

    this.error.set(null);
  }

  onCanvasContextMenu(event: MouseEvent): void {
    const nodeId = this.findNodeIdAtEvent(event);
    if (!nodeId || !this.editor) return;

    const node = this.editor.getNode(nodeId) as WorkflowEditorNode | undefined;
    if (!node) return;

    event.preventDefault();
    this.selectedNodeId.set(nodeId);
    this.loadAgentDocsForNode(node);
    this.loadSynthesizerDocsForNode(node);

    const items: NodeContextMenuItem[] = [];

    if (AGENT_BEARING_KINDS.has(node.kind)) {
      const hasAgent = !!node.agentKey;
      items.push({
        id: 'edit-in-place',
        label: 'Edit agent in place…',
        disabled: !hasAgent,
        disabledReason: hasAgent ? undefined : 'Pick an agent on the inspector first.'
      });
      if (node.agentKey?.startsWith(FORK_KEY_PREFIX)) {
        items.push({ id: 'publish-fork', label: 'Publish fork…' });
      }
      items.push({
        id: 'update-to-latest',
        label: 'Update to latest agent version…',
        disabled: !hasAgent,
        disabledReason: hasAgent ? undefined : 'Pick an agent on the inspector first.'
      });
    } else if (node.kind === 'Subflow' || node.kind === 'ReviewLoop') {
      const hasChild = !!node.subflowKey;
      items.push({
        id: 'update-to-latest',
        label: 'Update to latest workflow version…',
        disabled: !hasChild,
        disabledReason: hasChild ? undefined : 'Pick a child workflow on the inspector first.'
      });
    }

    items.push({ id: 'delete', label: 'Delete node', danger: true });

    this.contextMenu.set({
      nodeId,
      x: event.clientX,
      y: event.clientY,
      items
    });
  }

  onContextMenuPick(item: NodeContextMenuItem): void {
    const menu = this.contextMenu();
    if (!menu || !this.editor) return;

    const node = this.editor.getNode(menu.nodeId) as WorkflowEditorNode | undefined;
    this.closeContextMenu();
    if (!node) return;

    switch (item.id) {
      case 'delete':
        void this.removeSelectedNode();
        return;
      case 'edit-in-place':
        this.openEditInPlace(node);
        return;
      case 'publish-fork':
        this.openPublishFork(node);
        return;
      case 'update-to-latest':
        this.openVersionUpdate(node);
        return;
    }
  }

  protected openVersionUpdate(node: WorkflowEditorNode): void {
    const outgoing = (this.editor?.getConnections() ?? [])
      .filter(c => c.source === node.id)
      .map(c => ({
        sourcePort: c.sourceOutput,
        targetLabel: this.connectionEndpointLabel(c.target, c.targetInput),
      }));
    this.dialogs.openVersionUpdate(node, outgoing, {
      setStatus: message => this.statusMessage.set(message),
      setError: message => this.error.set(message),
    });
  }

  onVersionUpdateConfirmed(result: VersionUpdateResult): void {
    const node = this.editor?.getNode(result.nodeId) as WorkflowEditorNode | undefined;
    if (!node) {
      this.dialogs.cancelVersionUpdate();
      return;
    }

    if (AGENT_BEARING_KINDS.has(node.kind)) {
      node.agentVersion = result.toVersion;
      this.loadAgentDocsForNode(node);
    } else if (node.kind === 'Subflow' || node.kind === 'ReviewLoop') {
      node.subflowVersion = result.toVersion;
      node.label = labelFor(node);
      this.area?.update('node', node.id);
    }

    // Drop edges from removed ports first so applyNodePorts doesn't churn over them.
    for (const port of result.edgePortsToRemove) {
      this.editor?.getConnections()
        .filter(c => c.source === node.id && c.sourceOutput === port)
        .forEach(c => this.editor?.removeConnection(c.id));
    }

    this.applyNodePorts(node, result.newPorts);
    this.dialogs.cancelVersionUpdate();
    this.statusMessage.set(`Updated to v${result.toVersion}.`);
  }

  closeContextMenu(): void {
    if (this.contextMenu()) this.contextMenu.set(null);
  }

  protected openEditInPlace(node: WorkflowEditorNode): void {
    this.dialogs.openEditInPlace(
      node,
      this.workflowKey().trim(),
      message => this.error.set(message)
    );
  }

  protected openPublishFork(node: WorkflowEditorNode): void {
    this.dialogs.openPublishFork(node);
  }

  onForkPublished(result: PublishForkResult): void {
    if (this.editor) {
      const node = this.editor.getNode(result.nodeId) as WorkflowEditorNode | undefined;
      if (node) {
        node.agentKey = result.publishedKey;
        node.agentVersion = result.publishedVersion;
        node.label = labelFor(node);
        this.area?.update('node', node.id);
        this.selectedNodeId.set(this.selectedNodeId());
        this.loadAgentDocsForNode(node);
      }
    }
    this.statusMessage.set(`Published ${result.publishedKey} v${result.publishedVersion}. Save the workflow to persist the re-link.`);
    this.dialogs.closePublishFork();
  }

  onEditInPlaceSaved(result: InPlaceEditResult): void {
    if (!this.editor) {
      this.dialogs.closeEditInPlace();
      return;
    }

    const node = this.editor.getNode(result.nodeId) as WorkflowEditorNode | undefined;
    if (node) {
      node.agentKey = result.agentKey;
      node.agentVersion = result.agentVersion;
      node.label = labelFor(node);

      const declared = result.config.outputs;
      if (Array.isArray(declared) && declared.length > 0) {
        const portNames = declared
          .map(d => d.kind)
          .filter((kind): kind is string => typeof kind === 'string' && kind.length > 0);
        if (portNames.length > 0) this.applyNodePorts(node, portNames);
      }

      this.area?.update('node', node.id);
      // Bump the signal so the inspector re-reads node fields.
      this.selectedNodeId.set(this.selectedNodeId());
      this.loadAgentDocsForNode(node);
    }

    this.statusMessage.set(`Saved ${result.agentKey} v${result.agentVersion} (workflow-scoped fork). Save the workflow to persist.`);
    this.dialogs.closeEditInPlace();
  }

  private findNodeIdAtEvent(event: MouseEvent): string | null {
    if (!this.area) return null;
    const target = event.target as Node | null;
    if (!target) return null;

    const nodeViews = (this.area as unknown as { nodeViews?: Map<string, { element: HTMLElement }> }).nodeViews;
    if (!nodeViews) return null;

    for (const [id, view] of nodeViews.entries()) {
      if (view.element && view.element.contains(target)) {
        return id;
      }
    }
    return null;
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
    this.clearSelectedAgentDocs();
  }

  async removeSelectedConnection(): Promise<void> {
    if (!this.editor) return;
    const id = this.selectedConnectionId();
    if (!id) return;

    await this.editor.removeConnection(id);
    this.selectedConnectionId.set(null);
  }

  onSubflowKeyChanged(node: WorkflowEditorNode, value: string): void {
    node.subflowKey = value || null;
    node.label = labelFor(node);
    this.area?.update('node', node.id);
    this.selectedNodeId.set(this.selectedNodeId());

    if (!value) {
      this.selectedSubflowDetail.set(null);
      this.applyNodePorts(node, []);
      return;
    }

    this.api.getLatest(value).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: detail => this.selectedSubflowDetail.set(detail),
      error: () => this.selectedSubflowDetail.set(null)
    });

    this.refreshSubflowPorts(node);
  }

  onSubflowVersionChanged(node: WorkflowEditorNode, value: number | null): void {
    node.subflowVersion = value && value > 0 ? value : null;
    node.label = labelFor(node);
    this.area?.update('node', node.id);
    this.refreshSubflowPorts(node);
  }

  onReviewMaxRoundsChanged(node: WorkflowEditorNode, value: number | null): void {
    // Clamp to the validator-enforced [1, 10] range so the UI can't silently desync from the
    // save-time error. `null` leaves it unset so the user sees the required-field warning.
    if (value === null || value === undefined) {
      node.reviewMaxRounds = null;
    } else {
      node.reviewMaxRounds = Math.max(1, Math.min(10, Math.floor(value)));
    }
    node.label = labelFor(node);
    this.area?.update('node', node.id);
  }

  onLoopDecisionChanged(node: WorkflowEditorNode, value: string): void {
    // Empty string means "use the default" (Rejected); store null so the API layer can see
    // the author didn't override it and the config travels cleanly.
    const trimmed = value?.trim() ?? '';
    node.loopDecision = trimmed.length === 0 ? null : trimmed;
    this.area?.update('node', node.id);
    // loopDecision is excluded from the ReviewLoop child terminal-port set (it iterates
    // instead of exiting), so refresh ports when the author changes it.
    if (node.kind === 'ReviewLoop') {
      this.refreshSubflowPorts(node);
    }
  }

  /** VZ6: Eagerly fetch the latest-version terminal ports for every candidate workflow so
   *  the Subflow / ReviewLoop picker can show "{key} (vN) → port1, port2, …" inline. The
   *  current workflow itself is excluded (`availableSubflowTargets` filters it before render
   *  anyway, but we still skip the fetch). Failures per-candidate are silently dropped — a
   *  missing entry just means the picker option falls back to the bare `{key} (vN)` label. */
  private preloadTerminalPortsForCandidates(workflows: WorkflowSummary[]): void {
    const currentKey = this.workflowKey().trim();
    for (const wf of workflows) {
      if (wf.key === currentKey) continue;
      this.api.getTerminalPorts(wf.key, wf.latestVersion).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: ports => {
          this.terminalPortsByKey.update(prev => ({ ...prev, [wf.key]: ports }));
        },
        error: () => { /* best-effort cache; option falls back to bare label */ }
      });
    }
  }

  /** VZ6: option text for a candidate workflow in the Subflow / ReviewLoop picker. */
  subflowPickerLabel(wf: WorkflowSummary): string {
    const ports = this.terminalPortsByKey()[wf.key];
    const base = `${wf.key} (v${wf.latestVersion})`;
    if (!ports) return base;
    const portList = ports.length > 0 ? ports.join(', ') + ', Failed' : 'Failed';
    return `${base} → ${portList}`;
  }

  /** Fetch the child workflow's terminal ports and apply them to this Subflow / ReviewLoop
   *  node. ReviewLoop additionally synthesizes `Exhausted` and excludes the configured
   *  loopDecision (since that one iterates rather than exits). */
  private refreshSubflowPorts(node: WorkflowEditorNode): void {
    if (node.kind !== 'Subflow' && node.kind !== 'ReviewLoop') return;
    const subflowKey = node.subflowKey;
    if (!subflowKey) return;

    this.api.getTerminalPorts(subflowKey, node.subflowVersion ?? null).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: terminals => {
        let ports = terminals.slice();
        if (node.kind === 'ReviewLoop') {
          const loopDecision = (node.loopDecision?.trim()) || 'Rejected';
          ports = ports.filter(p => p !== loopDecision);
          if (!ports.includes('Exhausted')) ports.push('Exhausted');
        }
        this.applyNodePorts(node, ports);
      },
      error: () => { /* best-effort; leave existing ports in place */ }
    });
  }

  labelForOutline(node: { kind: string; agentKey?: string | null; subflowKey?: string | null; reviewMaxRounds?: number | null }): string {
    if (node.kind === 'Subflow') return `→ ${node.subflowKey ?? '?'}`;
    if (node.kind === 'ReviewLoop') return `×${node.reviewMaxRounds ?? '?'} → ${node.subflowKey ?? '?'}`;
    return node.agentKey ?? '';
  }

  connectionSummary(connection: WorkflowEditorConnection): string {
    return `${this.connectionEndpointLabel(connection.source, connection.sourceOutput)} -> ${this.connectionEndpointLabel(connection.target, connection.targetInput)}`;
  }

  onAgentChanged(node: WorkflowEditorNode, value: string): void {
    node.agentKey = value || null;
    node.label = labelFor(node);
    this.selectedNodeId.set(this.selectedNodeId());
    this.loadAgentDocsForNode(node);

    if (!value) return;

    this.agentsApi.getLatest(value).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: version => {
        const declared = version.config?.outputs;
        if (!declared || declared.length === 0) return;
        const portNames = declared.map(d => d.kind).filter(n => typeof n === 'string' && n.length > 0);
        if (portNames.length === 0) return;
        this.applyNodePorts(node, portNames);
      },
      error: () => { /* best-effort; leave existing ports in place */ }
    });
  }

  onAgentVersionChanged(node: WorkflowEditorNode, value: number | string | null): void {
    const version = typeof value === 'number' ? value : Number(value);
    node.agentVersion = Number.isFinite(version) && version > 0 ? Math.floor(version) : null;
    this.loadAgentDocsForNode(node);
  }

  onOutputPortsChanged(node: WorkflowEditorNode, value: string): void {
    const next = value
      .split(/\r?\n/)
      .map(line => line.trim())
      .filter(line => line.length > 0);
    this.applyNodePorts(node, next);
  }

  /** Reconcile the selected agent-bearing node's ports with its pinned agent's declared
   *  outputs. Stale ports are removed (and any wires on them dropped); missing ports are
   *  added. Wires on ports that survive the diff are left untouched. */
  syncPortsFromAgent(node: WorkflowEditorNode): void {
    if (!AGENT_BEARING_KINDS.has(node.kind)) return;
    const declared = declaredOutputPorts(this.selectedAgentDocs()?.config);
    if (!declared) return;
    this.applyNodePorts(node, declared);
  }

  private applyNodePorts(node: WorkflowEditorNode, desired: string[]): void {
    // Failed is implicit: the editor always renders it as a handle and it's never declared.
    // Strip it from incoming desired sets so it can't be rebuilt as a regular port.
    const cleaned = desired.filter(p => p !== 'Failed' && p.trim().length > 0);
    const current = new Set(node.outputPortNames); // already excludes implicit Failed
    const desiredSet = new Set(cleaned);

    for (const port of current) {
      if (!desiredSet.has(port)) {
        this.editor?.getConnections()
          .filter(c => c.source === node.id && c.sourceOutput === port)
          .forEach(c => this.editor?.removeConnection(c.id));
        node.removeOutput(port);
      }
    }

    for (const port of cleaned) {
      if (!current.has(port)) {
        node.addOutput(port, new ClassicPreset.Output(new ClassicPreset.Socket('port'), port));
      }
    }

    this.area?.update('node', node.id);
    this.portsRevision.update(v => v + 1);
  }

  private bindConnectionInteraction(connection: WorkflowEditorConnection): void {
    connection.onPick = () => this.selectConnection(connection.id);
  }

  private bindConnectionElement(connection: WorkflowEditorConnection, element: HTMLElement): void {
    const existing = this.connectionElements.get(connection.id);
    if (existing?.element !== element) {
      existing?.element.removeEventListener('click', existing.onClick);
      this.connectionElements.delete(connection.id);
    }

    if (!this.connectionElements.has(connection.id)) {
      const onClick = (event: MouseEvent) => {
        event.preventDefault();
        event.stopPropagation();
        connection.onPick?.();
      };
      element.addEventListener('click', onClick);
      this.connectionElements.set(connection.id, { element, onClick });
    }

    requestAnimationFrame(() => this.applyConnectionStyles(connection.id));
  }

  private connectionEndpointLabel(nodeId: string, portKey?: string): string {
    const label = (this.editor?.getNode(nodeId) as WorkflowEditorNode | undefined)?.label ?? nodeId;
    return portKey ? `${label}.${portKey}` : label;
  }

  private selectConnection(connectionId: string | null): void {
    const previousId = this.selectedConnectionId();
    if (previousId === connectionId) return;

    if (previousId) {
      const previous = this.editor?.getConnection(previousId) as WorkflowEditorConnection | undefined;
      if (previous) {
        previous.isSelected = false;
        this.applyConnectionStyles(previous.id);
      }
    }

    this.selectedConnectionId.set(connectionId);

    if (!connectionId) return;

    const next = this.editor?.getConnection(connectionId) as WorkflowEditorConnection | undefined;
    if (!next) return;

    next.isSelected = true;
    this.selectedNodeId.set(null);
    this.clearSelectedAgentDocs();
    this.scriptValidationError.set(null);
    this.scriptValidationOk.set(false);
    this.scriptMarkers.set([]);
    this.inputScriptValidationError.set(null);
    this.inputScriptValidationOk.set(false);
    this.inputScriptMarkers.set([]);
    this.selectedSubflowDetail.set(null);
    this.applyConnectionStyles(next.id);
  }

  /** VZ7: toggle the V6 intentional-backedge flag on the selected connection. Suppresses
   *  this edge's dashed-amber render and the V6 save warning on subsequent saves. */
  toggleSelectedConnectionIntentional(intentional: boolean): void {
    const id = this.selectedConnectionId();
    if (!id) return;
    const connection = this.editor?.getConnection(id) as WorkflowEditorConnection | undefined;
    if (!connection) return;
    connection.intentionalBackedge = intentional;
    this.applyConnectionStyles(id);
    this.backedgeRevision.update(v => v + 1);
  }

  private applyConnectionStyles(connectionId: string): void {
    const registered = this.connectionElements.get(connectionId);
    const connection = this.editor?.getConnection(connectionId) as WorkflowEditorConnection | undefined;
    if (!registered || !connection) return;

    WorkflowBackedgeAnalyzer.applyConnectionStyles(
      { element: registered.element, connection },
      { backedgeIds: this.backedgeIds, cycleByConnectionId: this.backedgeCycleByConnId }
    );
  }

  /**
   * VZ7: DFS-coloring backedge detection. Mirrors the V6 BackedgeRule (port from
   * CodeFlow.Api/Validation/Pipeline/Rules/BackedgeRule.cs). An edge (u, v) is a backedge iff
   * v is on the active DFS stack when we walk it. Roots are picked by topology (no-incoming
   * first, leftover whites second) so the closing edge of a cycle is the one flagged, not
   * forward edges feeding into the cycle.
   *
   * ReviewLoop iteration is internal to the loop primitive and never appears in the authored
   * connection list, so it cannot trip this check.
   */
  private recomputeBackedges(): void {
    if (!this.editor) return;
    this.backedgeIds.clear();
    this.backedgeCycleByConnId.clear();

    const connections = this.editor.getConnections() as WorkflowEditorConnection[];
    const nodeIds = this.editor.getNodes().map(n => n.id);

    if (connections.length === 0 || nodeIds.length === 0) {
      this.backedgeRevision.update(v => v + 1);
      return;
    }

    const analysis = WorkflowBackedgeAnalyzer.recompute(
      nodeIds,
      connections,
      id => this.nodeLabelFor(id)
    );
    for (const id of analysis.backedgeIds) this.backedgeIds.add(id);
    for (const [id, cycle] of analysis.cycleByConnectionId) this.backedgeCycleByConnId.set(id, cycle);

    this.backedgeRevision.update(v => v + 1);
    for (const c of connections) this.applyConnectionStyles(c.id);
  }

  private nodeLabelFor(nodeId: string): string {
    return (this.editor?.getNode(nodeId) as WorkflowEditorNode | undefined)?.label ?? nodeId;
  }

  private releaseConnectionElement(element: HTMLElement): void {
    for (const [id, registered] of this.connectionElements.entries()) {
      if (registered.element !== element) continue;
      registered.element.removeEventListener('click', registered.onClick);
      this.connectionElements.delete(id);
      return;
    }
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

  onNodeScriptChanged(node: WorkflowEditorNode, slot: 'input' | 'output', value: string): void {
    if (slot === 'input') {
      node.inputScript = value;
      if (this.inputScriptMarkers().length > 0) this.inputScriptMarkers.set([]);
      if (this.inputScriptValidationError()) this.inputScriptValidationError.set(null);
      if (this.inputScriptValidationOk()) this.inputScriptValidationOk.set(false);
    } else {
      node.outputScript = value;
      if (this.scriptMarkers().length > 0) this.scriptMarkers.set([]);
      if (this.scriptValidationError()) this.scriptValidationError.set(null);
      if (this.scriptValidationOk()) this.scriptValidationOk.set(false);
    }
    if (this.dataflowSnapshot()) this.dataflowDirty.set(true);
  }

  onTransformTemplateChanged(node: WorkflowEditorNode, value: string): void {
    node.template = value;
    if (this.dataflowSnapshot()) this.dataflowDirty.set(true);
    this.scheduleTransformPreview();
  }

  onTransformOutputTypeChanged(node: WorkflowEditorNode, value: WorkflowTransformOutputType): void {
    if (node.outputType === value) return;
    node.outputType = value;
    node.label = labelFor(node);
    this.area?.update('node', node.id);
    if (this.dataflowSnapshot()) this.dataflowDirty.set(true);
    this.scheduleTransformPreview();
  }

  onSwarmProtocolChanged(node: WorkflowEditorNode, value: WorkflowSwarmProtocol): void {
    node.swarmProtocol = value;
    // Validator rejects coordinator fields under Sequential. Clear them on switch
    // so the inspector + serialized payload stay consistent.
    if (value !== 'Coordinator') {
      node.coordinatorAgentKey = null;
      node.coordinatorAgentVersion = null;
    }
    node.label = labelFor(node);
    this.area?.update('node', node.id);
  }

  onSwarmNChanged(node: WorkflowEditorNode, value: number | null): void {
    // Clamp to validator's [1, 16] range so the UI can't desync from save-time errors.
    if (value === null || value === undefined) {
      node.swarmN = null;
    } else {
      node.swarmN = Math.max(1, Math.min(16, Math.floor(value)));
    }
    node.label = labelFor(node);
    this.area?.update('node', node.id);
  }

  onSwarmAgentKeyChanged(
    node: WorkflowEditorNode,
    slot: 'contributor' | 'synthesizer' | 'coordinator',
    value: string
  ): void {
    const key = value || null;
    if (slot === 'contributor') node.contributorAgentKey = key;
    else if (slot === 'synthesizer') {
      node.synthesizerAgentKey = key;
      // Synthesizer drives the Swarm node's terminal ports — refresh derived doc cache and
      // (when an agent is picked) apply its declared outputs to the node's ports. Cleared
      // selection just clears the cache; existing ports stay so authors don't lose wires.
      this.loadSynthesizerDocsForNode(node);
      if (key) this.fetchAndApplySynthesizerPorts(node);
    }
    else node.coordinatorAgentKey = key;
    this.area?.update('node', node.id);
  }

  onSwarmAgentVersionChanged(
    node: WorkflowEditorNode,
    slot: 'contributor' | 'synthesizer' | 'coordinator',
    value: number | string | null
  ): void {
    const num = typeof value === 'number' ? value : Number(value);
    const version = Number.isFinite(num) && num > 0 ? Math.floor(num) : null;
    if (slot === 'contributor') node.contributorAgentVersion = version;
    else if (slot === 'synthesizer') {
      node.synthesizerAgentVersion = version;
      // Re-fetch — different versions may declare different outputs; the derived ports +
      // drift badges should follow the version pin.
      this.loadSynthesizerDocsForNode(node);
    }
    else node.coordinatorAgentVersion = version;
  }

  /** One-shot fetch + apply for the synthesizer's declared outputs. Called when the author
   *  picks a synthesizer for the first time so the node's ports adopt the agent's outputs
   *  without the author needing to click "Sync from synthesizer". */
  private fetchAndApplySynthesizerPorts(node: WorkflowEditorNode): void {
    if (node.kind !== 'Swarm' || !node.synthesizerAgentKey) return;
    const request$ = node.synthesizerAgentVersion && node.synthesizerAgentVersion > 0
      ? this.agentsApi.getVersion(node.synthesizerAgentKey, node.synthesizerAgentVersion)
      : this.agentsApi.getLatest(node.synthesizerAgentKey);
    request$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: version => {
        const declared = version.config?.outputs;
        if (!declared || declared.length === 0) return;
        const portNames = declared.map(d => d.kind).filter(n => typeof n === 'string' && n.length > 0);
        if (portNames.length === 0) return;
        this.applyNodePorts(node, portNames);
      },
      error: () => { /* best-effort; existing ports stay in place */ }
    });
  }

  onSwarmTokenBudgetChanged(node: WorkflowEditorNode, value: number | null): void {
    if (value === null || value === undefined) {
      node.swarmTokenBudget = null;
      return;
    }
    const floored = Math.floor(value);
    node.swarmTokenBudget = floored > 0 ? floored : null;
  }

  onTransformPreviewFixtureChanged(value: string): void {
    this.transformPreviewFixture.set(value);
    this.scheduleTransformPreview();
  }

  private scheduleTransformPreview(): void {
    this.cancelTransformPreview();
    const sel = this.selectedNode();
    if (!sel || sel.editor.kind !== 'Transform') {
      this.resetTransformPreviewSignals();
      return;
    }
    const template = sel.editor.template ?? '';
    if (!template.trim()) {
      this.resetTransformPreviewSignals();
      return;
    }
    this.transformPreviewPending.set(true);
    this.transformPreviewTimer = setTimeout(() => {
      this.transformPreviewTimer = undefined;
      const current = this.selectedNode();
      if (!current || current.editor.kind !== 'Transform' || current.editor.id !== sel.editor.id) return;
      this.runTransformPreview(current.editor);
    }, 200);
  }

  private cancelTransformPreview(): void {
    if (this.transformPreviewTimer !== undefined) {
      clearTimeout(this.transformPreviewTimer);
      this.transformPreviewTimer = undefined;
    }
  }

  private resetTransformPreviewSignals(): void {
    this.transformPreviewRendered.set(null);
    this.transformPreviewParsedPretty.set(null);
    this.transformPreviewJsonParseError.set(null);
    this.transformPreviewError.set(null);
    this.transformPreviewPending.set(false);
    this.transformPreviewFixtureError.set(null);
  }

  private runTransformPreview(node: WorkflowEditorNode): void {
    const template = node.template ?? '';
    const outputType: WorkflowTransformOutputType = node.outputType ?? 'string';
    const fixtureText = this.transformPreviewFixture();

    let inputValue: unknown;
    if (!fixtureText.trim()) {
      inputValue = {};
    } else {
      try {
        inputValue = JSON.parse(fixtureText);
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        this.transformPreviewFixtureError.set(`Sample input is not valid JSON: ${message}`);
        this.transformPreviewPending.set(false);
        this.transformPreviewRendered.set(null);
        this.transformPreviewParsedPretty.set(null);
        this.transformPreviewJsonParseError.set(null);
        this.transformPreviewError.set(null);
        return;
      }
    }
    this.transformPreviewFixtureError.set(null);

    this.api.renderTransformPreview({
      template,
      outputType,
      input: inputValue,
      context: {},
      workflow: {}
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: response => {
        this.transformPreviewRendered.set(response.rendered);
        this.transformPreviewError.set(null);
        this.transformPreviewPending.set(false);
        this.transformPreviewJsonParseError.set(response.jsonParseError ?? null);
        if (outputType === 'json' && !response.jsonParseError && response.parsed !== undefined) {
          try {
            this.transformPreviewParsedPretty.set(JSON.stringify(response.parsed, null, 2));
          } catch {
            this.transformPreviewParsedPretty.set(null);
          }
        } else {
          this.transformPreviewParsedPretty.set(null);
        }
      },
      error: err => {
        const body = err && typeof err === 'object' ? (err as { error?: unknown }).error : null;
        let message: string;
        if (body && typeof body === 'object') {
          message = (body as { error?: string }).error ?? 'Preview render failed.';
        } else if (typeof body === 'string') {
          message = body;
        } else {
          message = 'Preview render failed.';
        }
        this.transformPreviewError.set(message);
        this.transformPreviewRendered.set(null);
        this.transformPreviewParsedPretty.set(null);
        this.transformPreviewJsonParseError.set(null);
        this.transformPreviewPending.set(false);
      }
    });
  }

  validateNodeScript(node: WorkflowEditorNode, slot: 'input' | 'output'): void {
    const script = slot === 'input' ? node.inputScript : node.outputScript;
    const errorSig = slot === 'input' ? this.inputScriptValidationError : this.scriptValidationError;
    const okSig = slot === 'input' ? this.inputScriptValidationOk : this.scriptValidationOk;
    const markersSig = slot === 'input' ? this.inputScriptMarkers : this.scriptMarkers;

    if (!script) {
      errorSig.set('Script is empty.');
      okSig.set(false);
      markersSig.set([]);
      return;
    }

    this.api.validateScript({
      script,
      direction: slot === 'input' ? 'Input' : 'Output'
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: result => {
        if (result.ok) {
          errorSig.set(null);
          okSig.set(true);
          markersSig.set([]);
        } else {
          errorSig.set(result.errors.map(e => e.message).join('; '));
          okSig.set(false);
          markersSig.set(result.errors.map(e => ({
            startLineNumber: Math.max(1, e.line || 1),
            startColumn: Math.max(1, e.column || 1),
            endLineNumber: Math.max(1, e.line || 1),
            endColumn: Math.max(1, (e.column || 1) + 1),
            message: e.message,
            severity: 'error' as const
          })));
        }
      },
      error: err => {
        errorSig.set(`Validation failed: ${err.message ?? err}`);
        okSig.set(false);
        markersSig.set([]);
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
      category: this.workflowCategory(),
      tags: this.workflowTags(),
      inputs: this.inputs()
    });

    this.saving.set(true);
    this.error.set(null);
    this.statusMessage.set(null);

    const request$ = this.hasExistingKey()
      ? this.api.addVersion(key, payload)
      : this.api.create(payload);

    request$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: result => {
        this.saving.set(false);
        this.statusMessage.set(`Saved ${result.key} v${result.version}`);
        this.loadDataflow(result.key, result.version);
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

  /** VZ1: fetch the F2 dataflow snapshot for the workflow's saved version. */
  private loadDataflow(key: string, version: number): void {
    if (!key || !Number.isFinite(version) || version <= 0) {
      this.dataflowSnapshot.set(null);
      this.dataflowVersion.set(null);
      this.dataflowDirty.set(false);
      return;
    }
    this.dataflowLoading.set(true);
    this.dataflowError.set(null);
    this.api.getDataflow(key, version).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: snapshot => {
        this.dataflowSnapshot.set(snapshot);
        this.dataflowVersion.set(version);
        this.dataflowDirty.set(false);
        this.dataflowLoading.set(false);
      },
      error: err => {
        this.dataflowSnapshot.set(null);
        this.dataflowLoading.set(false);
        this.dataflowError.set(`Failed to load data-flow analysis: ${err?.message ?? err}`);
      }
    });
  }

  /** VZ1: select a node by its persistence Guid (used by clickable variable sources to
   *  navigate from a downstream consumer to the upstream writer). Falls back silently if
   *  the target node has been deleted from the editor. */
  navigateToSourceNode(persistenceNodeId: string): void {
    if (!this.editor || !this.area) return;
    const target = this.editor.getNodes().find(n => n.nodeId === persistenceNodeId);
    if (!target) return;
    this.selectedNodeId.set(target.id);
    this.loadAgentDocsForNode(target);
    this.loadSynthesizerDocsForNode(target);
    AreaExtensions.zoomAt(this.area, [target]);
  }

  /** Resolve a persistence-Guid node id to a human-readable label for the inspector. */
  labelForSource(persistenceNodeId: string): string {
    if (!this.editor) return persistenceNodeId.slice(0, 8);
    const target = this.editor.getNodes().find(n => n.nodeId === persistenceNodeId);
    return target ? target.label : persistenceNodeId.slice(0, 8);
  }
}
