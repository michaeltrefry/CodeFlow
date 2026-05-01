import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { formatHttpError } from '../../../core/format-error';
import {
  NOTIFICATION_CHANNELS,
  NOTIFICATION_EVENT_KINDS,
  NOTIFICATION_SEVERITIES,
  NotificationChannel,
  NotificationCredentialAction,
  NotificationDiagnosticsResponse,
  NotificationEventKind,
  NotificationProviderResponse,
  NotificationProviderWriteRequest,
  NotificationRouteResponse,
  NotificationRouteWriteRequest,
  NotificationSeverity,
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
                      <cf-button kind="ghost" (click)="archiveProvider(p)">Archive</cf-button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
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
                <cf-button type="submit" [disabled]="edit.saving">
                  {{ edit.saving ? 'Saving…' : 'Save' }}
                </cf-button>
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
                <cf-button type="submit" [disabled]="edit.saving">
                  {{ edit.saving ? 'Saving…' : 'Save' }}
                </cf-button>
                <cf-button kind="ghost" (click)="cancelRouteEdit()">Cancel</cf-button>
              </div>
            </form>
          </div>
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
  `]
})
export class NotificationsAdminComponent implements OnInit {
  private readonly api = inject(NotificationsAdminApi);

  readonly channels = NOTIFICATION_CHANNELS;
  readonly eventKinds = NOTIFICATION_EVENT_KINDS;
  readonly severities = NOTIFICATION_SEVERITIES;

  readonly providers = signal<NotificationProviderResponse[]>([]);
  readonly routes = signal<NotificationRouteResponse[]>([]);
  readonly diagnostics = signal<NotificationDiagnosticsResponse | null>(null);
  readonly loadError = signal<string | null>(null);

  readonly providerEdit = signal<ProviderEditState | null>(null);
  readonly routeEdit = signal<RouteEditState | null>(null);

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
