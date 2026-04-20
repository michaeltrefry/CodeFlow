import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import {
  DeadLetterListResponse,
  DeadLetterMessage,
  DeadLetterRetryResponse,
  OpsApi
} from '../../core/ops.api';

@Component({
  selector: 'cf-dlq',
  standalone: true,
  imports: [CommonModule],
  template: `
    <section class="page">
      <header class="page-header">
        <div>
          <h1>Dead Letter Queue</h1>
          <p class="muted">Inspect and retry messages that exceeded the consumer retry budget.</p>
        </div>
        <button type="button" class="button" (click)="refresh()" [disabled]="loading()">
          {{ loading() ? 'Loading…' : 'Refresh' }}
        </button>
      </header>

      @if (error(); as err) {
        <div class="banner error">{{ err }}</div>
      }

      @if (response(); as data) {
        <section class="stats">
          @for (queue of data.queues; track queue.queueName) {
            <div class="stat">
              <div class="stat-value">{{ queue.messageCount }}</div>
              <div class="stat-label">{{ queue.queueName }}</div>
            </div>
          }
          @if (data.queues.length === 0) {
            <div class="muted">No error queues reported.</div>
          }
        </section>

        <section class="messages">
          <h2>Messages</h2>
          @if (data.messages.length === 0) {
            <div class="muted">No dead-lettered messages to show.</div>
          } @else {
            <table class="table">
              <thead>
                <tr>
                  <th>Queue</th>
                  <th>Faulted at</th>
                  <th>Exception</th>
                  <th>Original input</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                @for (message of data.messages; track message.messageId) {
                  <tr>
                    <td>{{ message.queueName }}</td>
                    <td>{{ message.firstFaultedAtUtc | date:'medium' }}</td>
                    <td>
                      <div class="mono muted small">{{ message.faultExceptionType }}</div>
                      <div>{{ message.faultExceptionMessage }}</div>
                    </td>
                    <td class="mono small">{{ message.originalInputAddress ?? '—' }}</td>
                    <td class="actions">
                      <button type="button" class="button secondary" (click)="inspect(message)">Inspect</button>
                      <button type="button" class="button" [disabled]="retrying() === message.messageId"
                        (click)="retry(message)">
                        {{ retrying() === message.messageId ? 'Retrying…' : 'Retry' }}
                      </button>
                    </td>
                  </tr>
                  @if (inspecting() === message.messageId) {
                    <tr>
                      <td colspan="5">
                        <pre class="payload">{{ message.payloadPreview }}</pre>
                      </td>
                    </tr>
                  }
                }
              </tbody>
            </table>
          }
        </section>
      }

      @if (retryResult(); as rr) {
        <div class="banner" [class.error]="!rr.success" [class.success]="rr.success">
          @if (rr.success) {
            Republished to <strong>{{ rr.republishedTo }}</strong>.
          } @else {
            {{ rr.errorMessage ?? 'Retry failed.' }}
          }
        </div>
      }
    </section>
  `,
  styles: [`
    .page { display: flex; flex-direction: column; gap: 1.25rem; }
    .page-header { display: flex; align-items: center; justify-content: space-between; }
    .stats { display: flex; gap: 1rem; flex-wrap: wrap; }
    .stat {
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: 8px;
      padding: 0.75rem 1rem;
      min-width: 12rem;
    }
    .stat-value { font-size: 1.4rem; font-weight: 700; color: var(--color-accent); }
    .stat-label { font-size: 0.85rem; color: var(--color-muted); font-family: monospace; }
    .mono { font-family: monospace; }
    .small { font-size: 0.85rem; }
    .muted { color: var(--color-muted); }
    .actions { display: flex; gap: 0.35rem; }
    .payload {
      background: var(--color-surface-alt);
      padding: 0.75rem;
      border-radius: 4px;
      max-height: 280px;
      overflow: auto;
      margin: 0;
    }
    .banner {
      padding: 0.75rem 1rem;
      border-radius: 6px;
    }
    .banner.error { background: rgba(248,113,113,0.12); color: #fecaca; }
    .banner.success { background: rgba(56,189,248,0.12); color: var(--color-accent); }
  `]
})
export class DlqComponent {
  private readonly ops = inject(OpsApi);

  protected readonly loading = signal(false);
  protected readonly response = signal<DeadLetterListResponse | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly retrying = signal<string | null>(null);
  protected readonly retryResult = signal<DeadLetterRetryResponse | null>(null);
  protected readonly inspecting = signal<string | null>(null);

  constructor() {
    this.refresh();
  }

  refresh(): void {
    this.loading.set(true);
    this.error.set(null);
    this.ops.listDlq().subscribe({
      next: data => {
        this.response.set(data);
        this.loading.set(false);
      },
      error: err => {
        this.error.set(err?.message ?? 'Failed to load DLQ data.');
        this.loading.set(false);
      }
    });
  }

  retry(message: DeadLetterMessage): void {
    this.retrying.set(message.messageId);
    this.retryResult.set(null);
    this.ops.retry(message.queueName, message.messageId).subscribe({
      next: result => {
        this.retryResult.set(result);
        this.retrying.set(null);
        if (result.success) {
          this.refresh();
        }
      },
      error: err => {
        this.retryResult.set({
          success: false,
          republishedTo: null,
          errorMessage: err?.message ?? 'Retry failed.'
        });
        this.retrying.set(null);
      }
    });
  }

  inspect(message: DeadLetterMessage): void {
    this.inspecting.set(this.inspecting() === message.messageId ? null : message.messageId);
  }
}
