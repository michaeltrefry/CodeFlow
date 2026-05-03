import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { formatHttpError } from '../../../core/format-error';
import { WebSearchProviderApi } from '../../../core/web-search-provider.api';
import {
  WEB_SEARCH_PROVIDER_DISPLAY_NAMES,
  WEB_SEARCH_PROVIDER_KEYS,
  WebSearchProviderKey,
  WebSearchProviderResponse,
  WebSearchProviderTokenAction,
} from '../../../core/models';
import { PageHeaderComponent } from '../../../ui/page-header.component';
import { ButtonComponent } from '../../../ui/button.component';
import { ChipComponent } from '../../../ui/chip.component';
import { CardComponent } from '../../../ui/card.component';

interface FormState {
  loading: boolean;
  saving: boolean;
  loadError: string | null;
  saveError: string | null;
  provider: WebSearchProviderKey;
  hasApiKey: boolean;
  endpointUrl: string;
  replacingToken: boolean;
  tokenValue: string;
  clearToken: boolean;
  updatedBy: string | null;
  updatedAtUtc: string | null;
}

const PROVIDER_PLACEHOLDERS: Record<WebSearchProviderKey, string> = {
  none: '',
  brave: 'https://api.search.brave.com/res/v1/web/search',
};

const PROVIDER_NEEDS_KEY: Record<WebSearchProviderKey, boolean> = {
  none: false,
  brave: true,
};

@Component({
  selector: 'cf-web-search-provider-settings',
  standalone: true,
  imports: [FormsModule, DatePipe, PageHeaderComponent, ButtonComponent, ChipComponent, CardComponent],
  template: `
    <div class="page">
      <cf-page-header
        title="Web search"
        subtitle="Backend that powers the agent-side web_search tool. Pick a provider and store its API key here; runtime picks up changes within 30 seconds (or immediately on save).">
      </cf-page-header>

      @if (state().loading) {
        <cf-card><div class="muted">Loading…</div></cf-card>
      } @else {
        @if (state().loadError) {
          <cf-card>
            <div class="trace-failure"><strong>Couldn't load current settings:</strong> {{ state().loadError }}</div>
          </cf-card>
        }
        <cf-card title="Active provider">
          <p class="muted small" style="margin-top: 0">
            web_search is a role-granted host tool. Until a real provider is selected here, every call
            returns a structured "search-not-configured" refusal so agents see a clear message instead
            of a silent failure. Selecting <em>None</em> is the same as having no row at all.
          </p>
          <form (submit)="save($event)">
            <div class="form-grid">
              <label class="field span-2">
                <span class="field-label">Provider</span>
                <select class="input"
                        [ngModel]="state().provider"
                        (ngModelChange)="patch({ provider: $event })"
                        name="provider">
                  @for (p of WEB_SEARCH_PROVIDER_KEYS; track p) {
                    <option [value]="p">{{ providerDisplayName(p) }}</option>
                  }
                </select>
                @if (state().provider === 'brave') {
                  <span class="field-hint">
                    Sign up at <span class="mono">https://api.search.brave.com</span> to mint a
                    subscription token. The free tier is plenty for development.
                  </span>
                }
              </label>

              @if (providerNeedsKey()) {
                <div class="field span-2">
                  <span class="field-label">API key</span>
                  @if (state().hasApiKey && !state().replacingToken && !state().clearToken) {
                    <div class="row">
                      <cf-chip mono>••••••••</cf-chip>
                      <button type="button" cf-button size="sm" (click)="startReplace()">Replace</button>
                      <button type="button" cf-button size="sm" variant="ghost" (click)="markClear()">Clear</button>
                    </div>
                    <span class="field-hint">Stored encrypted at rest. Never returned by the API.</span>
                  } @else if (state().clearToken) {
                    <div class="row">
                      <cf-chip variant="warn" dot>Will clear on save</cf-chip>
                      <button type="button" cf-button size="sm" variant="ghost" (click)="cancelClear()">Undo</button>
                    </div>
                  } @else {
                    <input type="password" class="input mono"
                           [ngModel]="state().tokenValue" (ngModelChange)="patch({ tokenValue: $event })"
                           name="token"
                           [placeholder]="state().hasApiKey ? 'New token (replaces current)' : 'Paste API key'"
                           autocomplete="new-password" />
                    @if (state().hasApiKey) {
                      <button type="button" cf-button variant="ghost" size="sm" (click)="cancelReplace()">Cancel replace</button>
                    }
                    <span class="field-hint">Stored encrypted at rest. Never returned by the API.</span>
                  }
                </div>

                <label class="field span-2">
                  <span class="field-label">Endpoint URL <span class="muted small">(optional — overrides the built-in default)</span></span>
                  <input class="input mono"
                         [ngModel]="state().endpointUrl" (ngModelChange)="patch({ endpointUrl: $event })"
                         name="endpoint" [placeholder]="endpointPlaceholder()" />
                </label>
              }
            </div>

            @if (state().saveError) {
              <div class="trace-failure"><strong>Save failed:</strong> {{ state().saveError }}</div>
            }

            <div class="row" style="margin-top: 14px; justify-content: space-between; align-items: center">
              <span class="muted small">
                @if (state().updatedAtUtc) {
                  Last updated {{ state().updatedAtUtc | date:'medium' }}
                  @if (state().updatedBy) { by {{ state().updatedBy }} }
                } @else {
                  Never saved
                }
              </span>
              <button type="submit" cf-button variant="primary" [disabled]="state().saving">
                {{ state().saving ? 'Saving…' : 'Save' }}
              </button>
            </div>
          </form>
        </cf-card>
      }
    </div>
  `,
})
export class WebSearchProviderSettingsComponent implements OnInit {
  private readonly api = inject(WebSearchProviderApi);

  protected readonly state = signal<FormState>({
    loading: true,
    saving: false,
    loadError: null,
    saveError: null,
    provider: 'none',
    hasApiKey: false,
    endpointUrl: '',
    replacingToken: false,
    tokenValue: '',
    clearToken: false,
    updatedBy: null,
    updatedAtUtc: null,
  });

  protected readonly WEB_SEARCH_PROVIDER_KEYS = WEB_SEARCH_PROVIDER_KEYS;

  ngOnInit(): void {
    this.load();
  }

  protected providerDisplayName(key: WebSearchProviderKey): string {
    return WEB_SEARCH_PROVIDER_DISPLAY_NAMES[key];
  }

  protected providerNeedsKey(): boolean {
    return PROVIDER_NEEDS_KEY[this.state().provider];
  }

  protected endpointPlaceholder(): string {
    return PROVIDER_PLACEHOLDERS[this.state().provider] ?? '';
  }

  protected patch(patch: Partial<FormState>): void {
    this.state.update(s => ({ ...s, ...patch }));
  }

  protected startReplace(): void {
    this.patch({ replacingToken: true, clearToken: false, tokenValue: '' });
  }

  protected cancelReplace(): void {
    this.patch({ replacingToken: false, tokenValue: '' });
  }

  protected markClear(): void {
    this.patch({ clearToken: true, replacingToken: false, tokenValue: '' });
  }

  protected cancelClear(): void {
    this.patch({ clearToken: false });
  }

  protected save(event: Event): void {
    event.preventDefault();
    const current = this.state();
    this.patch({ saving: true, saveError: null });

    const tokenUpdate = current.clearToken
      ? { action: 'Clear' as WebSearchProviderTokenAction }
      : current.replacingToken || (!current.hasApiKey && current.tokenValue.length > 0)
        ? { action: 'Replace' as WebSearchProviderTokenAction, value: current.tokenValue }
        : { action: 'Preserve' as WebSearchProviderTokenAction };

    this.api
      .set({
        provider: current.provider,
        endpointUrl: current.endpointUrl.trim() || null,
        token: tokenUpdate,
      })
      .subscribe({
        next: response => this.applyResponse(response),
        error: err => this.patch({ saving: false, saveError: formatHttpError(err) }),
      });
  }

  private load(): void {
    this.patch({ loading: true, loadError: null });
    this.api.get().subscribe({
      next: response => this.applyResponse(response),
      error: err => this.patch({
        loading: false,
        loadError: formatHttpError(err),
      }),
    });
  }

  private applyResponse(response: WebSearchProviderResponse): void {
    this.state.set({
      loading: false,
      saving: false,
      loadError: null,
      saveError: null,
      provider: response.provider,
      hasApiKey: response.hasApiKey,
      endpointUrl: response.endpointUrl ?? '',
      replacingToken: false,
      tokenValue: '',
      clearToken: false,
      updatedBy: response.updatedBy ?? null,
      updatedAtUtc: response.updatedAtUtc ?? null,
    });
  }
}
