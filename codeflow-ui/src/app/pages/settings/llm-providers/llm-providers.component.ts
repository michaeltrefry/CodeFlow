import { Component, computed, inject, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { LlmProvidersApi } from '../../../core/llm-providers.api';
import {
  LLM_PROVIDER_KEYS,
  LlmProviderKey,
  LlmProviderResponse,
  LlmProviderTokenAction,
} from '../../../core/models';
import { PageHeaderComponent } from '../../../ui/page-header.component';
import { ButtonComponent } from '../../../ui/button.component';
import { ChipComponent } from '../../../ui/chip.component';
import { CardComponent } from '../../../ui/card.component';

interface ProviderFormState {
  provider: LlmProviderKey;
  displayName: string;
  endpointPlaceholder: string;
  supportsApiVersion: boolean;
  loading: boolean;
  saving: boolean;
  error: string | null;
  hasApiKey: boolean;
  endpointUrl: string;
  apiVersion: string;
  models: string[];
  newModel: string;
  replacingToken: boolean;
  tokenValue: string;
  clearToken: boolean;
  updatedBy: string | null;
  updatedAtUtc: string | null;
}

const PROVIDER_META: Record<LlmProviderKey, { displayName: string; placeholder: string; supportsApiVersion: boolean }> = {
  openai: {
    displayName: 'OpenAI',
    placeholder: 'https://api.openai.com/v1/responses',
    supportsApiVersion: false,
  },
  anthropic: {
    displayName: 'Anthropic',
    placeholder: 'https://api.anthropic.com/v1/messages',
    supportsApiVersion: true,
  },
  lmstudio: {
    displayName: 'LM Studio (local)',
    placeholder: 'http://localhost:1234/v1/responses',
    supportsApiVersion: false,
  },
};

@Component({
  selector: 'cf-llm-providers',
  standalone: true,
  imports: [
    FormsModule, DatePipe,
    PageHeaderComponent, ButtonComponent, ChipComponent, CardComponent,
  ],
  template: `
    <div class="page">
      <cf-page-header
        title="LLM providers"
        subtitle="API tokens (encrypted at rest), endpoint URLs, and available model lists for each provider. Changes apply on the next agent invocation (within 30s or after a save — whichever comes first).">
      </cf-page-header>

      @if (loading()) {
        <cf-card><div class="muted">Loading providers…</div></cf-card>
      } @else {
        @if (loadError()) {
          <cf-card>
            <div class="trace-failure"><strong>Couldn't load current settings:</strong> {{ loadError() }}</div>
          </cf-card>
        }
        @for (state of providers(); track state.provider) {
          <cf-card [title]="state.displayName">
            <form (submit)="save($event, state)">
              <div class="form-grid">
                <div class="field span-2">
                  <span class="field-label">API token</span>
                  @if (state.hasApiKey && !state.replacingToken && !state.clearToken) {
                    <div class="row">
                      <cf-chip mono>••••••••</cf-chip>
                      <button type="button" cf-button size="sm" (click)="startReplace(state)">Replace</button>
                      <button type="button" cf-button size="sm" variant="ghost" (click)="markClear(state)">Clear</button>
                    </div>
                    <span class="field-hint">Stored encrypted at rest. Never returned by the API.</span>
                  } @else if (state.clearToken) {
                    <div class="row">
                      <cf-chip variant="warn" dot>Will clear on save</cf-chip>
                      <button type="button" cf-button size="sm" variant="ghost" (click)="cancelClear(state)">Undo</button>
                    </div>
                  } @else {
                    <input type="password" class="input mono"
                           [ngModel]="state.tokenValue" (ngModelChange)="patch(state, { tokenValue: $event })"
                           [name]="'token_' + state.provider"
                           [placeholder]="state.hasApiKey ? 'New token (replaces current)' : 'Paste API token'"
                           autocomplete="new-password" />
                    @if (state.hasApiKey) {
                      <button type="button" cf-button variant="ghost" size="sm" (click)="cancelReplace(state)">Cancel replace</button>
                    }
                    <span class="field-hint">Stored encrypted at rest. Never returned by the API.</span>
                  }
                </div>

                <label class="field span-2">
                  <span class="field-label">Endpoint URL <span class="muted small">(optional — overrides the built-in default)</span></span>
                  <input class="input mono"
                         [ngModel]="state.endpointUrl" (ngModelChange)="patch(state, { endpointUrl: $event })"
                         [name]="'endpoint_' + state.provider" [placeholder]="state.endpointPlaceholder" />
                </label>

                @if (state.supportsApiVersion) {
                  <label class="field">
                    <span class="field-label">API version</span>
                    <input class="input mono"
                           [ngModel]="state.apiVersion" (ngModelChange)="patch(state, { apiVersion: $event })"
                           [name]="'apiversion_' + state.provider" placeholder="2023-06-01" />
                  </label>
                }

                <div class="field span-2">
                  <span class="field-label">Available models</span>
                  @if (state.models.length === 0) {
                    <span class="muted small">No models configured yet.</span>
                  } @else {
                    <div class="row" style="flex-wrap: wrap; gap: 6px">
                      @for (model of state.models; track $index; let i = $index) {
                        <span class="model-chip">
                          <span class="mono">{{ model }}</span>
                          <button type="button" class="model-remove" [attr.aria-label]="'Remove ' + model"
                                  (click)="removeModel(state, i)">×</button>
                        </span>
                      }
                    </div>
                  }
                  <div class="row" style="margin-top: 8px">
                    <input class="input mono" style="flex: 1"
                           [ngModel]="state.newModel" (ngModelChange)="patch(state, { newModel: $event })"
                           [name]="'newmodel_' + state.provider" placeholder="gpt-5.4-mini"
                           (keydown.enter)="addModel($event, state)" />
                    <button type="button" cf-button size="sm" icon="plus" (click)="addModel($event, state)"
                            [disabled]="!state.newModel.trim()">Add</button>
                  </div>
                </div>
              </div>

              @if (state.error) {
                <div class="trace-failure"><strong>Save failed:</strong> {{ state.error }}</div>
              }

              <div class="row" style="margin-top: 14px; justify-content: space-between; align-items: center">
                <span class="muted small">
                  @if (state.updatedAtUtc) {
                    Last updated {{ state.updatedAtUtc | date:'medium' }}
                    @if (state.updatedBy) { by {{ state.updatedBy }} }
                  } @else {
                    Never saved
                  }
                </span>
                <button type="submit" cf-button variant="primary" [disabled]="state.saving">
                  {{ state.saving ? 'Saving…' : 'Save' }}
                </button>
              </div>
            </form>
          </cf-card>
        }
      }
    </div>
  `,
  styles: [`
    .model-chip {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 2px 4px 2px 10px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface-2);
      font-size: var(--fs-sm);
    }
    .model-remove {
      border: 0;
      background: transparent;
      color: var(--muted);
      cursor: pointer;
      font-size: 1rem;
      line-height: 1;
      padding: 2px 6px;
      border-radius: var(--radius);
    }
    .model-remove:hover { color: var(--fg); background: var(--bg); }
  `]
})
export class LlmProvidersComponent implements OnInit {
  private readonly api = inject(LlmProvidersApi);

  protected readonly providers = signal<ProviderFormState[]>([]);
  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);

  ngOnInit(): void {
    this.load();
  }

  protected patch(state: ProviderFormState, patch: Partial<ProviderFormState>): void {
    this.providers.update(rows =>
      rows.map(r => (r.provider === state.provider ? { ...r, ...patch } : r)));
  }

  protected startReplace(state: ProviderFormState): void {
    this.patch(state, { replacingToken: true, clearToken: false, tokenValue: '' });
  }

  protected cancelReplace(state: ProviderFormState): void {
    this.patch(state, { replacingToken: false, tokenValue: '' });
  }

  protected markClear(state: ProviderFormState): void {
    this.patch(state, { clearToken: true, replacingToken: false, tokenValue: '' });
  }

  protected cancelClear(state: ProviderFormState): void {
    this.patch(state, { clearToken: false });
  }

  protected addModel(event: Event, state: ProviderFormState): void {
    event?.preventDefault?.();
    const trimmed = state.newModel.trim();
    if (!trimmed) return;
    if (state.models.includes(trimmed)) {
      this.patch(state, { newModel: '' });
      return;
    }
    this.patch(state, { models: [...state.models, trimmed], newModel: '' });
  }

  protected removeModel(state: ProviderFormState, index: number): void {
    this.patch(state, { models: state.models.filter((_, i) => i !== index) });
  }

  protected save(event: Event, state: ProviderFormState): void {
    event.preventDefault();
    this.patch(state, { saving: true, error: null });

    const tokenUpdate = state.clearToken
      ? { action: 'Clear' as LlmProviderTokenAction }
      : state.replacingToken || (!state.hasApiKey && state.tokenValue.length > 0)
        ? { action: 'Replace' as LlmProviderTokenAction, value: state.tokenValue }
        : { action: 'Preserve' as LlmProviderTokenAction };

    this.api
      .set(state.provider, {
        endpointUrl: state.endpointUrl.trim() || null,
        apiVersion: state.supportsApiVersion ? (state.apiVersion.trim() || null) : null,
        models: state.models,
        token: tokenUpdate,
      })
      .subscribe({
        next: response => {
          this.applyResponse(response);
        },
        error: err => {
          this.patch(state, { saving: false, error: this.formatError(err) });
        },
      });
  }

  private load(): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.api.list().subscribe({
      next: responses => {
        const byKey = new Map(responses.map(r => [r.provider, r] as const));
        const states = LLM_PROVIDER_KEYS.map(provider => {
          const response = byKey.get(provider);
          return this.buildState(provider, response);
        });
        this.providers.set(states);
        this.loading.set(false);
      },
      error: err => {
        // Seed empty forms so the UI is still usable once the backend comes back online; the
        // banner tells the operator why saving isn't going to work yet.
        this.providers.set(LLM_PROVIDER_KEYS.map(p => this.buildState(p, undefined)));
        this.loadError.set(this.formatError(err));
        this.loading.set(false);
      },
    });
  }

  private buildState(
    provider: LlmProviderKey,
    response: LlmProviderResponse | undefined,
  ): ProviderFormState {
    const meta = PROVIDER_META[provider];
    return {
      provider,
      displayName: meta.displayName,
      endpointPlaceholder: meta.placeholder,
      supportsApiVersion: meta.supportsApiVersion,
      loading: false,
      saving: false,
      error: null,
      hasApiKey: response?.hasApiKey ?? false,
      endpointUrl: response?.endpointUrl ?? '',
      apiVersion: response?.apiVersion ?? '',
      models: [...(response?.models ?? [])],
      newModel: '',
      replacingToken: false,
      tokenValue: '',
      clearToken: false,
      updatedBy: response?.updatedBy ?? null,
      updatedAtUtc: response?.updatedAtUtc ?? null,
    };
  }

  private applyResponse(response: LlmProviderResponse): void {
    this.providers.update(rows =>
      rows.map(r => (r.provider === response.provider ? this.buildState(response.provider, response) : r)));
  }

  private formatError(err: unknown): string {
    if (err && typeof err === 'object' && 'error' in err) {
      const body = (err as { error: unknown }).error;
      if (body && typeof body === 'object' && 'title' in body) {
        return String((body as { title: unknown }).title);
      }
      if (typeof body === 'string') {
        return body;
      }
    }
    return 'Request failed.';
  }
}
