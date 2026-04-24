import { Component, inject, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { GitHostApi } from '../../../core/git-host.api';
import { GitHostMode, GitHostSettingsResponse, GitHostVerifyResponse } from '../../../core/models';
import { PageHeaderComponent } from '../../../ui/page-header.component';
import { ButtonComponent } from '../../../ui/button.component';
import { ChipComponent } from '../../../ui/chip.component';
import { CardComponent } from '../../../ui/card.component';

@Component({
  selector: 'cf-git-host-settings',
  standalone: true,
  imports: [
    FormsModule, DatePipe,
    PageHeaderComponent, ButtonComponent, ChipComponent, CardComponent,
  ],
  template: `
    <div class="page">
      <cf-page-header
        title="Git host"
        subtitle="Configure the git host (GitHub or self-hosted GitLab) this CodeFlow instance will clone from, push to, and open PRs against.">
      </cf-page-header>

    @if (loading()) {
      <cf-card><div class="muted">Loading…</div></cf-card>
    } @else {
      <cf-card title="Provider connection">
      <form (submit)="save($event)">
        <div class="form-grid">
          <div class="field">
            <span class="field-label">Host</span>
            <div class="seg" style="width: fit-content">
              <button type="button" [attr.data-active]="mode() === 'GitHub' ? 'true' : null" (click)="setMode('GitHub')">GitHub</button>
              <button type="button" [attr.data-active]="mode() === 'GitLab' ? 'true' : null" (click)="setMode('GitLab')">Self-hosted GitLab</button>
            </div>
          </div>

          @if (mode() === 'GitLab') {
            <div class="field">
              <span class="field-label">Base URL</span>
              <input class="input mono" [ngModel]="baseUrl()" (ngModelChange)="baseUrl.set($event)" name="baseUrl"
                     placeholder="https://gitlab.example.com" />
              <span class="field-hint">No trailing slash; the API path <code>/api/v4</code> is appended automatically.</span>
            </div>
          }

          <div class="field span-2">
            <span class="field-label">Personal access token</span>
            @if (hasToken() && !replacingToken()) {
              <div class="row">
                <cf-chip mono>••••••••</cf-chip>
                <button type="button" cf-button size="sm" (click)="startReplace()">Replace token</button>
              </div>
              <span class="field-hint">Minimum scopes — GitHub: <code>repo</code>. GitLab: <code>api</code> + <code>write_repository</code>.</span>
            } @else {
              <input type="password" class="input mono" [ngModel]="tokenValue()" (ngModelChange)="tokenValue.set($event)"
                     name="token" placeholder="Paste a personal access token" autocomplete="new-password" />
              @if (hasToken()) {
                <button type="button" cf-button variant="ghost" size="sm" (click)="cancelReplace()">Cancel replace</button>
              }
              <span class="field-hint">Tokens are encrypted at rest and never returned by the API.</span>
            }
          </div>
        </div>

        @if (error()) {
          <div class="trace-failure"><strong>Save failed:</strong> {{ error() }}</div>
        }

        <div class="row" style="margin-top: 14px; justify-content: flex-end">
          <button type="submit" cf-button variant="primary" [disabled]="saving() || !canSave()">
            {{ saving() ? 'Saving…' : 'Save' }}
          </button>
        </div>
      </form>
      </cf-card>

      <cf-card title="Verify">
        <ng-template #cardRight>
          <button type="button" cf-button (click)="verify()" [disabled]="verifying() || !hasToken() || replacingToken()">
            {{ verifying() ? 'Verifying…' : 'Verify connection' }}
          </button>
        </ng-template>

        <div class="row">
          @if (statusOk()) {
            <cf-chip variant="ok" dot>Healthy</cf-chip>
          } @else if (statusFailed()) {
            <cf-chip variant="err" dot>Failed</cf-chip>
          } @else if (statusUnverified()) {
            <cf-chip variant="warn" dot>Unverified</cf-chip>
          } @else {
            <cf-chip>No token</cf-chip>
          }
          @if (lastVerifiedAtUtc()) {
            <span class="muted small">Last verified: {{ lastVerifiedAtUtc() | date:'medium' }}</span>
          } @else {
            <span class="muted small">{{ hasToken() ? 'Not yet verified' : 'No token configured yet' }}</span>
          }
        </div>

        @if (verifyError()) {
          <div class="trace-failure" style="margin-top: 10px"><strong>Error:</strong> {{ verifyError() }}</div>
        }
      </cf-card>
    }
    </div>
  `,
  styles: [``],
})
export class GitHostSettingsComponent implements OnInit {
  private readonly api = inject(GitHostApi);

  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly verifying = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly verifyError = signal<string | null>(null);

  protected readonly mode = signal<GitHostMode>('GitHub');
  protected readonly baseUrl = signal<string>('');
  protected readonly hasToken = signal(false);
  protected readonly replacingToken = signal(false);
  protected readonly tokenValue = signal<string>('');
  protected readonly lastVerifiedAtUtc = signal<string | null>(null);

  protected statusOk = (): boolean => this.hasToken() && !!this.lastVerifiedAtUtc() && !this.verifyError();
  protected statusUnverified = (): boolean => this.hasToken() && !this.lastVerifiedAtUtc() && !this.verifyError();
  protected statusFailed = (): boolean => !!this.verifyError();

  ngOnInit(): void {
    this.load();
  }

  protected setMode(mode: GitHostMode): void {
    this.mode.set(mode);
    if (mode === 'GitHub') {
      this.baseUrl.set('');
    }
  }

  protected startReplace(): void {
    this.replacingToken.set(true);
    this.tokenValue.set('');
  }

  protected cancelReplace(): void {
    this.replacingToken.set(false);
    this.tokenValue.set('');
  }

  protected canSave(): boolean {
    if (this.mode() === 'GitLab' && !this.baseUrl()?.trim()) {
      return false;
    }
    if (!this.hasToken() || this.replacingToken()) {
      return this.tokenValue().length > 0;
    }
    return true;
  }

  protected save(event: Event): void {
    event.preventDefault();
    if (!this.canSave()) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);
    const isReplacing = !this.hasToken() || this.replacingToken();
    this.api
      .set({
        mode: this.mode(),
        baseUrl: this.mode() === 'GitLab' ? this.baseUrl() : null,
        token: isReplacing
          ? { action: 'Replace', value: this.tokenValue() }
          : { action: 'Preserve' },
      })
      .subscribe({
        next: response => {
          this.applyResponse(response);
          this.tokenValue.set('');
          this.replacingToken.set(false);
          this.saving.set(false);
        },
        error: err => {
          this.error.set(this.formatError(err));
          this.saving.set(false);
        },
      });
  }

  protected verify(): void {
    this.verifying.set(true);
    this.verifyError.set(null);
    this.api.verify().subscribe({
      next: (response: GitHostVerifyResponse) => {
        if (response.success) {
          this.lastVerifiedAtUtc.set(response.lastVerifiedAtUtc ?? null);
          this.verifyError.set(null);
        } else {
          this.verifyError.set(response.error ?? 'Verification failed.');
        }
        this.verifying.set(false);
      },
      error: err => {
        this.verifyError.set(this.formatError(err));
        this.verifying.set(false);
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.api.get().subscribe({
      next: response => {
        this.applyResponse(response);
        this.loading.set(false);
      },
      error: err => {
        this.error.set(this.formatError(err));
        this.loading.set(false);
      },
    });
  }

  private applyResponse(response: GitHostSettingsResponse): void {
    this.mode.set(response.mode);
    this.baseUrl.set(response.baseUrl ?? '');
    this.hasToken.set(response.hasToken);
    this.lastVerifiedAtUtc.set(response.lastVerifiedAtUtc ?? null);
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
