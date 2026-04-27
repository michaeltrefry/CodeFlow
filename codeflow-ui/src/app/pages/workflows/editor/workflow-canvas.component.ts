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
import { ActivatedRoute, Router } from '@angular/router';
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
  WorkflowSummary
} from '../../../core/models';
import { WorkflowsApi } from '../../../core/workflows.api';
import { ButtonComponent } from '../../../ui/button.component';
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
import { MonacoMarker, MonacoScriptEditorComponent } from './monaco-script-editor.component';
import { NodeContextMenuComponent, NodeContextMenuItem } from './node-context-menu.component';
import {
  AgentInPlaceEditDialogComponent,
  InPlaceEditResult,
  InPlaceEditTarget
} from './agent-in-place-edit-dialog.component';
import {
  PublishForkDialogComponent,
  PublishForkResult,
  PublishForkTarget
} from './publish-fork-dialog.component';
import {
  VersionUpdateDialogComponent,
  VersionUpdateResult,
  VersionUpdateTarget
} from './version-update-dialog.component';

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
  imports: [CommonModule, FormsModule, MonacoScriptEditorComponent, TagInputComponent, ButtonComponent, NodeContextMenuComponent, AgentInPlaceEditDialogComponent, PublishForkDialogComponent, VersionUpdateDialogComponent],
  changeDetection: ChangeDetectionStrategy.Default,
  template: `
    <div class="editor-layout">
      <main class="canvas-wrapper">
        <div class="toolbar">
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
          <div class="toolbar-actions">
            <button type="button" cf-button variant="ghost" size="sm" (click)="tidy()">Tidy up</button>
            <button type="button" cf-button variant="ghost" size="sm" (click)="cancel()">Cancel</button>
            <button type="button" cf-button variant="primary" size="sm" (click)="save()" [disabled]="saving()">
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
                <div class="tag error">{{ selectedAgentDocsError() }}</div>
              } @else {
                @for (row of selectedPortReferences(); track row.port) {
                  <article class="port-reference-row" [class.blank]="row.source === 'blank'">
                    <div class="port-reference-meta">
                      <span class="mono port-name">{{ row.port }}</span>
                      @if (row.source === 'payload') {
                        <span class="tag small success">payload example</span>
                      } @else if (row.source === 'template') {
                        <span class="tag small">decision template</span>
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
            <button type="button" class="palette-item hitl" (click)="addPaletteNode('Hitl')">HITL</button>
            <button type="button" class="palette-item subflow" (click)="addPaletteNode('Subflow')">Subflow</button>
            <button type="button" class="palette-item reviewloop" (click)="addPaletteNode('ReviewLoop')">Review Loop</button>
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
                            <span class="tag error xsmall" title="Wired on this node but the pinned agent can't submit it. Agents reaching this port at runtime would crash.">stale (not declared by agent)</span>
                          } @else if (p.status === 'missing') {
                            <span class="tag warn xsmall" title="Declared by the pinned agent but missing from this node. Submissions to this port would route nowhere (dead branch).">missing on node</span>
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
                    (valueChange)="onNodeScriptChanged(sel.editor, 'input', $event)"></cf-monaco-script-editor>
                </div>
                <div class="row">
                  <button type="button" (click)="validateNodeScript(sel.editor, 'input')">Validate</button>
                  @if (inputScriptValidationError()) {
                    <span class="tag error">{{ inputScriptValidationError() }}</span>
                  } @else if (inputScriptValidationOk()) {
                    <span class="tag success">Script parses OK</span>
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
                    (valueChange)="onNodeScriptChanged(sel.editor, 'output', $event)"></cf-monaco-script-editor>
                </div>
                <div class="row">
                  <button type="button" (click)="validateNodeScript(sel.editor, 'output')">Validate</button>
                  @if (scriptValidationError()) {
                    <span class="tag error">{{ scriptValidationError() }}</span>
                  } @else if (scriptValidationOk()) {
                    <span class="tag success">Script parses OK</span>
                  }
                </div>
              </div>
            }

            @if (sel.editor.kind === 'Subflow') {
              <div class="inspector-section">
                <label class="field">
                  <span>Workflow <span class="muted xsmall">(the subflow to invoke)</span></span>
                  <select [ngModel]="sel.editor.subflowKey ?? ''"
                          (ngModelChange)="onSubflowKeyChanged(sel.editor, $event)">
                    <option value="">(pick workflow)</option>
                    @for (wf of availableSubflowTargets(); track wf.key) {
                      <option [value]="wf.key">{{ wf.key }} (v{{ wf.latestVersion }})</option>
                    }
                  </select>
                  @if (sel.editor.subflowKey && sel.editor.subflowKey === workflowKey()) {
                    <span class="tag error xsmall">Self-reference — save will be rejected.</span>
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
                          <li><span class="tag small">{{ n.kind }}</span> <span class="mono xsmall">{{ labelForOutline(n) }}</span></li>
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
                  <span>Child workflow <span class="muted xsmall">(re-invoked every round)</span></span>
                  <select [ngModel]="sel.editor.subflowKey ?? ''"
                          (ngModelChange)="onSubflowKeyChanged(sel.editor, $event)">
                    <option value="">(pick workflow)</option>
                    @for (wf of availableSubflowTargets(); track wf.key) {
                      <option [value]="wf.key">{{ wf.key }} (v{{ wf.latestVersion }})</option>
                    }
                  </select>
                  @if (sel.editor.subflowKey && sel.editor.subflowKey === workflowKey()) {
                    <span class="tag error xsmall">Self-reference — save will be rejected.</span>
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
                          <li><span class="tag small">{{ n.kind }}</span> <span class="mono xsmall">{{ labelForOutline(n) }}</span></li>
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
                    (valueChange)="onNodeScriptChanged(sel.editor, 'output', $event)"></cf-monaco-script-editor>
                </div>
                <div class="row">
                  <button type="button" (click)="validateNodeScript(sel.editor, 'output')">Validate</button>
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
          } @else if (selectedConnection(); as sel) {
            <div class="inspector-section">
              <div class="row-spread">
                <div class="inspector-kind connection">Wire</div>
                <button type="button" class="danger small" (click)="removeSelectedConnection()">Delete wire</button>
              </div>
              <div class="muted xsmall">
                <code class="mono">{{ connectionSummary(sel.editor) }}</code>
              </div>
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
      [target]="editTarget()"
      [suppressWarning]="warningSuppressed()"
      (close)="closeEditInPlace()"
      (saved)="onEditInPlaceSaved($event)"
      (warningSuppressed)="warningSuppressed.set(true)"></cf-agent-in-place-edit-dialog>

    <cf-publish-fork-dialog
      [target]="publishTarget()"
      (close)="closePublishFork()"
      (published)="onForkPublished($event)"></cf-publish-fork-dialog>

    <cf-version-update-dialog
      [target]="versionUpdateTarget()"
      (confirmed)="onVersionUpdateConfirmed($event)"
      (cancelled)="versionUpdateTarget.set(null)"></cf-version-update-dialog>
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
      justify-content: space-between;
      align-items: flex-end;
      padding: 0.75rem 1rem;
      border-bottom: 1px solid var(--border);
      gap: 1rem;
      flex-wrap: wrap;
    }
    .toolbar-fields {
      display: flex;
      gap: 0.75rem;
      flex-wrap: wrap;
      align-items: flex-end;
      flex: 1;
      min-width: 0;
    }
    .toolbar-actions {
      display: flex;
      gap: 0.4rem;
      align-items: center;
      padding-bottom: 1px;
    }
    .tb-field {
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
      min-width: 160px;
    }
    .tb-field.tb-narrow { min-width: 110px; max-width: 160px; }
    .tb-field.tb-tags { flex: 1; min-width: 260px; }
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
    .palette-item.hitl { border-left: 4px solid #bc8cff; }
    .palette-item.escalation { border-left: 4px solid #f85149; }
    .palette-item.subflow { border-left: 4px solid #2ea3f2; }
    .palette-item.reviewloop { border-left: 4px solid #f5a623; }
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
    .tag {
      background: rgba(88, 166, 255, 0.14);
      color: #58a6ff;
      padding: 0.2rem 0.4rem;
      border-radius: 3px;
      font-size: 0.75rem;
    }
    .tag.error { background: rgba(248, 81, 73, 0.15); color: #f85149; padding: 0.2rem 0.4rem; border-radius: 3px; font-size: 0.75rem; }
    .tag.warn { background: rgba(245, 184, 76, 0.18); color: #f5b84c; padding: 0.2rem 0.4rem; border-radius: 3px; font-size: 0.75rem; }
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
  readonly editTarget = signal<InPlaceEditTarget | null>(null);
  readonly warningSuppressed = signal(false);
  readonly publishTarget = signal<PublishForkTarget | null>(null);
  readonly versionUpdateTarget = signal<VersionUpdateTarget | null>(null);
  readonly selectedAgentDocs = signal<SelectedAgentDocs | null>(null);
  readonly selectedAgentDocsLoading = signal(false);
  readonly selectedAgentDocsError = signal<string | null>(null);

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
  readonly derivedPortRows = computed<{ name: string; status: 'ok' | 'stale' | 'missing' }[]>(() => {
    this.portsRevision();
    const sel = this.selectedNode();
    if (!sel) return [];
    if (!AGENT_BEARING_KINDS.has(sel.editor.kind)) return [];

    const docs = this.selectedAgentDocs();
    const declared = Array.isArray(docs?.config.outputs)
      ? docs!.config.outputs
          .map(o => o.kind)
          .filter((k): k is string => typeof k === 'string' && k.length > 0 && k !== 'Failed')
      : null;
    const declaredSet = declared ? new Set(declared) : null;
    const nodePorts = sel.editor.outputPortNames;
    const nodePortSet = new Set(nodePorts);

    const rows: { name: string; status: 'ok' | 'stale' | 'missing' }[] = nodePorts.map(name => ({
      name,
      status: declaredSet
        ? (declaredSet.has(name) ? 'ok' : 'stale')
        : 'ok',
    }));

    if (declared) {
      for (const name of declared) {
        if (!nodePortSet.has(name)) {
          rows.push({ name, status: 'missing' });
        }
      }
    }

    return rows;
  });

  /** True when the selected agent-bearing node's ports drift from its pinned agent's declared
   *  outputs in either direction (stale or missing). Drives the "Sync from agent" affordance. */
  readonly selectedNodeHasPortDrift = computed(() => {
    return this.derivedPortRows().some(r => r.status !== 'ok');
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

    request$.subscribe({
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
    this.agentsApi.list().subscribe({
      next: agents => this.agents.set(agents),
      error: err => this.error.set(`Failed to load agents: ${err.message ?? err}`)
    });
    this.api.list().subscribe({
      next: workflows => this.workflows.set(workflows),
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
        if ((picked?.kind === 'Subflow' || picked?.kind === 'ReviewLoop') && picked.subflowKey) {
          this.api.getLatest(picked.subflowKey).subscribe({
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

    const key = this.route.snapshot.paramMap.get('key');
    if (key) {
      this.hasExistingKey.set(true);
      this.workflowKey.set(key);
      this.api.getLatest(key).subscribe({
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
    if (!this.selectedConnectionId() && !this.selectedNodeId()) return;
    event.preventDefault();
    if (this.selectedConnectionId()) {
      void this.removeSelectedConnection();
      return;
    }
    void this.removeSelectedNode();
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

  onCanvasContextMenu(event: MouseEvent): void {
    const nodeId = this.findNodeIdAtEvent(event);
    if (!nodeId || !this.editor) return;

    const node = this.editor.getNode(nodeId) as WorkflowEditorNode | undefined;
    if (!node) return;

    event.preventDefault();
    this.selectedNodeId.set(nodeId);
    this.loadAgentDocsForNode(node);

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

    if (AGENT_BEARING_KINDS.has(node.kind) && node.agentKey) {
      const agentKey = node.agentKey;
      const fromVersion = node.agentVersion ?? 0;
      this.agentsApi.getLatest(agentKey).subscribe({
        next: latest => {
          if (!latest.version || latest.version <= fromVersion) {
            this.statusMessage.set(`Agent '${agentKey}' v${fromVersion} is already the latest.`);
            return;
          }
          const latestPorts = (latest.config?.outputs ?? [])
            .map(o => o.kind)
            .filter((k): k is string => typeof k === 'string' && k.length > 0);
          this.versionUpdateTarget.set({
            nodeId: node.id,
            kind: 'agent',
            refKey: agentKey,
            fromVersion,
            toVersion: latest.version,
            currentPorts: node.outputPortNames.slice(),
            latestPorts,
            outgoing,
          });
        },
        error: err => this.error.set(`Failed to load latest agent version: ${err?.message ?? err}`),
      });
      return;
    }

    if ((node.kind === 'Subflow' || node.kind === 'ReviewLoop') && node.subflowKey) {
      const subflowKey = node.subflowKey;
      const fromVersion = node.subflowVersion ?? 0;
      this.api.getLatest(subflowKey).subscribe({
        next: latest => {
          if (!latest.version || latest.version <= fromVersion) {
            this.statusMessage.set(`Workflow '${subflowKey}' v${fromVersion} is already the latest.`);
            return;
          }
          this.api.getTerminalPorts(subflowKey, latest.version).subscribe({
            next: terminals => {
              let latestPorts = terminals.slice();
              if (node.kind === 'ReviewLoop') {
                const loopDecision = (node.loopDecision?.trim()) || 'Rejected';
                latestPorts = latestPorts.filter(p => p !== loopDecision);
                if (!latestPorts.includes('Exhausted')) latestPorts.push('Exhausted');
              }
              this.versionUpdateTarget.set({
                nodeId: node.id,
                kind: 'workflow',
                refKey: subflowKey,
                fromVersion,
                toVersion: latest.version,
                currentPorts: node.outputPortNames.slice(),
                latestPorts,
                outgoing,
              });
            },
            error: err => this.error.set(`Failed to load latest workflow's terminal ports: ${err?.message ?? err}`),
          });
        },
        error: err => this.error.set(`Failed to load latest workflow version: ${err?.message ?? err}`),
      });
    }
  }

  onVersionUpdateConfirmed(result: VersionUpdateResult): void {
    const node = this.editor?.getNode(result.nodeId) as WorkflowEditorNode | undefined;
    if (!node) {
      this.versionUpdateTarget.set(null);
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
    this.versionUpdateTarget.set(null);
    this.statusMessage.set(`Updated to v${result.toVersion}.`);
  }

  closeContextMenu(): void {
    if (this.contextMenu()) this.contextMenu.set(null);
  }

  protected openEditInPlace(node: WorkflowEditorNode): void {
    if (!node.agentKey) return;
    const workflowKey = this.workflowKey().trim();
    if (!workflowKey) {
      this.error.set('Pick a workflow key before editing an agent in place.');
      return;
    }

    const isExistingFork = node.agentKey.startsWith(FORK_KEY_PREFIX);
    const load$ = node.agentVersion
      ? this.agentsApi.getVersion(node.agentKey, node.agentVersion)
      : this.agentsApi.getLatest(node.agentKey);

    load$.subscribe({
      next: version => {
        const config = (version.config ?? {}) as import('../../../core/models').AgentConfig;
        const resolvedType = version.type === 'hitl' ? 'hitl' : 'agent';
        this.editTarget.set({
          nodeId: node.id,
          agentKey: version.key,
          agentVersion: version.version,
          workflowKey,
          initialConfig: config,
          initialType: resolvedType,
          isExistingFork
        });
      },
      error: err => this.error.set(`Failed to load agent for in-place edit: ${err?.message ?? err}`)
    });
  }

  protected openPublishFork(node: WorkflowEditorNode): void {
    if (!node.agentKey || !node.agentKey.startsWith(FORK_KEY_PREFIX)) return;
    this.publishTarget.set({ nodeId: node.id, forkKey: node.agentKey });
  }

  closePublishFork(): void {
    this.publishTarget.set(null);
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
    this.publishTarget.set(null);
  }

  closeEditInPlace(): void {
    this.editTarget.set(null);
  }

  onEditInPlaceSaved(result: InPlaceEditResult): void {
    if (!this.editor) {
      this.editTarget.set(null);
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
    this.editTarget.set(null);
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

    this.api.getLatest(value).subscribe({
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

  /** Fetch the child workflow's terminal ports and apply them to this Subflow / ReviewLoop
   *  node. ReviewLoop additionally synthesizes `Exhausted` and excludes the configured
   *  loopDecision (since that one iterates rather than exits). */
  private refreshSubflowPorts(node: WorkflowEditorNode): void {
    if (node.kind !== 'Subflow' && node.kind !== 'ReviewLoop') return;
    const subflowKey = node.subflowKey;
    if (!subflowKey) return;

    this.api.getTerminalPorts(subflowKey, node.subflowVersion ?? null).subscribe({
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

    this.agentsApi.getLatest(value).subscribe({
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
    const docs = this.selectedAgentDocs();
    const declared = Array.isArray(docs?.config.outputs)
      ? docs!.config.outputs
          .map(o => o.kind)
          .filter((k): k is string => typeof k === 'string' && k.length > 0 && k !== 'Failed')
      : null;
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

  private applyConnectionStyles(connectionId: string): void {
    const registered = this.connectionElements.get(connectionId);
    const connection = this.editor?.getConnection(connectionId) as WorkflowEditorConnection | undefined;
    if (!registered || !connection) return;

    const path = registered.element.querySelector('path') as SVGPathElement | null;
    if (!path) return;

    path.style.cursor = 'pointer';
    path.style.pointerEvents = 'auto';
    path.style.transition = 'stroke 120ms ease, stroke-width 120ms ease, filter 120ms ease';
    path.style.stroke = connection.isSelected ? '#ffd166' : '#4682b4';
    path.style.strokeWidth = connection.isSelected ? '7px' : '5px';
    path.style.filter = connection.isSelected ? 'drop-shadow(0 0 6px rgba(255, 209, 102, 0.45))' : '';
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
    }).subscribe({
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
