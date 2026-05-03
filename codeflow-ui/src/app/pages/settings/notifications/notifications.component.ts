import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { formatHttpError } from '../../../core/format-error';
import {
  NOTIFICATION_CHANNELS,
  NOTIFICATION_DELIVERY_STATUSES,
  NOTIFICATION_EVENT_KINDS,
  NOTIFICATION_SEVERITIES,
  NotificationChannel,
  NotificationCredentialAction,
  NotificationDeliveryAttemptResponse,
  NotificationDeliveryStatus,
  NotificationDiagnosticsResponse,
  NotificationEventKind,
  NotificationProviderResponse,
  NotificationProviderValidationResponse,
  NotificationProviderWriteRequest,
  NotificationRouteResponse,
  NotificationRouteWriteRequest,
  NotificationSeverity,
  NotificationTemplatePreviewResponse,
  NotificationTemplateResponse,
  NotificationTemplateWriteRequest,
  NotificationTestSendResponse,
} from '../../../core/models';
import { NotificationsAdminApi } from '../../../core/notifications-admin.api';
import { PageHeaderComponent } from '../../../ui/page-header.component';
import { ButtonComponent } from '../../../ui/button.component';
import { ChipComponent } from '../../../ui/chip.component';
import { CardComponent } from '../../../ui/card.component';

interface ProviderEditState {
  id: string;
  isNew: boolean;
  displayName: string;
  channel: NotificationChannel;
  endpointUrl: string;
  fromAddress: string;
  additionalConfigJson: string;
  enabled: boolean;
  hasCredential: boolean;
  credentialAction: NotificationCredentialAction;
  credentialValue: string;
  saving: boolean;
  error: string | null;
}

interface RouteEditState {
  routeId: string;
  isNew: boolean;
  eventKind: NotificationEventKind;
  providerId: string;
  recipientsJson: string;
  templateId: string;
  templateVersion: number;
  minimumSeverity: NotificationSeverity;
  enabled: boolean;
  saving: boolean;
  error: string | null;
}

/**
 * sc-58 — state for the inline test-send dialog. One per provider at a time; admins target
 * a single recipient and optionally pin a template ref. Result is held here so the UI can
 * show the rendered preview alongside the provider's delivery outcome.
 */
interface TestSendState {
  providerId: string;
  channel: NotificationChannel;
  recipientAddress: string;
  recipientDisplayName: string;
  useTemplate: boolean;
  templateId: string;
  templateVersion: number;
  sending: boolean;
  result: NotificationTestSendResponse | null;
  error: string | null;
}

interface ValidationOutcome {
  providerId: string;
  result: NotificationProviderValidationResponse;
}

/**
 * sc-63 — inline template editor state. Mutated in place so signals don't fire on every
 * keystroke; the surrounding signal stores `null` (no editor open) or a single open editor.
 */
interface TemplateEditState {
  templateId: string;
  isNew: boolean;
  eventKind: NotificationEventKind;
  channel: NotificationChannel;
  subjectTemplate: string;
  bodyTemplate: string;
  saving: boolean;
  error: string | null;
  preview: NotificationTemplatePreviewResponse | null;
  previewing: boolean;
}

/**
 * sc-59 — filter + paging state for the delivery audit panel. The form bindings live on this
 * record so admins can refine filters without losing the existing page; "Apply" resets the
 * cursor and pulls the first page anew.
 */
interface DeliveryAuditState {
  filterEventId: string;
  filterProviderId: string;
  filterRouteId: string;
  filterStatus: NotificationDeliveryStatus | '';
  filterSinceUtc: string;
  items: NotificationDeliveryAttemptResponse[];
  nextBeforeId: number | null;
  loading: boolean;
  error: string | null;
}

@Component({
  selector: 'cf-notifications-admin',
  standalone: true,
  imports: [
    FormsModule, DatePipe,
    PageHeaderComponent, ButtonComponent, ChipComponent, CardComponent,
  ],
  template: `
    <div class="page">
      <cf-page-header
        title="HITL notifications"
        subtitle="Configure providers (Slack, email, SMS) and route HITL events to one or many destinations. Credentials are encrypted at rest and never returned by the API.">
      </cf-page-header>

      @if (loadError()) {
        <cf-card>
          <div class="trace-failure"><strong>Couldn't load:</strong> {{ loadError() }}</div>
        </cf-card>
      }

      @if (diagnostics(); as diag) {
        <cf-card title="Diagnostics">
          <div class="diag-grid">
            <div>
              <div class="muted small">Public base URL</div>
              <div>
                @if (diag.publicBaseUrl) {
                  <code class="code-inline">{{ diag.publicBaseUrl }}</code>
                } @else {
                  <cf-chip tone="warn">unset</cf-chip>
                }
              </div>
            </div>
            <div>
              <div class="muted small">Action URLs</div>
              <div>
                @if (diag.actionUrlsConfigured) {
                  <cf-chip tone="ok">ready</cf-chip>
                } @else {
                  <cf-chip tone="warn">not configured</cf-chip>
                }
              </div>
            </div>
            <div>
              <div class="muted small">Providers</div>
              <div>{{ diag.providerCount }}</div>
            </div>
            <div>
              <div class="muted small">Routes</div>
              <div>{{ diag.routeCount }}</div>
            </div>
          </div>
          @if (!diag.actionUrlsConfigured) {
            <p class="muted small" style="margin-top: 0.5rem">
              Set <code class="code-inline">CodeFlow:Notifications:PublicBaseUrl</code> in configuration so HITL
              notifications can include a working action link. Until then HITL events fire but skip the publish path.
            </p>
          }
        </cf-card>
      }

      <!-- Providers -->
      <cf-card title="Providers">
        <div class="row" style="margin-bottom: 0.75rem; gap: 0.5rem">
          <cf-button (click)="newProvider()">+ New provider</cf-button>
          <label class="muted small" style="margin-left: auto; display: flex; align-items: center; gap: 0.25rem">
            <input type="checkbox" [(ngModel)]="includeArchivedProviders" name="includeArchived"
              (change)="reloadProviders()">
            Include archived
          </label>
        </div>

        @if (providers().length === 0) {
          <div class="muted">No providers configured yet.</div>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>Id</th>
                <th>Channel</th>
                <th>Display name</th>
                <th>From</th>
                <th>Cred?</th>
                <th>Status</th>
                <th>Updated</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (p of providers(); track p.id) {
                <tr>
                  <td><code class="code-inline">{{ p.id }}</code></td>
                  <td><cf-chip>{{ p.channel }}</cf-chip></td>
                  <td>{{ p.displayName }}</td>
                  <td><span class="muted small">{{ p.fromAddress || '—' }}</span></td>
                  <td>
                    @if (p.hasCredential) {
                      <cf-chip tone="ok">set</cf-chip>
                    } @else {
                      <cf-chip tone="muted">none</cf-chip>
                    }
                  </td>
                  <td>
                    @if (p.isArchived) {
                      <cf-chip tone="muted">archived</cf-chip>
                    } @else if (p.enabled) {
                      <cf-chip tone="ok">enabled</cf-chip>
                    } @else {
                      <cf-chip tone="warn">disabled</cf-chip>
                    }
                  </td>
                  <td><span class="muted small">{{ p.updatedAtUtc | date:'shortDate' }}</span></td>
                  <td class="actions">
                    <cf-button kind="ghost" (click)="editProvider(p)">Edit</cf-button>
                    @if (!p.isArchived) {
                      <cf-button kind="ghost" (click)="validateProvider(p)" [disabled]="validatingProviderId() === p.id">
                        {{ validatingProviderId() === p.id ? 'Validating…' : 'Validate' }}
                      </cf-button>
                      <cf-button kind="ghost" (click)="openTestSend(p)">Test send</cf-button>
                      <cf-button kind="ghost" (click)="archiveProvider(p)">Archive</cf-button>
                    }
                  </td>
                </tr>
                @if (validationOutcome(); as outcome) {
                  @if (outcome.providerId === p.id) {
                    <tr class="result-row">
                      <td colspan="8">
                        @if (outcome.result.isValid) {
                          <cf-chip tone="ok">Validated</cf-chip>
                          <span class="muted small">Provider credentials accepted by the upstream service.</span>
                        } @else {
                          <cf-chip tone="warn">Validation failed</cf-chip>
                          <code class="code-inline">{{ outcome.result.errorCode }}</code>
                          <span class="muted small">{{ outcome.result.errorMessage }}</span>
                        }
                        <cf-button kind="ghost" (click)="dismissValidation()">Dismiss</cf-button>
                      </td>
                    </tr>
                  }
                }
              }
            </tbody>
          </table>
        }

        @if (testSendState(); as ts) {
          <div class="edit-panel">
            <h3>Send test from {{ ts.providerId }} ({{ ts.channel }})</h3>
            @if (ts.error) {
              <div class="trace-failure">{{ ts.error }}</div>
            }
            <p class="muted small" style="margin-top: 0">
              Sends a synthetic notification through the provider with a real action URL. No
              audit row is written. Use this to verify credentials, destination format, and
              rendered copy before live HITL events fire.
            </p>
            <form (submit)="runTestSend($event)">
              <div class="form-row">
                <label>Recipient address</label>
                <input type="text" [(ngModel)]="ts.recipientAddress" name="recipientAddress" required
                  [placeholder]="recipientPlaceholder(ts.channel)">
              </div>
              <div class="form-row">
                <label>Display name (optional)</label>
                <input type="text" [(ngModel)]="ts.recipientDisplayName" name="recipientDisplayName">
              </div>
              <div class="form-row">
                <label>Use template</label>
                <input type="checkbox" [(ngModel)]="ts.useTemplate" name="useTemplate">
                <span class="muted small">When unchecked, sends a built-in "[CodeFlow] Test notification" body.</span>
              </div>
              @if (ts.useTemplate) {
                <div class="form-row">
                  <label>Template id</label>
                  <input type="text" [(ngModel)]="ts.templateId" name="testTemplateId" required
                    placeholder="hitl-task-pending/slack/default">
                </div>
                <div class="form-row">
                  <label>Template version</label>
                  <input type="number" [(ngModel)]="ts.templateVersion" name="testTemplateVersion" min="1" required>
                </div>
              }
              <div class="actions">
                <button type="submit" cf-button [disabled]="ts.sending">
                  {{ ts.sending ? 'Sending…' : 'Send test' }}
                </button>
                <cf-button kind="ghost" (click)="cancelTestSend()">Close</cf-button>
              </div>
            </form>

            @if (ts.result; as r) {
              <div style="margin-top: 1rem;">
                <h4 style="margin: 0 0 0.5rem 0">Result</h4>
                <div class="form-row">
                  <label>Status</label>
                  <div>
                    @if (r.delivery.status === 'Sent') {
                      <cf-chip tone="ok">{{ r.delivery.status }}</cf-chip>
                    } @else {
                      <cf-chip tone="warn">{{ r.delivery.status }}</cf-chip>
                    }
                    @if (r.delivery.providerMessageId) {
                      <span class="muted small">id: <code class="code-inline">{{ r.delivery.providerMessageId }}</code></span>
                    }
                  </div>
                </div>
                @if (r.delivery.errorCode) {
                  <div class="form-row">
                    <label>Error</label>
                    <div>
                      <code class="code-inline">{{ r.delivery.errorCode }}</code>
                      <span class="muted small">{{ r.delivery.errorMessage }}</span>
                    </div>
                  </div>
                }
                <div class="form-row">
                  <label>Action URL</label>
                  <div><code class="code-inline">{{ r.actionUrl }}</code></div>
                </div>
                @if (r.subject) {
                  <div class="form-row">
                    <label>Subject</label>
                    <div>{{ r.subject }}</div>
                  </div>
                }
                <div class="form-row">
                  <label>Rendered body</label>
                  <textarea readonly rows="6" style="font-family: ui-monospace, monospace; font-size: 0.85rem">{{ r.body }}</textarea>
                </div>
              </div>
            }
          </div>
        }

        @if (providerEdit(); as edit) {
          <div class="edit-panel">
            <h3>{{ edit.isNew ? 'New provider' : 'Edit provider — ' + edit.id }}</h3>
            @if (edit.error) {
              <div class="trace-failure">{{ edit.error }}</div>
            }
            <form (submit)="saveProvider($event)">
              <div class="form-row">
                <label>Provider id</label>
                <input type="text" [(ngModel)]="edit.id" name="id" required [disabled]="!edit.isNew"
                  placeholder="e.g. slack-prod">
              </div>
              <div class="form-row">
                <label>Channel</label>
                <select [(ngModel)]="edit.channel" name="channel" [disabled]="!edit.isNew">
                  @for (c of channels; track c) {
                    <option [value]="c">{{ c }}</option>
                  }
                </select>
              </div>
              <div class="form-row">
                <label>Display name</label>
                <input type="text" [(ngModel)]="edit.displayName" name="displayName" required>
              </div>
              <div class="form-row">
                <label>From address / sender</label>
                <input type="text" [(ngModel)]="edit.fromAddress" name="fromAddress"
                  [placeholder]="fromPlaceholder(edit.channel)">
              </div>
              <div class="form-row">
                <label>Endpoint URL</label>
                <input type="text" [(ngModel)]="edit.endpointUrl" name="endpointUrl"
                  placeholder="optional, e.g. SMTP host or override base URL">
              </div>
              <div class="form-row">
                <label>Additional config (JSON)</label>
                <textarea [(ngModel)]="edit.additionalConfigJson" name="additionalConfigJson" rows="3"
                  [placeholder]="additionalConfigPlaceholder(edit.channel)"></textarea>
                <span class="muted small">{{ additionalConfigHelp(edit.channel) }}</span>
              </div>

              <div class="form-row">
                <label>Credential</label>
                <div>
                  @if (edit.credentialAction === 'Replace') {
                    <input type="password" [(ngModel)]="edit.credentialValue" name="credentialValue"
                      [placeholder]="credentialPlaceholder(edit.channel)" required>
                    <cf-button kind="ghost" (click)="setCredentialAction(edit, 'Preserve')">Cancel replace</cf-button>
                  } @else if (edit.credentialAction === 'Clear') {
                    <cf-chip tone="warn">Will clear on save</cf-chip>
                    <cf-button kind="ghost" (click)="setCredentialAction(edit, 'Preserve')">Undo</cf-button>
                  } @else {
                    @if (edit.hasCredential) {
                      <cf-chip tone="ok">••••••••</cf-chip>
                      <cf-button kind="ghost" (click)="setCredentialAction(edit, 'Replace')">Replace</cf-button>
                      <cf-button kind="ghost" (click)="setCredentialAction(edit, 'Clear')">Clear</cf-button>
                    } @else if (edit.isNew) {
                      <input type="password" [(ngModel)]="edit.credentialValue" name="credentialValue"
                        [placeholder]="credentialPlaceholder(edit.channel)">
                      <span class="muted small">{{ credentialHelp(edit.channel) }}</span>
                    } @else {
                      <span class="muted">No credential set</span>
                      <cf-button kind="ghost" (click)="setCredentialAction(edit, 'Replace')">Set</cf-button>
                    }
                  }
                </div>
              </div>

              <div class="form-row">
                <label>Enabled</label>
                <input type="checkbox" [(ngModel)]="edit.enabled" name="enabled">
              </div>

              <div class="actions">
                <button type="submit" cf-button [disabled]="edit.saving">
                  {{ edit.saving ? 'Saving…' : 'Save' }}
                </button>
                <cf-button kind="ghost" (click)="cancelProviderEdit()">Cancel</cf-button>
              </div>
            </form>
          </div>
        }
      </cf-card>

      <!-- Routes -->
      <cf-card title="Routes">
        <div class="row" style="margin-bottom: 0.75rem">
          <cf-button (click)="newRoute()">+ New route</cf-button>
        </div>

        @if (routes().length === 0) {
          <div class="muted">No routes configured. Routes map a HITL event kind to one provider + recipient list + template.</div>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>Route id</th>
                <th>Event</th>
                <th>Provider</th>
                <th>Recipients</th>
                <th>Template</th>
                <th>Min severity</th>
                <th>Status</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (r of routes(); track r.routeId) {
                <tr>
                  <td><code class="code-inline">{{ r.routeId }}</code></td>
                  <td>{{ r.eventKind }}</td>
                  <td><code class="code-inline">{{ r.providerId }}</code></td>
                  <td><span class="muted small">{{ r.recipients.length }} recipient(s)</span></td>
                  <td><span class="muted small">{{ r.template.templateId }} v{{ r.template.version }}</span></td>
                  <td>{{ r.minimumSeverity }}</td>
                  <td>
                    @if (r.enabled) {
                      <cf-chip tone="ok">enabled</cf-chip>
                    } @else {
                      <cf-chip tone="warn">disabled</cf-chip>
                    }
                  </td>
                  <td class="actions">
                    <cf-button kind="ghost" (click)="editRoute(r)">Edit</cf-button>
                    <cf-button kind="ghost" (click)="deleteRoute(r)">Delete</cf-button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }

        @if (routeEdit(); as edit) {
          <div class="edit-panel">
            <h3>{{ edit.isNew ? 'New route' : 'Edit route — ' + edit.routeId }}</h3>
            @if (edit.error) {
              <div class="trace-failure">{{ edit.error }}</div>
            }
            <form (submit)="saveRoute($event)">
              <div class="form-row">
                <label>Route id</label>
                <input type="text" [(ngModel)]="edit.routeId" name="routeId" required [disabled]="!edit.isNew"
                  placeholder="e.g. route-hitl-pending-slack">
              </div>
              <div class="form-row">
                <label>Event kind</label>
                <select [(ngModel)]="edit.eventKind" name="eventKind">
                  @for (k of eventKinds; track k) {
                    <option [value]="k">{{ k }}</option>
                  }
                </select>
              </div>
              <div class="form-row">
                <label>Provider</label>
                <select [(ngModel)]="edit.providerId" name="providerId" required>
                  <option value="">— pick a provider —</option>
                  @for (p of providers(); track p.id) {
                    @if (!p.isArchived) {
                      <option [value]="p.id">{{ p.id }} ({{ p.channel }})</option>
                    }
                  }
                </select>
              </div>
              <div class="form-row">
                <label>Recipients (JSON array)</label>
                <textarea [(ngModel)]="edit.recipientsJson" name="recipientsJson" rows="4" required
                  placeholder='[{"channel":"Slack","address":"C012AB3CD","displayName":"#hitl-queue"}]'></textarea>
                <span class="muted small">Each recipient: {{ '{' }}channel, address, displayName?{{ '}' }}. Channel must match the provider's channel.</span>
              </div>
              <div class="form-row">
                <label>Template id</label>
                <input type="text" [(ngModel)]="edit.templateId" name="templateId" required
                  placeholder="hitl-task-pending/slack/default">
              </div>
              <div class="form-row">
                <label>Template version</label>
                <input type="number" [(ngModel)]="edit.templateVersion" name="templateVersion" min="1" required>
              </div>
              <div class="form-row">
                <label>Minimum severity</label>
                <select [(ngModel)]="edit.minimumSeverity" name="minimumSeverity">
                  @for (s of severities; track s) {
                    <option [value]="s">{{ s }}</option>
                  }
                </select>
              </div>
              <div class="form-row">
                <label>Enabled</label>
                <input type="checkbox" [(ngModel)]="edit.enabled" name="enabled">
              </div>

              <div class="actions">
                <button type="submit" cf-button [disabled]="edit.saving">
                  {{ edit.saving ? 'Saving…' : 'Save' }}
                </button>
                <cf-button kind="ghost" (click)="cancelRouteEdit()">Cancel</cf-button>
              </div>
            </form>
          </div>
        }
      </cf-card>

      <!-- Templates (sc-63) -->
      <cf-card title="Templates">
        <p class="muted small" style="margin-top: 0">
          Scriban-rendered subject + body for HITL notifications. Each save creates an
          immutable new version; routes pin a specific version so editing a template never
          silently changes what production sends.
        </p>
        <div class="row" style="margin-bottom: 0.75rem; gap: 0.5rem">
          <cf-button (click)="newTemplate()">+ New template</cf-button>
        </div>

        @if (templates().length === 0) {
          <div class="muted">No templates yet. Routes need at least one to render notifications.</div>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>Template id</th>
                <th>Event</th>
                <th>Channel</th>
                <th>Latest</th>
                <th>Updated</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (t of templates(); track t.templateId) {
                <tr>
                  <td><code class="code-inline">{{ t.templateId }}</code></td>
                  <td>{{ t.eventKind }}</td>
                  <td><cf-chip>{{ t.channel }}</cf-chip></td>
                  <td><span class="muted small">v{{ t.version }}</span></td>
                  <td><span class="muted small">{{ t.updatedAtUtc | date:'shortDate' }}</span></td>
                  <td class="actions">
                    <cf-button kind="ghost" (click)="editTemplate(t)">Edit</cf-button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }

        @if (templateEdit(); as edit) {
          <div class="edit-panel">
            <h3>{{ edit.isNew ? 'New template' : 'Edit template — ' + edit.templateId }}</h3>
            @if (edit.error) {
              <div class="trace-failure">{{ edit.error }}</div>
            }
            <form (submit)="saveTemplate($event)">
              <div class="form-row">
                <label>Template id</label>
                <input type="text" [(ngModel)]="edit.templateId" name="templateId" required
                  [disabled]="!edit.isNew" placeholder="hitl-task-pending/slack/default">
              </div>
              <div class="form-row">
                <label>Event kind</label>
                <select [(ngModel)]="edit.eventKind" name="eventKind">
                  @for (k of eventKinds; track k) {
                    <option [value]="k">{{ k }}</option>
                  }
                </select>
              </div>
              <div class="form-row">
                <label>Channel</label>
                <select [(ngModel)]="edit.channel" name="channel">
                  @for (c of channels; track c) {
                    <option [value]="c">{{ c }}</option>
                  }
                </select>
              </div>
              <div class="form-row">
                <label>Subject template</label>
                <textarea [(ngModel)]="edit.subjectTemplate" name="subjectTemplate" rows="2"
                  [placeholder]="subjectPlaceholder(edit.channel)"></textarea>
                <span class="muted small">{{ subjectHelp(edit.channel) }}</span>
              </div>
              <div class="form-row">
                <label>Body template</label>
                <textarea [(ngModel)]="edit.bodyTemplate" name="bodyTemplate" rows="8" required
                  placeholder="Body — Scriban syntax. Use {{ '{{' }} action_url {{ '}}' }}, {{ '{{' }} workflow_key {{ '}}' }}, etc."></textarea>
                <span class="muted small">
                  Available variables: action_url, hitl_task_id, trace_id, round_id, node_id,
                  workflow_key, workflow_version, agent_key, agent_version, severity,
                  input_preview, subflow_path. See docs/notifications.md.
                </span>
              </div>

              <div class="actions">
                <button type="submit" cf-button [disabled]="edit.saving">
                  {{ edit.saving ? 'Saving…' : 'Save (new version)' }}
                </button>
                <cf-button kind="ghost" (click)="previewTemplate()" [disabled]="edit.previewing">
                  {{ edit.previewing ? 'Rendering…' : 'Preview' }}
                </cf-button>
                <cf-button kind="ghost" (click)="cancelTemplateEdit()">Cancel</cf-button>
              </div>
            </form>

            @if (edit.preview; as p) {
              <div style="margin-top: 1rem; padding-top: 1rem; border-top: 1px solid var(--cf-border, #e5e7eb)">
                <h4 style="margin: 0 0 0.5rem 0">Preview</h4>
                @if (p.errorCode) {
                  <div class="form-row">
                    <label>Render failed</label>
                    <div>
                      <code class="code-inline">{{ p.errorCode }}</code>
                      <span class="muted small">{{ p.errorMessage }}</span>
                    </div>
                  </div>
                } @else {
                  @if (p.subject) {
                    <div class="form-row">
                      <label>Rendered subject</label>
                      <div>{{ p.subject }}</div>
                    </div>
                  }
                  <div class="form-row">
                    <label>Rendered body</label>
                    <textarea readonly rows="6" style="font-family: ui-monospace, monospace; font-size: 0.85rem">{{ p.body }}</textarea>
                  </div>
                }
                <p class="muted small" style="margin: 0">
                  Rendered against a synthetic HitlTaskPendingEvent (HitlTaskId=42, "preview" workflow/agent keys).
                </p>
              </div>
            }
          </div>
        }
      </cf-card>

      <!-- Delivery audit (sc-59) -->
      <cf-card title="Delivery attempts">
        <p class="muted small" style="margin-top: 0">
          Provider delivery audit. Every dispatcher attempt is recorded — Sent, Failed, Skipped,
          Retrying, or Suppressed — with a secret-stripped destination. Filter by event id when
          troubleshooting a specific HITL task.
        </p>
        <div class="filter-row">
          <label>
            <span class="muted small">Event id</span>
            <input type="text" [(ngModel)]="audit().filterEventId" name="filterEventId"
              placeholder="GUID" (keyup.enter)="applyAuditFilters()">
          </label>
          <label>
            <span class="muted small">Provider</span>
            <select [(ngModel)]="audit().filterProviderId" name="filterProviderId" (change)="applyAuditFilters()">
              <option value="">All</option>
              @for (p of providers(); track p.id) {
                <option [value]="p.id">{{ p.id }}</option>
              }
            </select>
          </label>
          <label>
            <span class="muted small">Route</span>
            <select [(ngModel)]="audit().filterRouteId" name="filterRouteId" (change)="applyAuditFilters()">
              <option value="">All</option>
              @for (r of routes(); track r.routeId) {
                <option [value]="r.routeId">{{ r.routeId }}</option>
              }
            </select>
          </label>
          <label>
            <span class="muted small">Status</span>
            <select [(ngModel)]="audit().filterStatus" name="filterStatus" (change)="applyAuditFilters()">
              <option value="">All</option>
              @for (s of deliveryStatuses; track s) {
                <option [value]="s">{{ s }}</option>
              }
            </select>
          </label>
          <label>
            <span class="muted small">Since (ISO UTC)</span>
            <input type="text" [(ngModel)]="audit().filterSinceUtc" name="filterSinceUtc"
              placeholder="2026-04-30T00:00:00Z" (keyup.enter)="applyAuditFilters()">
          </label>
          <div class="filter-actions">
            <cf-button kind="ghost" (click)="applyAuditFilters()" [disabled]="audit().loading">
              {{ audit().loading ? 'Loading…' : 'Apply' }}
            </cf-button>
            <cf-button kind="ghost" (click)="clearAuditFilters()">Clear</cf-button>
          </div>
        </div>

        @if (audit().error) {
          <div class="trace-failure">{{ audit().error }}</div>
        }

        @if (!audit().loading && audit().items.length === 0) {
          <div class="muted">No delivery attempts found for the current filters.</div>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>Attempted</th>
                <th>Status</th>
                <th>Provider</th>
                <th>Route</th>
                <th>Event</th>
                <th>Destination</th>
                <th>Attempt</th>
                <th>Error</th>
              </tr>
            </thead>
            <tbody>
              @for (a of audit().items; track a.id) {
                <tr>
                  <td><span class="muted small">{{ a.attemptedAtUtc | date:'medium' }}</span></td>
                  <td>
                    @switch (a.status) {
                      @case ('Sent')       { <cf-chip tone="ok">Sent</cf-chip> }
                      @case ('Failed')     { <cf-chip tone="warn">Failed</cf-chip> }
                      @case ('Retrying')   { <cf-chip tone="warn">Retrying</cf-chip> }
                      @case ('Skipped')    { <cf-chip tone="muted">Skipped</cf-chip> }
                      @case ('Suppressed') { <cf-chip tone="muted">Suppressed</cf-chip> }
                      @default             { <cf-chip>{{ a.status }}</cf-chip> }
                    }
                  </td>
                  <td><code class="code-inline">{{ a.providerId }}</code></td>
                  <td><code class="code-inline">{{ a.routeId }}</code></td>
                  <td>
                    <span class="muted small">{{ a.eventKind }}</span>
                    <code class="code-inline" style="margin-left: 0.25rem">{{ a.eventId.substring(0, 8) }}…</code>
                  </td>
                  <td><code class="code-inline">{{ a.normalizedDestination }}</code></td>
                  <td><span class="muted small">#{{ a.attemptNumber }}</span></td>
                  <td>
                    @if (a.errorCode) {
                      <code class="code-inline">{{ a.errorCode }}</code>
                      @if (a.errorMessage) {
                        <span class="muted small" style="margin-left: 0.25rem">{{ a.errorMessage }}</span>
                      }
                    } @else if (a.providerMessageId) {
                      <span class="muted small">id: <code class="code-inline">{{ a.providerMessageId }}</code></span>
                    } @else {
                      <span class="muted small">—</span>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>

          @if (audit().nextBeforeId !== null) {
            <div class="row" style="margin-top: 0.75rem; justify-content: center">
              <cf-button kind="ghost" (click)="loadMoreAudit()" [disabled]="audit().loading">
                {{ audit().loading ? 'Loading…' : 'Load more' }}
              </cf-button>
            </div>
          }
        }
      </cf-card>
    </div>
  `,
  styles: [`
    .page { display: flex; flex-direction: column; gap: 1rem; }
    .row { display: flex; align-items: center; gap: 0.5rem; }
    .diag-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 1rem; }
    .data-table { width: 100%; border-collapse: collapse; font-size: 0.9rem; }
    .data-table th, .data-table td { padding: 0.4rem 0.6rem; text-align: left; border-bottom: 1px solid var(--cf-border, #e5e7eb); vertical-align: middle; }
    .data-table th { font-weight: 600; color: var(--cf-muted, #6b7280); font-size: 0.8rem; text-transform: uppercase; letter-spacing: 0.04em; }
    .data-table .actions { display: flex; gap: 0.25rem; justify-content: flex-end; }
    .edit-panel { margin-top: 1rem; padding: 1rem; border: 1px solid var(--cf-border, #e5e7eb); border-radius: 0.5rem; background: var(--cf-surface-2, #f9fafb); }
    .edit-panel h3 { margin: 0 0 0.75rem 0; font-size: 1rem; }
    .form-row { display: grid; grid-template-columns: 180px 1fr; gap: 0.5rem; align-items: start; margin-bottom: 0.75rem; }
    .form-row > label { font-weight: 500; padding-top: 0.4rem; }
    .form-row input[type="text"], .form-row input[type="password"], .form-row input[type="number"],
    .form-row select, .form-row textarea {
      width: 100%; padding: 0.4rem 0.6rem; border: 1px solid var(--cf-border, #d1d5db); border-radius: 0.35rem;
      font: inherit;
    }
    .form-row textarea { font-family: ui-monospace, monospace; font-size: 0.85rem; }
    .form-row > div { display: flex; gap: 0.5rem; align-items: center; flex-wrap: wrap; }
    .actions { display: flex; gap: 0.5rem; }
    .code-inline { font-family: ui-monospace, monospace; font-size: 0.85rem; padding: 0.05rem 0.3rem; background: var(--cf-surface-2, #f3f4f6); border-radius: 0.25rem; }
    .trace-failure { color: var(--cf-danger, #b91c1c); padding: 0.5rem 0.75rem; background: var(--cf-danger-bg, #fef2f2); border-radius: 0.35rem; }
    .small { font-size: 0.85rem; }
    .muted { color: var(--cf-muted, #6b7280); }
    .filter-row { display: flex; flex-wrap: wrap; gap: 0.75rem; align-items: flex-end; margin: 0.5rem 0 1rem; }
    .filter-row label { display: flex; flex-direction: column; gap: 0.2rem; min-width: 12rem; flex: 1 1 12rem; }
    .filter-row input, .filter-row select {
      padding: 0.4rem 0.6rem; border: 1px solid var(--cf-border, #d1d5db); border-radius: 0.35rem;
      font: inherit;
    }
    .filter-actions { display: flex; gap: 0.4rem; align-items: center; }
  `]
})
export class NotificationsAdminComponent implements OnInit {
  private readonly api = inject(NotificationsAdminApi);

  readonly channels = NOTIFICATION_CHANNELS;
  readonly eventKinds = NOTIFICATION_EVENT_KINDS;
  readonly severities = NOTIFICATION_SEVERITIES;
  readonly deliveryStatuses = NOTIFICATION_DELIVERY_STATUSES;

  readonly providers = signal<NotificationProviderResponse[]>([]);
  readonly routes = signal<NotificationRouteResponse[]>([]);
  readonly templates = signal<NotificationTemplateResponse[]>([]);
  readonly templateEdit = signal<TemplateEditState | null>(null);
  readonly diagnostics = signal<NotificationDiagnosticsResponse | null>(null);
  readonly loadError = signal<string | null>(null);

  readonly providerEdit = signal<ProviderEditState | null>(null);
  readonly routeEdit = signal<RouteEditState | null>(null);
  readonly testSendState = signal<TestSendState | null>(null);
  readonly validatingProviderId = signal<string | null>(null);
  readonly validationOutcome = signal<ValidationOutcome | null>(null);

  // sc-59 — delivery audit panel state. Initialised once; mutated in place so the form
  // bindings on the inputs survive Apply/Clear cycles.
  readonly audit = signal<DeliveryAuditState>({
    filterEventId: '',
    filterProviderId: '',
    filterRouteId: '',
    filterStatus: '',
    filterSinceUtc: '',
    items: [],
    nextBeforeId: null,
    loading: false,
    error: null,
  });

  includeArchivedProviders = false;

  ngOnInit(): void {
    this.reloadAll();
  }

  reloadAll(): void {
    this.loadError.set(null);
    this.api.getDiagnostics().subscribe({
      next: d => this.diagnostics.set(d),
      error: e => this.loadError.set(formatHttpError(e)),
    });
    this.reloadProviders();
    this.reloadRoutes();
    this.reloadTemplates();
    this.applyAuditFilters();
  }

  reloadTemplates(): void {
    this.api.listTemplates().subscribe({
      next: list => this.templates.set(list),
      error: e => this.loadError.set(formatHttpError(e)),
    });
  }

  reloadProviders(): void {
    this.api.listProviders(this.includeArchivedProviders).subscribe({
      next: list => this.providers.set(list),
      error: e => this.loadError.set(formatHttpError(e)),
    });
  }

  reloadRoutes(): void {
    this.api.listRoutes().subscribe({
      next: list => this.routes.set(list),
      error: e => this.loadError.set(formatHttpError(e)),
    });
  }

  newProvider(): void {
    this.providerEdit.set({
      id: '',
      isNew: true,
      displayName: '',
      channel: 'Slack',
      endpointUrl: '',
      fromAddress: '',
      additionalConfigJson: '',
      enabled: true,
      hasCredential: false,
      credentialAction: 'Replace',
      credentialValue: '',
      saving: false,
      error: null,
    });
  }

  editProvider(p: NotificationProviderResponse): void {
    this.providerEdit.set({
      id: p.id,
      isNew: false,
      displayName: p.displayName,
      channel: p.channel,
      endpointUrl: p.endpointUrl ?? '',
      fromAddress: p.fromAddress ?? '',
      additionalConfigJson: p.additionalConfigJson ?? '',
      enabled: p.enabled,
      hasCredential: p.hasCredential,
      credentialAction: 'Preserve',
      credentialValue: '',
      saving: false,
      error: null,
    });
  }

  cancelProviderEdit(): void {
    this.providerEdit.set(null);
  }

  setCredentialAction(state: ProviderEditState, action: NotificationCredentialAction): void {
    state.credentialAction = action;
    if (action !== 'Replace') {
      state.credentialValue = '';
    }
  }

  saveProvider(event: Event): void {
    event.preventDefault();
    const state = this.providerEdit();
    if (!state) return;
    state.saving = true;
    state.error = null;

    const request: NotificationProviderWriteRequest = {
      displayName: state.displayName,
      channel: state.channel,
      endpointUrl: state.endpointUrl || null,
      fromAddress: state.fromAddress || null,
      additionalConfigJson: state.additionalConfigJson || null,
      enabled: state.enabled,
      credential: this.buildCredentialUpdate(state),
    };

    this.api.putProvider(state.id, request).subscribe({
      next: () => {
        this.providerEdit.set(null);
        this.reloadProviders();
        this.reloadDiagnostics();
      },
      error: (e) => {
        state.saving = false;
        state.error = formatHttpError(e);
      },
    });
  }

  archiveProvider(p: NotificationProviderResponse): void {
    if (!confirm(`Archive provider "${p.id}"? Routes referencing it will fail at dispatch until reassigned.`)) {
      return;
    }
    this.api.archiveProvider(p.id).subscribe({
      next: () => {
        this.reloadProviders();
        this.reloadDiagnostics();
      },
      error: (e) => this.loadError.set(formatHttpError(e)),
    });
  }

  validateProvider(p: NotificationProviderResponse): void {
    // sc-58 — credential check only, no message sent. The result lands in the inline
    // validation chip on the provider's row. One outstanding validation at a time.
    this.validatingProviderId.set(p.id);
    this.validationOutcome.set(null);
    this.api.validateProvider(p.id).subscribe({
      next: (result) => {
        this.validatingProviderId.set(null);
        this.validationOutcome.set({ providerId: p.id, result });
      },
      error: (e) => {
        this.validatingProviderId.set(null);
        this.loadError.set(formatHttpError(e));
      },
    });
  }

  dismissValidation(): void {
    this.validationOutcome.set(null);
  }

  openTestSend(p: NotificationProviderResponse): void {
    this.testSendState.set({
      providerId: p.id,
      channel: p.channel,
      recipientAddress: '',
      recipientDisplayName: '',
      useTemplate: false,
      templateId: '',
      templateVersion: 1,
      sending: false,
      result: null,
      error: null,
    });
  }

  cancelTestSend(): void {
    this.testSendState.set(null);
  }

  runTestSend(event: Event): void {
    event.preventDefault();
    const state = this.testSendState();
    if (!state) return;

    if (!state.recipientAddress) {
      state.error = 'recipient.address is required.';
      return;
    }

    if (state.useTemplate && (!state.templateId || state.templateVersion <= 0)) {
      state.error = 'Template id + version must be set when "Use template" is on.';
      return;
    }

    state.sending = true;
    state.error = null;
    state.result = null;

    this.api.testSendProvider(state.providerId, {
      recipient: {
        channel: state.channel,
        address: state.recipientAddress,
        displayName: state.recipientDisplayName || null,
      },
      template: state.useTemplate
        ? { templateId: state.templateId, version: state.templateVersion }
        : null,
    }).subscribe({
      next: (result) => {
        state.sending = false;
        state.result = result;
      },
      error: (e) => {
        state.sending = false;
        state.error = formatHttpError(e);
      },
    });
  }

  recipientPlaceholder(channel: NotificationChannel): string {
    switch (channel) {
      case 'Email': return 'reviewer@example.com';
      case 'Sms':   return '+15551234567 (E.164)';
      case 'Slack': return 'C012AB3CD (channel id) or U… (user id)';
    }
  }

  newRoute(): void {
    this.routeEdit.set({
      routeId: '',
      isNew: true,
      eventKind: 'HitlTaskPending',
      providerId: '',
      recipientsJson: '[]',
      templateId: '',
      templateVersion: 1,
      minimumSeverity: 'Normal',
      enabled: true,
      saving: false,
      error: null,
    });
  }

  editRoute(r: NotificationRouteResponse): void {
    this.routeEdit.set({
      routeId: r.routeId,
      isNew: false,
      eventKind: r.eventKind,
      providerId: r.providerId,
      recipientsJson: JSON.stringify(r.recipients, null, 2),
      templateId: r.template.templateId,
      templateVersion: r.template.version,
      minimumSeverity: r.minimumSeverity,
      enabled: r.enabled,
      saving: false,
      error: null,
    });
  }

  cancelRouteEdit(): void {
    this.routeEdit.set(null);
  }

  saveRoute(event: Event): void {
    event.preventDefault();
    const state = this.routeEdit();
    if (!state) return;

    let recipients: any;
    try {
      recipients = JSON.parse(state.recipientsJson);
    } catch (e: any) {
      state.error = `Recipients JSON is invalid: ${e?.message ?? e}`;
      return;
    }

    if (!Array.isArray(recipients) || recipients.length === 0) {
      state.error = 'Recipients must be a non-empty JSON array.';
      return;
    }

    state.saving = true;
    state.error = null;

    const request: NotificationRouteWriteRequest = {
      eventKind: state.eventKind,
      providerId: state.providerId,
      recipients,
      template: { templateId: state.templateId, version: state.templateVersion },
      minimumSeverity: state.minimumSeverity,
      enabled: state.enabled,
    };

    this.api.putRoute(state.routeId, request).subscribe({
      next: () => {
        this.routeEdit.set(null);
        this.reloadRoutes();
        this.reloadDiagnostics();
      },
      error: (e) => {
        state.saving = false;
        state.error = formatHttpError(e);
      },
    });
  }

  deleteRoute(r: NotificationRouteResponse): void {
    if (!confirm(`Delete route "${r.routeId}"? This is permanent (no soft-delete for routes).`)) {
      return;
    }
    this.api.deleteRoute(r.routeId).subscribe({
      next: () => {
        this.reloadRoutes();
        this.reloadDiagnostics();
      },
      error: (e) => this.loadError.set(formatHttpError(e)),
    });
  }

  fromPlaceholder(channel: NotificationChannel): string {
    switch (channel) {
      case 'Email': return 'ops@codeflow.example.com (verified sender)';
      case 'Sms':   return '+15551234567 or MGxxxxxxxx (Messaging Service SID)';
      case 'Slack': return '(unused — leave blank)';
    }
  }

  credentialPlaceholder(channel: NotificationChannel): string {
    switch (channel) {
      case 'Email': return 'JSON {"access_key":..,"secret_key":..} for SES, or plain SMTP password';
      case 'Sms':   return 'JSON {"account_sid":"AC...","auth_token":"..."}';
      case 'Slack': return 'xoxb-… bot token';
    }
  }

  credentialHelp(channel: NotificationChannel): string {
    switch (channel) {
      case 'Email':
        return 'SES with explicit creds: JSON {"access_key":"AKIA…","secret_key":"…"}; SES with IAM role: leave blank; SMTP: paste the password.';
      case 'Sms':
        return 'Twilio: JSON {"account_sid":"AC…","auth_token":"…"}.';
      case 'Slack':
        return 'Slack bot token (xoxb-…). Required for chat.postMessage.';
    }
  }

  additionalConfigPlaceholder(channel: NotificationChannel): string {
    switch (channel) {
      case 'Email': return '{"engine":"ses","region":"us-east-1"}  OR  {"engine":"smtp","host":"…","port":587,"username":"…","useStartTls":true}';
      case 'Sms':   return '(none required for v1 / Twilio)';
      case 'Slack': return '(optional, e.g. {"workspace":"acme"})';
    }
  }

  additionalConfigHelp(channel: NotificationChannel): string {
    switch (channel) {
      case 'Email': return 'Email engine selector + engine-specific settings. Required.';
      case 'Sms':   return "Currently optional — Twilio doesn't need additional config.";
      case 'Slack': return 'Optional opaque settings the provider may surface in audit rows.';
    }
  }

  /**
   * sc-59 — apply current filters and pull the first page from scratch. Resets the cursor.
   * `since` accepts an ISO 8601 string; the API also takes a Z suffix.
   */
  applyAuditFilters(): void {
    const state = this.audit();
    state.loading = true;
    state.error = null;
    state.items = [];
    state.nextBeforeId = null;

    this.api.listDeliveryAttempts({
      eventId: state.filterEventId || null,
      providerId: state.filterProviderId || null,
      routeId: state.filterRouteId || null,
      status: state.filterStatus || null,
      sinceUtc: state.filterSinceUtc || null,
      beforeId: null,
      limit: 50,
    }).subscribe({
      next: (page) => {
        state.items = page.items;
        state.nextBeforeId = page.nextBeforeId ?? null;
        state.loading = false;
        this.audit.set({ ...state });
      },
      error: (e) => {
        state.loading = false;
        state.error = formatHttpError(e);
        this.audit.set({ ...state });
      },
    });
  }

  loadMoreAudit(): void {
    const state = this.audit();
    if (state.nextBeforeId === null) return;

    state.loading = true;
    state.error = null;

    this.api.listDeliveryAttempts({
      eventId: state.filterEventId || null,
      providerId: state.filterProviderId || null,
      routeId: state.filterRouteId || null,
      status: state.filterStatus || null,
      sinceUtc: state.filterSinceUtc || null,
      beforeId: state.nextBeforeId,
      limit: 50,
    }).subscribe({
      next: (page) => {
        state.items = [...state.items, ...page.items];
        state.nextBeforeId = page.nextBeforeId ?? null;
        state.loading = false;
        this.audit.set({ ...state });
      },
      error: (e) => {
        state.loading = false;
        state.error = formatHttpError(e);
        this.audit.set({ ...state });
      },
    });
  }

  clearAuditFilters(): void {
    const state = this.audit();
    state.filterEventId = '';
    state.filterProviderId = '';
    state.filterRouteId = '';
    state.filterStatus = '';
    state.filterSinceUtc = '';
    this.applyAuditFilters();
  }

  // sc-63 — template authoring -----------------------------------------------------

  newTemplate(): void {
    this.templateEdit.set({
      templateId: '',
      isNew: true,
      eventKind: 'HitlTaskPending',
      channel: 'Slack',
      subjectTemplate: '',
      bodyTemplate: '',
      saving: false,
      error: null,
      preview: null,
      previewing: false,
    });
  }

  editTemplate(t: NotificationTemplateResponse): void {
    this.templateEdit.set({
      templateId: t.templateId,
      isNew: false,
      eventKind: t.eventKind,
      channel: t.channel,
      subjectTemplate: t.subjectTemplate ?? '',
      bodyTemplate: t.bodyTemplate,
      saving: false,
      error: null,
      preview: null,
      previewing: false,
    });
  }

  cancelTemplateEdit(): void {
    this.templateEdit.set(null);
  }

  saveTemplate(event: Event): void {
    event.preventDefault();
    const state = this.templateEdit();
    if (!state) return;
    state.saving = true;
    state.error = null;

    const request: NotificationTemplateWriteRequest = {
      eventKind: state.eventKind,
      channel: state.channel,
      subjectTemplate: state.subjectTemplate || null,
      bodyTemplate: state.bodyTemplate,
    };

    this.api.putTemplate(state.templateId, request).subscribe({
      next: () => {
        this.templateEdit.set(null);
        this.reloadTemplates();
      },
      error: (e) => {
        state.saving = false;
        state.error = formatHttpError(e);
      },
    });
  }

  previewTemplate(): void {
    const state = this.templateEdit();
    if (!state) return;
    state.previewing = true;
    state.preview = null;
    state.error = null;

    this.api.previewTemplate({
      eventKind: state.eventKind,
      channel: state.channel,
      subjectTemplate: state.subjectTemplate || null,
      bodyTemplate: state.bodyTemplate,
    }).subscribe({
      next: (preview) => {
        state.preview = preview;
        state.previewing = false;
      },
      error: (e) => {
        state.previewing = false;
        state.error = formatHttpError(e);
      },
    });
  }

  subjectPlaceholder(channel: NotificationChannel): string {
    switch (channel) {
      case 'Email': return '[CodeFlow] HITL review needed for {{ workflow_key }}';
      case 'Slack': return '(optional — used as the notification fallback)';
      case 'Sms':   return '(leave empty — SMS has no subject)';
    }
  }

  subjectHelp(channel: NotificationChannel): string {
    switch (channel) {
      case 'Email': return 'Required. Scriban-rendered.';
      case 'Slack': return 'Optional. Slack uses it as the screen-reader fallback.';
      case 'Sms':   return 'Must be empty for SMS — providers ignore it.';
    }
  }

  private reloadDiagnostics(): void {
    this.api.getDiagnostics().subscribe({
      next: d => this.diagnostics.set(d),
      error: () => { /* don't blow up the page on a diag-only failure */ },
    });
  }

  private buildCredentialUpdate(state: ProviderEditState) {
    if (state.isNew) {
      // First-time save: if a value was entered, treat it as Replace; else explicit Clear.
      return state.credentialValue
        ? { action: 'Replace' as const, value: state.credentialValue }
        : { action: 'Preserve' as const, value: null };
    }

    if (state.credentialAction === 'Replace') {
      return { action: 'Replace' as const, value: state.credentialValue };
    }
    if (state.credentialAction === 'Clear') {
      return { action: 'Clear' as const, value: null };
    }
    return { action: 'Preserve' as const, value: null };
  }
}
