import { Component, inject, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { GitHostApi } from '../../../core/git-host.api';
import { GitHostMode, GitHostSettingsResponse, GitHostVerifyResponse } from '../../../core/models';

@Component({
  selector: 'cf-git-host-settings',
  standalone: true,
  imports: [FormsModule, DatePipe],
  template: `
    <header class="page-header">
      <div>
        <h1>Git host</h1>
        <p class="muted">Configure the git host (GitHub or self-hosted GitLab) this CodeFlow instance
          will clone from, push to, and open PRs against.</p>
      </div>
    </header>

    @if (loading()) {
      <p class="muted">Loading&hellip;</p>
    } @else {
      <form (submit)="save($event)">
        <div class="form-field">
          <label>Host</label>
          <div class="radio-row">
            <label class="radio">
              <input type="radio" name="mode" value="GitHub" [checked]="mode() === 'GitHub'" (change)="setMode('GitHub')" />
              <span>GitHub (github.com)</span>
            </label>
            <label class="radio">
              <input type="radio" name="mode" value="GitLab" [checked]="mode() === 'GitLab'" (change)="setMode('GitLab')" />
              <span>Self-hosted GitLab</span>
            </label>
          </div>
        </div>

        @if (mode() === 'GitLab') {
          <div class="form-field">
            <label>Base URL</label>
            <input [ngModel]="baseUrl()" (ngModelChange)="baseUrl.set($event)" name="baseUrl"
                   placeholder="https://gitlab.example.com" />
            <div class="muted small">No trailing slash; the API path <code>/api/v4</code> is appended automatically.</div>
          </div>
        }

        <div class="form-field">
          <label>Personal access token</label>
          @if (hasToken() && !replacingToken()) {
            <div class="token-row">
              <span class="tag">••••••••</span>
              <button type="button" class="secondary small" (click)="startReplace()">Replace token</button>
            </div>
            <div class="muted small">Minimum scopes — GitHub: <code>repo</code>. GitLab: <code>api</code> + <code>write_repository</code>.</div>
          } @else {
            <input type="password" [ngModel]="tokenValue()" (ngModelChange)="tokenValue.set($event)"
                   name="token" placeholder="Paste a personal access token" autocomplete="new-password" />
            @if (hasToken()) {
              <button type="button" class="ghost small" (click)="cancelReplace()">Cancel replace</button>
            }
            <div class="muted small">Tokens are encrypted at rest and never returned by the API.</div>
          }
        </div>

        @if (error()) {
          <div class="tag error">{{ error() }}</div>
        }

        <div class="row">
          <button type="submit" [disabled]="saving() || !canSave()">
            {{ saving() ? 'Saving…' : 'Save' }}
          </button>
        </div>
      </form>

      <section class="verify-section">
        <header class="section-header">
          <h2>Verify</h2>
          <button type="button" class="secondary" (click)="verify()" [disabled]="verifying() || !hasToken() || replacingToken()">
            {{ verifying() ? 'Verifying…' : 'Verify connection' }}
          </button>
        </header>

        <div class="status">
          <span class="dot"
                [class.success]="statusOk()"
                [class.warning]="statusUnverified()"
                [class.danger]="statusFailed()"></span>
          @if (lastVerifiedAtUtc()) {
            <span>Last verified: {{ lastVerifiedAtUtc() | date:'medium' }}</span>
          } @else {
            <span class="muted">{{ hasToken() ? 'Not yet verified' : 'No token configured yet' }}</span>
          }
        </div>

        @if (verifyError()) {
          <div class="tag error">{{ verifyError() }}</div>
        }
      </section>
    }
  `,
  styles: [`
    .page-header { margin-bottom: 1.5rem; }
    .radio-row { display: flex; gap: 1rem; }
    .radio { display: inline-flex; align-items: center; gap: 0.5rem; font-weight: normal; }
    .small { font-size: 0.75rem; padding: 0.2rem 0.5rem; }
    .token-row { display: flex; align-items: center; gap: 0.5rem; }
    .row { margin-top: 1rem; }
    .verify-section { margin-top: 2rem; padding-top: 1rem; border-top: 1px solid var(--color-border); }
    .section-header { display: flex; justify-content: space-between; align-items: center; }
    .status { display: flex; align-items: center; gap: 0.5rem; margin-top: 0.75rem; }
    .dot { width: 0.75rem; height: 0.75rem; border-radius: 50%; background: var(--color-muted); display: inline-block; }
    .dot.success { background: var(--color-success, #4caf50); }
    .dot.warning { background: var(--color-warning, #f0ad4e); }
    .dot.danger { background: var(--color-danger, #d9534f); }
    .tag.error { color: var(--color-error, #d9534f); }
  `],
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
    this.api
      .set({
        mode: this.mode(),
        baseUrl: this.mode() === 'GitLab' ? this.baseUrl() : null,
        token: this.tokenValue(),
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
