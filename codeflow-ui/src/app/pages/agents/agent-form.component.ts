import {
  Component,
  EventEmitter,
  Output,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  OnDestroy,
  OnInit
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
import { LlmProvidersApi } from '../../core/llm-providers.api';
import {
  AgentConfig,
  AgentOutputDeclaration,
  AuthorableHistoryMessage,
  AuthorableHistoryRole,
  DecisionOutputTemplateMode,
  LLM_PROVIDER_DISPLAY_NAMES,
  LLM_PROVIDER_KEYS,
  LlmProviderKey,
  LlmProviderModelOption,
  PromptPartialPinDto,
  PromptTemplatePreviewAutoInjection,
  PromptTemplatePreviewMissingPartial,
} from '../../core/models';
import { FORM_PRESETS, FormPresetKey, getFormPreset } from './form-presets';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';
import { CardComponent } from '../../ui/card.component';
import { TabsComponent, TabItem } from '../../ui/tabs.component';
import { MonacoScriptEditorComponent } from '../workflows/editor/monaco-script-editor.component';

interface DecisionTemplateRow {
  port: string;
  template: string;
  preview: string;
  previewError: string | null;
  previewPending: boolean;
}

interface OutputRow {
  kind: string;
  description: string | null;
  payloadExample: unknown;
  template: string;
  preview: string;
  previewError: string | null;
  previewPending: boolean;
  expanded: boolean;
}

type EditorTab = 'identity' | 'prompt' | 'preview' | 'model' | 'outputs';

export interface AgentFormSaveRequest {
  key: string;
  type: 'agent' | 'hitl';
  config: AgentConfig;
}

export interface AgentFormHeaderState {
  type: 'agent' | 'hitl';
  provider: LlmProviderKey;
  model: string;
}

@Component({
  selector: 'cf-agent-form',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink,
    ButtonComponent, ChipComponent, CardComponent, TabsComponent,
    MonacoScriptEditorComponent,
  ],
  template: `
    <cf-tabs [items]="tabs()" [value]="tab()" (valueChange)="setTab($event)"></cf-tabs>

    <form (submit)="submit($event)">
        @if (tab() === 'identity') {
          <cf-card>
            <div class="form-section">
              <div class="form-section-head">
                <h3>Identity</h3>
                <p>Key is immutable once created. Rename the display name freely.</p>
              </div>
              <div class="form-grid">
                <label class="field">
                  <span class="field-label">Agent key</span>
                  <input class="input mono" [(ngModel)]="key" name="key"
                         [disabled]="!!existingKey()" required placeholder="reviewer-v1" />
                  <span class="field-hint">Lowercase letters, digits, '-' or '_'.</span>
                </label>
                <label class="field">
                  <span class="field-label">Display name</span>
                  <input class="input" [(ngModel)]="name" name="name" placeholder="Technical reviewer" />
                </label>
                <label class="field">
                  <span class="field-label">Type</span>
                  <div class="seg" style="width: fit-content">
                    <button type="button" [attr.data-active]="type() === 'agent' ? 'true' : null" (click)="type.set('agent')">LLM agent</button>
                    <button type="button" [attr.data-active]="type() === 'hitl' ? 'true' : null" (click)="type.set('hitl')">HITL</button>
                  </div>
                </label>
                <label class="field span-2">
                  <span class="field-label">Description</span>
                  <textarea class="textarea" [(ngModel)]="description" name="description" rows="2"
                            style="font-family: var(--font-sans); font-size: var(--fs-md)"></textarea>
                </label>
              </div>
            </div>
          </cf-card>
        }

        @if (tab() === 'prompt') {
          @if (type() === 'agent') {
            <cf-card>
              <div class="form-section">
                <div class="form-section-head">
                  <h3>System prompt</h3>
                  <p>Prepended to every call. Keep instruction-focused and stable.</p>
                </div>
                <div class="code-field">
                  <div class="code-field-head"><span>system.md</span><span>markdown</span></div>
                  <textarea class="textarea" rows="8"
                            [(ngModel)]="systemPrompt" name="systemPrompt"
                            style="border: 0; border-radius: 0; background: var(--bg)"></textarea>
                </div>
              </div>
              <div class="form-section">
                <div class="form-section-head">
                  <h3>History
                    @if (history().length > 0) {
                      <cf-chip mono>{{ history().length }}</cf-chip>
                    }
                  </h3>
                  <p>
                    Optional pre-canned conversation prepended to every invocation between the system
                    block and the latest user input. Use it for few-shot examples or stable conversational
                    priming. Limited to {{ HISTORY_MAX_ENTRIES }} messages and {{ HISTORY_MAX_TOTAL_LENGTH / 1024 }} KiB combined.
                  </p>
                </div>
                <div class="stack history-stack">
                  @for (msg of history(); track $index; let i = $index) {
                    <div class="history-row">
                      <div class="history-row-head">
                        <select class="input mono history-role"
                                [ngModel]="msg.role"
                                (ngModelChange)="updateHistoryMessage(i, { role: $event })"
                                [name]="'history_role_' + i"
                                [attr.aria-label]="'History message ' + (i + 1) + ' role'">
                          @for (opt of HISTORY_ROLE_OPTIONS; track opt.value) {
                            <option [value]="opt.value">{{ opt.label }}</option>
                          }
                        </select>
                        <button type="button" cf-button variant="ghost" size="sm" icon="x" iconOnly
                                (click)="removeHistoryMessage(i)"
                                [attr.aria-label]="'Remove history message ' + (i + 1)"></button>
                      </div>
                      <textarea class="textarea history-content" rows="3"
                                [ngModel]="msg.content"
                                (ngModelChange)="updateHistoryMessage(i, { content: $event })"
                                [name]="'history_content_' + i"
                                [placeholder]="msg.role === 'user' ? 'What the user said earlier…' : 'How the assistant responded…'"></textarea>
                    </div>
                  }
                  <div class="row history-footer">
                    <button type="button" cf-button size="sm" icon="plus"
                            [disabled]="history().length >= HISTORY_MAX_ENTRIES"
                            (click)="addHistoryMessage()">Add message</button>
                    @if (historyTotalLength() > 0) {
                      <span class="muted small">
                        {{ historyTotalLength() }} / {{ HISTORY_MAX_TOTAL_LENGTH }} chars
                      </span>
                    }
                    @if (historyTotalLength() > HISTORY_MAX_TOTAL_LENGTH) {
                      <cf-chip variant="err" dot>Too long — trim before saving</cf-chip>
                    }
                  </div>
                </div>
              </div>
              <div class="form-section">
                <div class="form-section-head">
                  <h3>Prompt template</h3>
                  <p>
                    Rendered per-round with Scriban substitution plus conditionals and loops —
                    see the <a class="mono-link" href="https://github.com/michaeltrefry/CodeFlow/blob/main/docs/prompt-templates.md" target="_blank" rel="noopener noreferrer">prompt template guide ↗</a>.
                  </p>
                </div>
                <div class="code-field">
                  <div class="code-field-head"><span>input.scriban</span><span>scriban</span></div>
                  <cf-monaco-script-editor
                    class="prompt-template-editor"
                    language="plaintext"
                    [value]="promptTemplate()"
                    [templateCompletion]="true"
                    (valueChange)="promptTemplate.set($event)"></cf-monaco-script-editor>
                </div>
              </div>
            </cf-card>
          } @else {
            <cf-card>
              @if (canShowFormPresets()) {
                <div class="form-section preset-picker">
                  <div class="form-section-head">
                    <h3>Start from a preset</h3>
                    <p>Templates for the most common HITL form shapes. Apply a preset to seed the output template, ports, and per-port templates — then customize from there.</p>
                  </div>
                  <div class="preset-grid">
                    @for (preset of formPresets; track preset.key) {
                      <div class="preset-card">
                        <div class="preset-card-head">
                          <h4>{{ preset.label }}</h4>
                          <p class="muted small">{{ preset.summary }}</p>
                        </div>
                        @if (preset.key === 'edit-then-approve') {
                          <label class="field">
                            <span class="field-label small">Pre-fill from workflow variable</span>
                            <input class="input mono" type="text"
                                   [ngModel]="presetEditSourceKey()"
                                   (ngModelChange)="presetEditSourceKey.set($event)"
                                   name="presetEditSourceKey" placeholder="draft" />
                          </label>
                        }
                        @if (preset.key === 'multi-action') {
                          <label class="field">
                            <span class="field-label small">Port names <span class="muted">(comma-separated)</span></span>
                            <input class="input mono" type="text"
                                   [ngModel]="presetMultiActionPorts()"
                                   (ngModelChange)="presetMultiActionPorts.set($event)"
                                   name="presetMultiActionPorts" placeholder="Approved, Rejected" />
                          </label>
                        }
                        <button type="button" cf-button variant="primary" size="sm" icon="plus"
                                (click)="applyFormPreset(preset.key)">
                          Apply preset
                        </button>
                      </div>
                    }
                  </div>
                </div>
              }
              <div class="form-section">
                <div class="form-section-head">
                  <h3>Output template</h3>
                  <p>
                    Defines exactly how reviewers enter their response. Use <code>{{ '{{name}}' }}</code> for free text,
                    <code>{{ '{{name:Opt1|Opt2}}' }}</code> for a dropdown, <code>{{ '{{decision}}' }}</code> for decision choice,
                    or <code>{{ '{{json(name)}}' }}</code> when the artifact should be JSON.
                  </p>
                </div>
                <div class="code-field">
                  <div class="code-field-head"><span>hitl.template</span><span>form</span></div>
                  <cf-monaco-script-editor
                    class="hitl-template-editor"
                    language="plaintext"
                    [value]="outputTemplate()"
                    [templateCompletion]="true"
                    (valueChange)="outputTemplate.set($event)"></cf-monaco-script-editor>
                </div>
              </div>
            </cf-card>
          }
        }

        @if (tab() === 'preview' && type() === 'agent') {
          <cf-card>
            <div class="form-section">
              <div class="form-section-head">
                <h3>Live preview</h3>
                <p>
                  Renders the system prompt and prompt template the model would actually receive,
                  given the sample scope below. Typos like <code>{{ '{{ wokflow.foo }}' }}</code>
                  render verbatim so you can spot them before runtime. Auto-injected partials —
                  for example <code>@codeflow/last-round-reminder</code> when this agent runs in a
                  ReviewLoop — appear in their own annotated block.
                </p>
              </div>

              <label class="field">
                <span class="field-label">Sample scope <span class="muted small">(JSON: workflow, context, input, reviewRound, reviewMaxRounds, optOutLastRoundReminder)</span></span>
                <textarea class="textarea mono" rows="9"
                          [ngModel]="promptPreviewScopeText()"
                          (ngModelChange)="promptPreviewScopeText.set($event)"
                          name="promptPreviewScope"></textarea>
                @if (promptPreviewScopeError()) {
                  <cf-chip variant="err" dot>{{ promptPreviewScopeError() }}</cf-chip>
                }
              </label>

              @if (promptPreviewMissingPartials().length > 0) {
                <div class="preview-missing-partials">
                  <strong>Missing partials:</strong>
                  @for (mp of promptPreviewMissingPartials(); track mp.key) {
                    <cf-chip variant="err" mono>{{ mp.key }} v{{ mp.version }}</cf-chip>
                  }
                  <p class="muted small">
                    Pin these to a valid version on the agent or remove the include from the prompt.
                  </p>
                </div>
              }

              @if (promptPreviewError(); as err) {
                <div class="form-section">
                  <div class="form-section-head"><h4>Render error</h4></div>
                  <pre class="preview-error mono">{{ err }}</pre>
                </div>
              } @else {
                <div class="form-section">
                  <div class="form-section-head">
                    <h4>System prompt
                      @if (promptPreviewPending()) { <cf-chip mono>rendering…</cf-chip> }
                    </h4>
                  </div>
                  <pre class="preview-output mono">{{ promptPreviewSystem() || '(empty)' }}</pre>
                </div>

                <div class="form-section">
                  <div class="form-section-head"><h4>Prompt template</h4></div>
                  <pre class="preview-output mono">{{ promptPreviewBody() || '(empty)' }}</pre>
                </div>

                @for (inj of promptPreviewAutoInjections(); track inj.key) {
                  <div class="form-section preview-injection">
                    <div class="form-section-head">
                      <h4>
                        <cf-chip variant="accent" mono>[auto-injected]</cf-chip>
                        {{ inj.key }}
                      </h4>
                      <p class="muted small">{{ inj.reason }}</p>
                    </div>
                    <pre class="preview-output mono">{{ inj.renderedBody || '(empty)' }}</pre>
                  </div>
                }
              }
            </div>
          </cf-card>
        }

        @if (tab() === 'model' && type() === 'agent') {
          <cf-card>
            <div class="form-section">
              <div class="form-section-head">
                <h3>Model</h3>
                <p>Provider, model id and generation parameters.</p>
              </div>
              <div class="form-grid">
                <label class="field">
                  <span class="field-label">Provider</span>
                  <select class="select" [(ngModel)]="provider" name="provider">
                    @for (providerKey of LLM_PROVIDER_KEYS; track providerKey) {
                      <option [value]="providerKey">{{ providerDisplayName(providerKey) }}</option>
                    }
                  </select>
                </label>
                <label class="field">
                  <span class="field-label">Model</span>
                  @if (availableModels().length > 0) {
                    <select class="select mono" [ngModel]="model()" (ngModelChange)="model.set($event)" name="model">
                      @for (option of modelOptions(); track option.value) {
                        <option [value]="option.value">{{ option.label }}</option>
                      }
                    </select>
                    @if (modelNotConfigured()) {
                      <span class="field-hint">
                        Current value <code>{{ model() }}</code> isn't in the configured list. Add it on the
                        <a routerLink="/settings/llm-providers" class="mono-link">LLM providers page ↗</a> to keep it as a canonical choice.
                      </span>
                    } @else {
                      <span class="field-hint">
                        Manage the choices on the <a routerLink="/settings/llm-providers" class="mono-link">LLM providers page ↗</a>.
                      </span>
                    }
                  } @else {
                    <input class="input mono" [(ngModel)]="model" name="model" placeholder="gpt-5.4" />
                    <span class="field-hint">
                      No models configured for {{ provider() }} yet — add some on the
                      <a routerLink="/settings/llm-providers" class="mono-link">LLM providers page ↗</a> to get a dropdown.
                    </span>
                  }
                </label>
                <label class="field">
                  <span class="field-label">Temperature</span>
                  <input class="input mono" type="number" step="0.1" min="0" max="2"
                         [ngModel]="temperature() ?? null"
                         (ngModelChange)="temperature.set(coerceOptionalNumber($event))"
                         name="temperature" autocomplete="off" />
                  <span class="field-hint">0.0 for deterministic routing; 0.7 for creative synthesis.</span>
                </label>
                <label class="field">
                  <span class="field-label">Max output tokens</span>
                  <input class="input mono" type="number" min="1"
                         [ngModel]="maxTokens() ?? null"
                         (ngModelChange)="maxTokens.set(coerceOptionalNumber($event))"
                         name="maxTokens" autocomplete="off" />
                </label>
              </div>
            </div>
          </cf-card>
        }

        @if (tab() === 'model' && type() === 'hitl') {
          <cf-card>
            <div class="card-body muted">
              HITL agents don't use a model. Switch to <strong>LLM agent</strong> on the Identity tab to configure a provider and model.
            </div>
          </cf-card>
        }

        @if (tab() === 'outputs') {
          <cf-card>
            <div class="form-section">
              <div class="form-section-head">
                <h3>Decisions</h3>
                <p>
                  Each decision this agent emits becomes an output port on workflow nodes. Optionally attach a
                  Scriban <strong>decision template</strong> to reshape the artifact passed downstream, and a
                  <strong>payload example</strong> that documents the JSON shape the agent produces.
                  Baseline agents at least emit <code>Completed</code> and <code>Failed</code>. See the
                  <a class="mono-link" href="https://github.com/michaeltrefry/CodeFlow/blob/main/docs/decision-output-templates.md" target="_blank" rel="noopener noreferrer">decision template guide ↗</a>.
                  A routing script that calls <code>setOutput()</code> still wins over any template here.
                </p>
              </div>

              <label class="field">
                <span class="field-label">Preview scope <span class="muted small">(JSON matching the template variables below)</span></span>
                <textarea class="textarea mono" rows="5"
                          [ngModel]="previewContextText()"
                          (ngModelChange)="previewContextText.set($event)"
                          name="decisionTemplatePreviewContext"
                          [placeholder]="previewContextPlaceholder()"></textarea>
                @if (previewContextError()) {
                  <cf-chip variant="err" dot>{{ previewContextError() }}</cf-chip>
                }
              </label>

              <div class="stack" style="margin-top: 14px">
                @for (output of outputs(); track $index; let i = $index) {
                  <div class="output-card">
                    <div class="row" style="justify-content: space-between">
                      <cf-chip mono>{{ output.kind || '(unnamed)' }}</cf-chip>
                      <button type="button" cf-button variant="ghost" size="sm" icon="x" iconOnly
                              (click)="removeOutput(i)" [attr.aria-label]="'Remove output ' + output.kind"></button>
                    </div>
                    <div class="form-grid">
                      <label class="field">
                        <span class="field-label">Kind</span>
                        <input class="input mono" type="text"
                               [ngModel]="output.kind"
                               (ngModelChange)="updateOutput(i, { kind: $event })"
                               [name]="'output_kind_' + i" placeholder="Completed" />
                        <span class="field-hint">Becomes the port name on workflow edges.</span>
                      </label>
                      <label class="field">
                        <span class="field-label">Description</span>
                        <input class="input" type="text"
                               [ngModel]="output.description ?? ''"
                               (ngModelChange)="updateOutput(i, { description: $event || null })"
                               [name]="'output_desc_' + i" placeholder="Normal success" />
                      </label>
                      <div class="field span-2 output-advanced">
                        <button type="button" class="advanced-toggle"
                                [attr.aria-expanded]="output.expanded"
                                (click)="toggleOutputExpanded(i)">
                          <span class="advanced-caret" [attr.data-expanded]="output.expanded ? 'true' : null">▸</span>
                          <span>{{ output.expanded ? 'Hide' : 'Show' }} decision template &amp; payload example</span>
                          @if (!output.expanded && output.template) {
                            <cf-chip mono>template</cf-chip>
                          }
                          @if (!output.expanded && output.payloadExample !== null && output.payloadExample !== undefined) {
                            <cf-chip mono>payload</cf-chip>
                          }
                        </button>
                        @if (output.expanded) {
                          <div class="advanced-body">
                            <label class="field">
                              <span class="field-label">Decision template <span class="muted small">(Scriban — leave blank to pass artifact through unchanged)</span></span>
                              <textarea class="textarea mono" rows="10"
                                        [ngModel]="output.template"
                                        (ngModelChange)="updateOutput(i, { template: $event })"
                                        [name]="'output_template_' + i"
                                        placeholder="[{{ '{{ decision }}' }}] {{ '{{ output.headline }}' }}"></textarea>
                            </label>
                            <label class="field">
                              <span class="field-label">Template preview</span>
                              @if (output.previewError) {
                                <pre class="preview-error mono">{{ output.previewError }}</pre>
                              } @else {
                                <pre class="preview-output mono">{{ output.preview || '(add a template to see the rendered output)' }}</pre>
                              }
                            </label>
                            <label class="field">
                              <div class="row" style="justify-content: space-between; align-items: center">
                                <span class="field-label">Payload example (JSON)</span>
                                <button type="button" cf-button variant="ghost" size="sm" icon="refresh"
                                        [disabled]="!canGeneratePayload(output)"
                                        (click)="generatePayloadFromTemplate(i)">
                                  Generate from template
                                </button>
                              </div>
                              <textarea class="textarea mono" rows="8"
                                        [ngModel]="payloadExampleText(output)"
                                        (ngModelChange)="updatePayloadExample(i, $event)"
                                        [name]="'output_example_' + i"
                                        placeholder='{"reasons": ["..."]}'></textarea>
                            </label>
                          </div>
                        }
                      </div>
                    </div>
                  </div>
                }
                <button type="button" cf-button size="sm" icon="plus" style="align-self: flex-start"
                        (click)="addOutput()">Add decision</button>
              </div>
            </div>

            <div class="form-section">
              <div class="form-section-head">
                <h3>Fallback templates</h3>
                <p>
                  Catch-all templates keyed by <code>*</code> or by a port name not declared above
                  (e.g. ports emitted only by a routing script). Rarely needed.
                </p>
              </div>
              <div class="stack">
                @for (row of fallbackTemplates(); track $index; let i = $index) {
                  <div class="output-card">
                    <div class="row" style="justify-content: space-between">
                      <cf-chip mono>{{ row.port || '(unnamed port)' }}</cf-chip>
                      <button type="button" cf-button variant="ghost" size="sm" icon="x" iconOnly
                              (click)="removeFallbackTemplate(i)" [attr.aria-label]="'Remove template'"></button>
                    </div>
                    <label class="field">
                      <span class="field-label">Output port <span class="muted small">(or <code>*</code>)</span></span>
                      <input class="input mono" type="text"
                             [ngModel]="row.port"
                             (ngModelChange)="updateFallbackTemplate(i, { port: $event })"
                             [name]="'fallback_template_port_' + i" placeholder="*" />
                    </label>
                    <label class="field">
                      <span class="field-label">Template</span>
                      <textarea class="textarea mono" rows="5"
                                [ngModel]="row.template"
                                (ngModelChange)="updateFallbackTemplate(i, { template: $event })"
                                [name]="'fallback_template_body_' + i"
                                placeholder="[{{ '{{ decision }}' }}] {{ '{{ output.headline }}' }}"></textarea>
                    </label>
                    <label class="field">
                      <span class="field-label">Preview</span>
                      @if (row.previewError) {
                        <pre class="preview-error mono">{{ row.previewError }}</pre>
                      } @else {
                        <pre class="preview-output mono">{{ row.preview || '(enter a template to see the rendered output)' }}</pre>
                      }
                    </label>
                  </div>
                }
                <button type="button" cf-button size="sm" icon="plus" style="align-self: flex-start"
                        (click)="addFallbackTemplate()">Add fallback template</button>
              </div>
            </div>
          </cf-card>
        }

    </form>
  `,
  styles: [`
    .output-card {
      padding: 14px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface-2);
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .history-stack { gap: 10px; }
    .history-row {
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 10px 12px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface-2);
    }
    .history-row-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
    }
    .history-role { width: auto; min-width: 120px; }
    .history-content { width: 100%; }
    .history-footer { gap: 12px; align-items: center; }
    .preview-output, .preview-error {
      padding: 10px 12px;
      border-radius: var(--radius);
      background: var(--surface-2);
      border: 1px solid var(--border);
      white-space: pre-wrap;
      word-break: break-word;
      margin: 0;
      font-size: var(--fs-sm);
    }
    .preview-error {
      color: var(--sem-red);
      border-color: color-mix(in oklab, var(--sem-red) 40%, transparent);
      background: var(--err-bg);
    }
    .output-advanced {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .advanced-toggle {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 6px 0;
      background: transparent;
      border: 0;
      color: var(--muted);
      font: inherit;
      cursor: pointer;
      align-self: flex-start;
    }
    .advanced-toggle:hover { color: var(--fg); }
    .advanced-caret {
      display: inline-block;
      transition: transform 0.12s ease-out;
      font-size: 0.9em;
    }
    .advanced-caret[data-expanded="true"] { transform: rotate(90deg); }
    .advanced-body {
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .preview-missing-partials {
      padding: 10px 12px;
      border-radius: var(--radius);
      background: var(--err-bg);
      border: 1px solid color-mix(in oklab, var(--sem-red) 40%, transparent);
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      align-items: center;
    }
    .preview-missing-partials p { width: 100%; margin: 6px 0 0; }
    .preview-injection {
      border-left: 3px solid var(--accent, var(--fg));
      padding-left: 12px;
    }
    .preset-picker {
      background: var(--surface-2);
      border-radius: var(--radius);
      padding: 14px;
      margin-bottom: 12px;
    }
    .preset-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
      gap: 12px;
    }
    .preset-card {
      display: flex;
      flex-direction: column;
      gap: 10px;
      padding: 12px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--bg);
    }
    .preset-card h4 { margin: 0 0 4px; font-size: var(--fs-md); }
    .preset-card-head p { margin: 0; }
    .preset-card .field-label.small { font-size: var(--fs-sm); }
  `]
})
export class AgentFormComponent implements OnInit, OnDestroy {
  private readonly agentsApi = inject(AgentsApi);
  private readonly llmProvidersApi = inject(LlmProvidersApi);
  private readonly destroyRef = inject(DestroyRef);

  readonly existingKey = input<string | undefined>(undefined, { alias: 'key' });
  readonly initialConfig = input<AgentConfig | null>(null);
  readonly initialType = input<'agent' | 'hitl' | null>(null);

  @Output() readonly saveRequested = new EventEmitter<AgentFormSaveRequest>();

  protected readonly key = signal('');
  protected readonly type = signal<'agent' | 'hitl'>('agent');
  protected readonly name = signal('');
  protected readonly description = signal('');
  protected readonly provider = signal<LlmProviderKey>('openai');
  protected readonly model = signal('gpt-5.4');
  protected readonly systemPrompt = signal('');
  protected readonly promptTemplate = signal('');
  protected readonly outputTemplate = signal('');
  protected readonly maxTokens = signal<number | undefined>(undefined);
  protected readonly temperature = signal<number | undefined>(undefined);
  protected readonly outputs = signal<OutputRow[]>([
    emptyOutputRow('Completed'),
    emptyOutputRow('Failed'),
  ]);

  protected readonly fallbackTemplates = signal<DecisionTemplateRow[]>([]);

  protected readonly partialPins = signal<PromptPartialPinDto[]>([]);

  // sc-570: hand-authored chat history prepended to every invocation between the system block
  // and the latest user input. Useful for few-shot examples or canned conversational priming.
  // Roles are limited to user/assistant; system content belongs in `systemPrompt` and tool
  // messages aren't a sensible authoring affordance.
  protected readonly history = signal<AuthorableHistoryMessage[]>([]);
  protected readonly historyTotalLength = computed(() =>
    this.history().reduce((sum, m) => sum + (m.content?.length ?? 0), 0));
  protected readonly HISTORY_MAX_ENTRIES = 32;
  protected readonly HISTORY_MAX_TOTAL_LENGTH = 32 * 1024;
  protected readonly HISTORY_ROLE_OPTIONS: ReadonlyArray<{ value: AuthorableHistoryRole; label: string }> = [
    { value: 'user', label: 'User' },
    { value: 'assistant', label: 'Assistant' },
  ];

  // S2: form-preset picker state. Visible only when type=hitl AND the form looks empty
  // (so editing an existing form doesn't get clobbered by an accidental Apply).
  protected readonly formPresets = FORM_PRESETS;
  protected readonly presetEditSourceKey = signal('draft');
  protected readonly presetMultiActionPorts = signal('Approved, Rejected');
  protected readonly canShowFormPresets = computed(() => {
    if (this.type() !== 'hitl') return false;
    const tpl = this.outputTemplate().trim();
    const outs = this.outputs();
    const isFreshOutputs = outs.length === 2
      && outs[0].kind === 'Completed'
      && outs[1].kind === 'Failed'
      && !outs[0].template && !outs[1].template;
    return tpl.length === 0 && (outs.length === 0 || isFreshOutputs);
  });

  // VZ3: live prompt-template preview state (separate from the decision-template preview).
  protected readonly promptPreviewScopeText = signal(defaultPromptPreviewScope());
  protected readonly promptPreviewScopeError = signal<string | null>(null);
  protected readonly promptPreviewSystem = signal<string | null>(null);
  protected readonly promptPreviewBody = signal<string | null>(null);
  protected readonly promptPreviewAutoInjections = signal<PromptTemplatePreviewAutoInjection[]>([]);
  protected readonly promptPreviewMissingPartials = signal<PromptTemplatePreviewMissingPartial[]>([]);
  protected readonly promptPreviewError = signal<string | null>(null);
  protected readonly promptPreviewPending = signal(false);

  protected readonly previewContextText = signal(defaultLlmPreviewContext());
  protected readonly previewContextPlaceholder = computed(() => {
    if (this.type() === 'hitl') return defaultHitlPreviewContext();
    const sample = pickSampleOutputRow(this.outputs());
    return defaultLlmPreviewContext(
      sample ? coerceSamplePayload(sample.payloadExample) : undefined,
      sample?.kind);
  });
  protected readonly previewContextError = signal<string | null>(null);

  protected readonly configuredModels = signal<LlmProviderModelOption[]>([]);
  protected readonly LLM_PROVIDER_KEYS = LLM_PROVIDER_KEYS;
  protected readonly availableModels = computed(() => {
    const all = this.configuredModels();
    const current = this.provider();
    return all.filter(o => o.provider === current).map(o => o.model);
  });

  protected providerDisplayName(key: LlmProviderKey): string {
    return LLM_PROVIDER_DISPLAY_NAMES[key];
  }
  protected readonly modelNotConfigured = computed(() => {
    const current = this.model();
    if (!current) return false;
    return !this.availableModels().includes(current);
  });
  protected readonly modelOptions = computed(() => {
    const options = this.availableModels().map(value => ({ value, label: value }));
    const current = this.model();
    if (current && !options.some(o => o.value === current)) {
      options.unshift({ value: current, label: `${current} (unconfigured)` });
    }
    return options;
  });

  protected readonly tab = signal<EditorTab>('identity');
  protected readonly tabs = computed<TabItem[]>(() => {
    const items: TabItem[] = [
      { value: 'identity', label: 'Identity' },
      { value: 'prompt', label: this.type() === 'hitl' ? 'Output template' : 'Prompt & output' },
    ];
    if (this.type() === 'agent') {
      items.push({ value: 'preview', label: 'Preview' });
    }
    items.push(
      { value: 'model', label: 'Model' },
      { value: 'outputs', label: 'Decisions', count: this.outputs().length },
    );
    return items;
  });

  readonly headerState = computed<AgentFormHeaderState>(() => ({
    type: this.type(),
    provider: this.provider(),
    model: this.model(),
  }));

  private lastHydrationSignature: string | null = null;

  constructor() {
    effect(() => {
      const key = this.existingKey();
      const type = this.initialType();
      const config = this.initialConfig();
      const signature = JSON.stringify([key ?? '', type ?? '', config ?? null]);
      if (signature === this.lastHydrationSignature) return;

      this.lastHydrationSignature = signature;
      if (key) this.key.set(key);
      if (type) this.type.set(type);
      if (config) {
        this.hydrateFromConfig(config, type ?? 'agent');
      }
    });

    effect(() => {
      const outputRows = this.outputs();
      const fallbackRows = this.fallbackTemplates();
      const contextText = this.previewContextText();
      const mode: DecisionOutputTemplateMode = this.type() === 'hitl' ? 'hitl' : 'llm';

      let parsedContext: Record<string, unknown> = {};
      try {
        const parsed = contextText.trim() ? JSON.parse(contextText) : {};
        if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
          parsedContext = parsed as Record<string, unknown>;
          this.previewContextError.set(null);
        } else {
          this.clearPreviewTimers();
          this.previewSignatures.clear();
          this.previewContextError.set('Preview context must be a JSON object.');
          return;
        }
      } catch {
        this.clearPreviewTimers();
        this.previewSignatures.clear();
        this.previewContextError.set('Preview context is not valid JSON.');
        return;
      }

      outputRows.forEach((row, index) => {
        const key = `output:${index}`;
        if (!row.template.trim()) {
          this.cancelPreviewRender(key);
          this.previewSignatures.delete(key);
          if (row.preview || row.previewError || row.previewPending) {
            this.patchOutputRowSilently(index, { preview: '', previewError: null, previewPending: false });
          }
          return;
        }
        const port = row.kind.trim() || '*';
        const signature = previewSignature(mode, port, row.template, contextText);
        if (this.previewSignatures.get(key) === signature) return;
        this.previewSignatures.set(key, signature);
        this.schedulePreviewRender(
          key,
          row.template, port, mode, parsedContext,
          patch => this.patchOutputRowSilently(index, patch)
        );
      });

      fallbackRows.forEach((row, index) => {
        const key = `fallback:${index}`;
        if (!row.template.trim()) {
          this.cancelPreviewRender(key);
          this.previewSignatures.delete(key);
          if (row.preview || row.previewError || row.previewPending) {
            this.patchFallbackRowSilently(index, { preview: '', previewError: null, previewPending: false });
          }
          return;
        }
        const port = row.port.trim() || '*';
        const signature = previewSignature(mode, port, row.template, contextText);
        if (this.previewSignatures.get(key) === signature) return;
        this.previewSignatures.set(key, signature);
        this.schedulePreviewRender(
          key,
          row.template, port, mode, parsedContext,
          patch => this.patchFallbackRowSilently(index, patch)
        );
      });
    });

    // VZ3: live prompt-template preview re-render. Debounced 200ms; reads systemPrompt/promptTemplate
    // along with the editable scope JSON and pinned partials, and renders against the same Scriban
    // path the runtime invocation consumer uses.
    effect(() => {
      if (this.type() !== 'agent') return;

      const systemPrompt = this.systemPrompt();
      const promptTemplate = this.promptTemplate();
      const scopeText = this.promptPreviewScopeText();
      const pins = this.partialPins();

      let scope: PromptPreviewScope;
      try {
        scope = parsePromptPreviewScope(scopeText);
        this.promptPreviewScopeError.set(null);
      } catch (err) {
        this.cancelPromptPreviewRender();
        this.promptPreviewScopeError.set(err instanceof Error ? err.message : 'Invalid scope.');
        return;
      }

      const signature = JSON.stringify([systemPrompt, promptTemplate, scopeText, pins]);
      if (this.lastPromptPreviewSignature === signature) return;
      this.lastPromptPreviewSignature = signature;
      this.schedulePromptPreviewRender(systemPrompt, promptTemplate, scope, pins);
    });
  }

  private lastPromptPreviewSignature: string | null = null;
  private promptPreviewTimer: ReturnType<typeof setTimeout> | undefined;

  private schedulePromptPreviewRender(
    systemPrompt: string,
    promptTemplate: string,
    scope: PromptPreviewScope,
    pins: PromptPartialPinDto[]
  ): void {
    this.cancelPromptPreviewRender();
    if (!systemPrompt.trim() && !promptTemplate.trim()) {
      this.promptPreviewSystem.set(null);
      this.promptPreviewBody.set(null);
      this.promptPreviewAutoInjections.set([]);
      this.promptPreviewMissingPartials.set([]);
      this.promptPreviewError.set(null);
      this.promptPreviewPending.set(false);
      return;
    }
    this.promptPreviewPending.set(true);
    this.promptPreviewTimer = setTimeout(() => {
      this.promptPreviewTimer = undefined;
      this.runPromptPreview(systemPrompt, promptTemplate, scope, pins);
    }, 200);
  }

  private cancelPromptPreviewRender(): void {
    if (this.promptPreviewTimer !== undefined) {
      clearTimeout(this.promptPreviewTimer);
      this.promptPreviewTimer = undefined;
    }
  }

  private runPromptPreview(
    systemPrompt: string,
    promptTemplate: string,
    scope: PromptPreviewScope,
    pins: PromptPartialPinDto[]
  ): void {
    this.agentsApi.renderPromptTemplate({
      systemPrompt: systemPrompt || null,
      promptTemplate: promptTemplate || null,
      workflow: scope.workflow,
      context: scope.context,
      input: scope.input,
      reviewRound: scope.reviewRound,
      reviewMaxRounds: scope.reviewMaxRounds,
      optOutLastRoundReminder: scope.optOutLastRoundReminder,
      partialPins: pins,
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: response => {
        this.promptPreviewSystem.set(response.renderedSystemPrompt);
        this.promptPreviewBody.set(response.renderedPromptTemplate);
        this.promptPreviewAutoInjections.set(response.autoInjections ?? []);
        this.promptPreviewMissingPartials.set(response.missingPartials ?? []);
        this.promptPreviewError.set(null);
        this.promptPreviewPending.set(false);
      },
      error: err => {
        const body = err && typeof err === 'object' ? (err as { error?: unknown }).error : null;
        if (body && typeof body === 'object') {
          const e = body as { error?: string; missingPartials?: PromptTemplatePreviewMissingPartial[] };
          this.promptPreviewError.set(e.error ?? 'Preview render failed.');
          this.promptPreviewMissingPartials.set(e.missingPartials ?? []);
        } else {
          this.promptPreviewError.set(typeof body === 'string' ? body : 'Preview render failed.');
          this.promptPreviewMissingPartials.set([]);
        }
        this.promptPreviewSystem.set(null);
        this.promptPreviewBody.set(null);
        this.promptPreviewAutoInjections.set([]);
        this.promptPreviewPending.set(false);
      }
    });
  }

  ngOnInit(): void {
    this.llmProvidersApi.listModels().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: options => this.configuredModels.set(options),
      error: () => this.configuredModels.set([]),
    });
  }

  ngOnDestroy(): void {
    this.clearPreviewTimers();
    this.cancelPromptPreviewRender();
  }

  private hydrateFromConfig(config: AgentConfig, resolvedType: 'agent' | 'hitl'): void {
    this.type.set(resolvedType);
    this.name.set((config['name'] as string) ?? '');
    this.description.set((config['description'] as string) ?? '');
    this.provider.set(config.provider ?? 'openai');
    this.model.set((config['model'] as string) ?? 'gpt-5.4');
    this.systemPrompt.set((config['systemPrompt'] as string) ?? '');
    this.promptTemplate.set((config['promptTemplate'] as string) ?? '');
    this.outputTemplate.set((config['outputTemplate'] as string) ?? '');
    this.maxTokens.set(config['maxTokens'] as number | undefined);
    this.temperature.set(config['temperature'] as number | undefined);
    this.partialPins.set(readPromptPartialPins(config['partialPins']));
    this.history.set(readAuthorableHistory(config['history']));
    const templates = (config['decisionOutputTemplates'] as Record<string, string> | undefined) ?? {};
    const declared = config['outputs'];
    const declaredKinds = new Set<string>();
    if (Array.isArray(declared) && declared.length > 0) {
      this.outputs.set(declared.map(d => {
        const kind = d.kind ?? '';
        declaredKinds.add(kind);
        const template = String(templates[kind] ?? '');
        const payloadExample = d.payloadExample ?? null;
        return {
          kind,
          description: d.description ?? null,
          payloadExample,
          template,
          preview: '',
          previewError: null,
          previewPending: false,
          expanded: template.length > 0 || payloadExample !== null
        } as OutputRow;
      }));
    } else {
      // Keep defaults but hydrate templates for them
      this.outputs.update(rows => rows.map(r => {
        const template = String(templates[r.kind] ?? '');
        return {
          ...r,
          template,
          expanded: template.length > 0 || r.payloadExample !== null
        };
      }));
      this.outputs().forEach(r => declaredKinds.add(r.kind));
    }
    const orphans: DecisionTemplateRow[] = Object.entries(templates)
      .filter(([port]) => !declaredKinds.has(port))
      .map(([port, template]) => ({
        port,
        template: String(template ?? ''),
        preview: '',
        previewError: null,
        previewPending: false
      }));
    this.fallbackTemplates.set(orphans);
    if (resolvedType === 'hitl') {
      this.previewContextText.set(defaultHitlPreviewContext());
    } else {
      const sample = pickSampleOutputRow(this.outputs());
      this.previewContextText.set(defaultLlmPreviewContext(
        sample ? coerceSamplePayload(sample.payloadExample) : undefined,
        sample?.kind));
    }
  }

  protected addFallbackTemplate(): void {
    this.fallbackTemplates.set([
      ...this.fallbackTemplates(),
      { port: '', template: '', preview: '', previewError: null, previewPending: false }
    ]);
  }

  protected removeFallbackTemplate(index: number): void {
    this.fallbackTemplates.set(this.fallbackTemplates().filter((_, i) => i !== index));
  }

  protected updateFallbackTemplate(index: number, patch: Partial<DecisionTemplateRow>): void {
    this.fallbackTemplates.set(this.fallbackTemplates().map((row, i) =>
      i === index ? { ...row, ...patch } : row));
  }

  private patchFallbackRowSilently(index: number, patch: Partial<DecisionTemplateRow>): void {
    this.fallbackTemplates.update(rows => rows.map((row, i) =>
      i === index ? { ...row, ...patch } : row));
  }

  private patchOutputRowSilently(index: number, patch: Partial<OutputRow>): void {
    this.outputs.update(rows => rows.map((row, i) =>
      i === index ? { ...row, ...patch } : row));
  }

  private readonly previewTimers = new Map<string, ReturnType<typeof setTimeout>>();
  private readonly previewSignatures = new Map<string, string>();

  private schedulePreviewRender(
    key: string,
    template: string,
    port: string,
    mode: DecisionOutputTemplateMode,
    parsedContext: Record<string, unknown>,
    commit: (patch: { preview?: string; previewError?: string | null; previewPending?: boolean }) => void
  ): void {
    this.cancelPreviewRender(key);
    const timer = setTimeout(() => {
      this.previewTimers.delete(key);
      this.runPreview(template, port, mode, parsedContext, commit);
    }, 200);
    this.previewTimers.set(key, timer);
  }

  private cancelPreviewRender(key: string): void {
    const timer = this.previewTimers.get(key);
    if (timer === undefined) return;
    clearTimeout(timer);
    this.previewTimers.delete(key);
  }

  private clearPreviewTimers(): void {
    this.previewTimers.forEach(timer => clearTimeout(timer));
    this.previewTimers.clear();
  }

  private runPreview(
    template: string,
    port: string,
    mode: DecisionOutputTemplateMode,
    parsedContext: Record<string, unknown>,
    commit: (patch: { preview?: string; previewError?: string | null; previewPending?: boolean }) => void
  ): void {
    this.agentsApi.renderDecisionOutputTemplate({
      template,
      mode,
      decision: port,
      outputPortName: port,
      output: mode === 'llm' ? stringifyPreviewOutput(parsedContext['output']) : undefined,
      input: mode === 'llm' ? parsedContext['input'] : undefined,
      fieldValues: mode === 'hitl'
        ? asRecord(parsedContext['input']) ?? asRecord(parsedContext['fieldValues'])
        : undefined,
      reason: mode === 'hitl' ? asString(parsedContext['reason']) : undefined,
      reasons: mode === 'hitl' ? asStringArray(parsedContext['reasons']) : undefined,
      actions: mode === 'hitl' ? asStringArray(parsedContext['actions']) : undefined,
      context: asRecord(parsedContext['context']),
      workflow: asRecord(parsedContext['workflow'])
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: response => commit({ preview: response.rendered, previewError: null, previewPending: false }),
      error: err => commit({ preview: '', previewError: extractPreviewError(err), previewPending: false })
    });
  }

  protected applyFormPreset(key: FormPresetKey): void {
    const preset = getFormPreset(key);
    if (!preset) return;

    const sourceVariableKey = key === 'edit-then-approve' ? this.presetEditSourceKey().trim() : undefined;
    const portNames = key === 'multi-action'
      ? this.presetMultiActionPorts().split(',').map(p => p.trim()).filter(p => p.length > 0)
      : undefined;

    const result = preset.build({ sourceVariableKey, portNames });

    this.outputTemplate.set(result.outputTemplate);
    this.outputs.set(result.outputs.map(o => ({
      kind: o.kind,
      description: o.description ?? null,
      payloadExample: o.payloadExample ?? null,
      template: result.decisionOutputTemplates?.[o.kind] ?? '',
      preview: '',
      previewError: null,
      previewPending: false,
      expanded: !!result.decisionOutputTemplates?.[o.kind],
    })));
    this.fallbackTemplates.set([]);
    this.previewContextText.set(defaultHitlPreviewContext());
  }

  protected addOutput(): void {
    this.outputs.set([...this.outputs(), emptyOutputRow('')]);
  }

  protected removeOutput(index: number): void {
    this.outputs.set(this.outputs().filter((_, i) => i !== index));
  }

  protected updateOutput(index: number, patch: Partial<OutputRow>): void {
    this.outputs.set(this.outputs().map((o, i) => (i === index ? { ...o, ...patch } : o)));
  }

  protected toggleOutputExpanded(index: number): void {
    const row = this.outputs()[index];
    if (!row) return;
    this.updateOutput(index, { expanded: !row.expanded });
  }

  protected addHistoryMessage(): void {
    if (this.history().length >= this.HISTORY_MAX_ENTRIES) return;
    const lastRole = this.history().at(-1)?.role;
    const nextRole: AuthorableHistoryRole = lastRole === 'user' ? 'assistant' : 'user';
    this.history.set([...this.history(), { role: nextRole, content: '' }]);
  }

  protected removeHistoryMessage(index: number): void {
    this.history.set(this.history().filter((_, i) => i !== index));
  }

  protected updateHistoryMessage(index: number, patch: Partial<AuthorableHistoryMessage>): void {
    this.history.set(this.history().map((m, i) => (i === index ? { ...m, ...patch } : m)));
  }

  protected payloadExampleText(output: OutputRow): string {
    if (output.payloadExample === null || output.payloadExample === undefined) return '';
    if (typeof output.payloadExample === 'string') return output.payloadExample;
    return JSON.stringify(output.payloadExample, null, 2);
  }

  protected updatePayloadExample(index: number, raw: string): void {
    const trimmed = raw?.trim() ?? '';
    if (!trimmed) {
      this.updateOutput(index, { payloadExample: null });
      return;
    }
    try {
      const parsed = JSON.parse(trimmed);
      this.updateOutput(index, { payloadExample: parsed });
    } catch {
      this.updateOutput(index, { payloadExample: raw });
    }
  }

  protected generatePayloadFromTemplate(index: number): void {
    const row = this.outputs()[index];
    if (!row) return;
    const generated = generatePayloadFromTemplate(row.template);
    if (generated === null) return;
    this.updateOutput(index, { payloadExample: generated });
  }

  protected canGeneratePayload(row: OutputRow): boolean {
    return !!row.template && /\boutput\.[a-zA-Z_]/.test(row.template);
  }

  protected setTab(value: string): void {
    if (isEditorTab(value)) {
      this.tab.set(value);
    }
  }

  submit(event: Event): void {
    event.preventDefault();

    const config: AgentConfig = {
      type: this.type(),
      name: this.name() || undefined,
      description: this.description() || undefined,
    };

    if (this.type() === 'agent') {
      config.provider = this.provider();
      config.model = this.model();
      config.systemPrompt = this.systemPrompt() || undefined;
      config.promptTemplate = this.promptTemplate() || undefined;
      if (this.maxTokens() !== undefined) config.maxTokens = this.maxTokens();
      if (this.temperature() !== undefined) config.temperature = this.temperature();
      const pins = this.partialPins();
      if (pins.length > 0) {
        config['partialPins'] = pins;
      }
      const cleanedHistory = this.history()
        .map(m => ({ role: m.role, content: m.content ?? '' }))
        .filter(m => m.content.trim().length > 0);
      if (cleanedHistory.length > 0) {
        config.history = cleanedHistory;
      }
    }

    if (this.type() === 'hitl') {
      config.outputTemplate = this.outputTemplate() || undefined;
    }

    const outputRows = this.outputs();
    const cleanedOutputs: AgentOutputDeclaration[] = outputRows
      .map(o => ({
        kind: (o.kind ?? '').trim(),
        description: o.description?.trim() || null,
        payloadExample: typeof o.payloadExample === 'string'
          ? tryParseJson(o.payloadExample)
          : o.payloadExample ?? null
      }))
      .filter(o => o.kind.length > 0);

    if (cleanedOutputs.length > 0) {
      config.outputs = cleanedOutputs;
    }

    const templatesMap: Record<string, string> = {};
    for (const row of outputRows) {
      const kind = (row.kind ?? '').trim();
      if (kind && row.template.trim().length > 0) {
        templatesMap[kind] = row.template;
      }
    }
    for (const row of this.fallbackTemplates()) {
      const port = row.port.trim();
      if (port && row.template.trim().length > 0) {
        templatesMap[port] = row.template;
      }
    }
    if (Object.keys(templatesMap).length > 0) {
      config.decisionOutputTemplates = templatesMap;
    }

    this.saveRequested.emit({ key: this.key(), type: this.type(), config });
  }

  protected coerceOptionalNumber(value: unknown): number | undefined {
    if (value === null || value === undefined || value === '') {
      return undefined;
    }
    if (typeof value === 'number') {
      return Number.isFinite(value) ? value : undefined;
    }
    if (typeof value === 'string') {
      const trimmed = value.trim();
      if (!trimmed) return undefined;
      const parsed = Number(trimmed);
      return Number.isFinite(parsed) ? parsed : undefined;
    }
    return undefined;
  }
}

function tryParseJson(raw: string): unknown {
  try { return JSON.parse(raw); } catch { return raw; }
}

function isEditorTab(value: string): value is EditorTab {
  return value === 'identity'
    || value === 'prompt'
    || value === 'preview'
    || value === 'model'
    || value === 'outputs';
}

function readPromptPartialPins(value: unknown): PromptPartialPinDto[] {
  if (!Array.isArray(value)) return [];
  return value.filter(isPromptPartialPin);
}

function isPromptPartialPin(value: unknown): value is PromptPartialPinDto {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<PromptPartialPinDto>;
  return typeof candidate.key === 'string' && typeof candidate.version === 'number';
}

function readAuthorableHistory(value: unknown): AuthorableHistoryMessage[] {
  if (!Array.isArray(value)) return [];
  const result: AuthorableHistoryMessage[] = [];
  for (const entry of value) {
    if (!entry || typeof entry !== 'object') continue;
    const candidate = entry as Partial<AuthorableHistoryMessage>;
    if (candidate.role !== 'user' && candidate.role !== 'assistant') continue;
    if (typeof candidate.content !== 'string') continue;
    result.push({ role: candidate.role, content: candidate.content });
  }
  return result;
}

function emptyOutputRow(kind: string): OutputRow {
  return {
    kind,
    description: null,
    payloadExample: null,
    template: '',
    preview: '',
    previewError: null,
    previewPending: false,
    expanded: false
  };
}

function generatePayloadFromTemplate(template: string): Record<string, unknown> | null {
  // Match references like `output.foo`, `output.foo.bar`, etc.
  // Stops at any non-identifier character; ignores bracket access on purpose.
  const re = /\boutput\.([a-zA-Z_][\w]*(?:\.[a-zA-Z_][\w]*)*)/g;
  const paths: string[][] = [];
  const seen = new Set<string>();
  for (const m of template.matchAll(re)) {
    if (seen.has(m[1])) continue;
    seen.add(m[1]);
    paths.push(m[1].split('.'));
  }
  if (paths.length === 0) return null;
  const result: Record<string, unknown> = {};
  for (const path of paths) {
    let cur: Record<string, unknown> = result;
    for (let i = 0; i < path.length; i++) {
      const key = path[i];
      const isLeaf = i === path.length - 1;
      if (isLeaf) {
        if (!(key in cur)) cur[key] = `<${path.join('.')}>`;
      } else {
        const next = cur[key];
        if (!next || typeof next !== 'object' || Array.isArray(next)) {
          cur[key] = {};
        }
        cur = cur[key] as Record<string, unknown>;
      }
    }
  }
  return result;
}

function defaultHitlPreviewContext(): string {
  return JSON.stringify({
    input: { feedback: 'looks good' },
    reason: 'short explanation',
    reasons: [],
    actions: [],
    context: {},
    workflow: {}
  }, null, 2);
}

function defaultLlmPreviewContext(sampleOutput?: unknown, sampleDecision?: string): string {
  const decision = sampleDecision && sampleDecision.trim() ? sampleDecision : 'Completed';
  const output = sampleOutput !== undefined ? sampleOutput : 'Example markdown content';
  return JSON.stringify({
    decision,
    outputPortName: decision,
    output,
    input: {},
    context: {},
    workflow: {}
  }, null, 2);
}

function pickSampleOutputRow(rows: readonly OutputRow[]): OutputRow | null {
  return rows.find(r => r.payloadExample !== null && r.payloadExample !== undefined) ?? null;
}

function coerceSamplePayload(value: unknown): unknown {
  if (typeof value !== 'string') return value;
  const trimmed = value.trim();
  if (!trimmed) return value;
  try { return JSON.parse(trimmed); } catch { return value; }
}

function stringifyPreviewOutput(value: unknown): string | undefined {
  if (value === undefined || value === null) return undefined;
  if (typeof value === 'string') return value;
  return JSON.stringify(value);
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  return value as Record<string, unknown>;
}

function asString(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined;
}

function asStringArray(value: unknown): string[] | undefined {
  if (!Array.isArray(value)) return undefined;
  return value.filter(item => typeof item === 'string') as string[];
}

interface PromptPreviewScope {
  workflow?: Record<string, unknown>;
  context?: Record<string, unknown>;
  input?: string | null;
  reviewRound?: number | null;
  reviewMaxRounds?: number | null;
  optOutLastRoundReminder?: boolean;
}

function defaultPromptPreviewScope(): string {
  return JSON.stringify({
    workflow: { exampleVar: 'sample value' },
    context: {},
    input: null,
    reviewRound: null,
    reviewMaxRounds: null,
    optOutLastRoundReminder: false,
  }, null, 2);
}

function parsePromptPreviewScope(text: string): PromptPreviewScope {
  if (!text || !text.trim()) return {};
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new Error('Scope must be valid JSON.');
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('Scope must be a JSON object.');
  }
  const obj = parsed as Record<string, unknown>;
  const result: PromptPreviewScope = {};
  if (obj['workflow'] && typeof obj['workflow'] === 'object' && !Array.isArray(obj['workflow'])) {
    result.workflow = obj['workflow'] as Record<string, unknown>;
  }
  if (obj['context'] && typeof obj['context'] === 'object' && !Array.isArray(obj['context'])) {
    result.context = obj['context'] as Record<string, unknown>;
  }
  if (typeof obj['input'] === 'string') result.input = obj['input'];
  else if (obj['input'] === null) result.input = null;
  else if (obj['input'] !== undefined) result.input = JSON.stringify(obj['input']);
  if (typeof obj['reviewRound'] === 'number') result.reviewRound = obj['reviewRound'];
  if (typeof obj['reviewMaxRounds'] === 'number') result.reviewMaxRounds = obj['reviewMaxRounds'];
  if (typeof obj['optOutLastRoundReminder'] === 'boolean') result.optOutLastRoundReminder = obj['optOutLastRoundReminder'];
  return result;
}

function previewSignature(
  mode: DecisionOutputTemplateMode,
  port: string,
  template: string,
  contextText: string
): string {
  return JSON.stringify([mode, port, template, contextText]);
}

function extractPreviewError(err: unknown): string {
  if (err && typeof err === 'object') {
    const httpErr = err as { error?: { error?: string } | string; message?: string; status?: number };
    if (httpErr.error && typeof httpErr.error === 'object' && 'error' in httpErr.error) {
      return String((httpErr.error as { error: unknown }).error);
    }
    if (typeof httpErr.error === 'string') {
      return httpErr.error;
    }
    if (httpErr.message) {
      return httpErr.message;
    }
  }
  return 'Preview render failed';
}
