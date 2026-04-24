import { CommonModule, DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import {
  DeadLetterListResponse,
  DeadLetterMessage,
  DeadLetterRetryResponse,
  OpsApi
} from '../../core/ops.api';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';
import { CardComponent } from '../../ui/card.component';
import { IconComponent } from '../../ui/icon.component';

function relTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return '—';
  const diff = (Date.now() - t) / 1000;
  if (diff < 60) return `${Math.max(0, Math.round(diff))}s ago`;
  if (diff < 3600) return `${Math.round(diff / 60)}m ago`;
  if (diff < 86400) return `${Math.round(diff / 3600)}h ago`;
  return `${Math.round(diff / 86400)}d ago`;
}

@Component({
  selector: 'cf-dlq',
  standalone: true,
  imports: [
    CommonModule, DatePipe,
    PageHeaderComponent, ButtonComponent, ChipComponent, CardComponent, IconComponent,
  ],
  template: `
    <div class="page">
      <cf-page-header
        title="DLQ ops"
        subtitle="Dead-letter queues from the RabbitMQ message bus. Inspect, fix upstream, retry.">
        <button type="button" cf-button variant="ghost" icon="refresh" (click)="refresh()" [disabled]="loading()">
          {{ loading() ? 'Loading…' : 'Refresh' }}
        </button>
      </cf-page-header>

      @if (error(); as err) {
        <div class="trace-failure"><strong>Error:</strong> {{ err }}</div>
      }

      @if (response(); as data) {
        <div class="stat-grid">
          @if (data.queues.length === 0) {
            <div class="muted">No error queues reported.</div>
          }
          @for (queue of data.queues; track queue.queueName) {
            <div class="stat"
                 [attr.data-selected]="selectedQueue() === queue.queueName ? 'true' : null"
                 (click)="selectQueue(queue.queueName)">
              <div class="stat-value">{{ queue.messageCount }}</div>
              <div class="stat-label">{{ queue.queueName.replace('codeflow.', '') }}</div>
              <div class="stat-delta" [class.up]="queue.messageCount > 0" [class.down]="queue.messageCount === 0">
                <cf-icon name="alert"></cf-icon>
                {{ queue.messageCount > 0 ? queue.messageCount + ' faulted' : 'clear' }}
              </div>
            </div>
          }
        </div>

        @if (filteredMessages().length === 0) {
          <cf-card><div class="muted">No dead-lettered messages to show.</div></cf-card>
        } @else {
          <div style="display: grid; grid-template-columns: 1fr 1.2fr; gap: 16px; align-items: flex-start">
            <cf-card [title]="selectedQueueLabel()" flush>
              <ng-template #cardRight><cf-chip mono>{{ filteredMessages().length }} faulted</cf-chip></ng-template>
              <table class="table">
                <thead>
                  <tr>
                    <th>Message</th>
                    <th>Fault</th>
                    <th>Age</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  @for (message of filteredMessages(); track message.messageId) {
                    <tr [attr.data-selected]="selectedMessageId() === message.messageId ? 'true' : null"
                        (click)="selectMessage(message.messageId)"
                        [style.background]="selectedMessageId() === message.messageId ? 'var(--surface-2)' : null">
                      <td><span class="mono" style="font-size: 11px">{{ messageIdShort(message.messageId) }}</span></td>
                      <td><cf-chip variant="err" mono>{{ shortException(message.faultExceptionType) }}</cf-chip></td>
                      <td class="muted small">{{ relTime(message.firstFaultedAtUtc) }}</td>
                      <td class="actions">
                        <button type="button" cf-button size="sm" variant="ghost">Inspect</button>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </cf-card>

            @if (selectedMessage(); as active) {
              <div class="stack">
                <cf-card title="Fault">
                  <ng-template #cardRight><cf-chip variant="err" mono>{{ active.faultExceptionType }}</cf-chip></ng-template>
                  <div style="font-size: var(--fs-sm); color: var(--text-2); line-height: 1.6">
                    {{ active.faultExceptionMessage }}
                  </div>
                  <div class="sep"></div>
                  <div class="stack" style="gap: 4px; font-size: 12px">
                    <div class="kv"><span>message id</span><span class="mono small">{{ active.messageId }}</span></div>
                    <div class="kv"><span>first fault</span><span class="mono small">{{ active.firstFaultedAtUtc | date:'medium' }}</span></div>
                    <div class="kv"><span>input address</span><span class="mono small">{{ active.originalInputAddress ?? '—' }}</span></div>
                  </div>
                </cf-card>
                <cf-card title="Payload preview">
                  <ng-template #cardRight>
                    <button type="button" cf-button size="sm" variant="ghost" icon="copy" (click)="copyPayload(active)">Copy</button>
                  </ng-template>
                  <pre class="payload-view">{{ active.payloadPreview }}</pre>
                </cf-card>
                <div class="row" style="justify-content: flex-end">
                  <button type="button" cf-button variant="primary" icon="refresh"
                          [disabled]="retrying() === active.messageId"
                          (click)="retry(active)">
                    {{ retrying() === active.messageId ? 'Retrying…' : 'Retry message' }}
                  </button>
                </div>

                @if (retryResult(); as rr) {
                  <div class="trace-failure" [style.background]="rr.success ? 'var(--ok-bg)' : 'var(--err-bg)'">
                    @if (rr.success) {
                      <strong>Republished to</strong> <span class="mono">{{ rr.republishedTo }}</span>
                    } @else {
                      <strong>Retry failed.</strong> {{ rr.errorMessage ?? '' }}
                    }
                  </div>
                }
              </div>
            } @else {
              <cf-card><div class="muted">Select a message to inspect its fault and payload.</div></cf-card>
            }
          </div>
        }
      }
    </div>
  `,
})
export class DlqComponent {
  private readonly ops = inject(OpsApi);

  protected readonly loading = signal(false);
  protected readonly response = signal<DeadLetterListResponse | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly retrying = signal<string | null>(null);
  protected readonly retryResult = signal<DeadLetterRetryResponse | null>(null);
  protected readonly selectedQueue = signal<string | null>(null);
  protected readonly selectedMessageId = signal<string | null>(null);

  protected readonly filteredMessages = computed<DeadLetterMessage[]>(() => {
    const data = this.response();
    if (!data) return [];
    const q = this.selectedQueue();
    return q ? data.messages.filter(m => m.queueName === q) : data.messages;
  });

  protected readonly selectedMessage = computed<DeadLetterMessage | null>(() => {
    const id = this.selectedMessageId();
    const msgs = this.filteredMessages();
    return msgs.find(m => m.messageId === id) ?? msgs[0] ?? null;
  });

  protected readonly selectedQueueLabel = computed(() => this.selectedQueue() ?? 'All queues');

  constructor() { this.refresh(); }

  refresh(): void {
    this.loading.set(true);
    this.error.set(null);
    this.ops.listDlq().subscribe({
      next: data => {
        this.response.set(data);
        this.loading.set(false);
        if (!this.selectedQueue() && data.queues.length > 0) {
          this.selectedQueue.set(data.queues[0].queueName);
        }
        if (!this.selectedMessageId() && data.messages.length > 0) {
          this.selectedMessageId.set(data.messages[0].messageId);
        }
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
        if (result.success) { this.refresh(); }
      },
      error: err => {
        this.retryResult.set({
          success: false,
          republishedTo: null,
          errorMessage: err?.message ?? 'Retry failed.',
        });
        this.retrying.set(null);
      }
    });
  }

  selectQueue(queueName: string): void {
    this.selectedQueue.set(queueName);
    const first = this.response()?.messages.find(m => m.queueName === queueName);
    if (first) this.selectedMessageId.set(first.messageId);
  }

  selectMessage(messageId: string): void {
    this.selectedMessageId.set(messageId);
  }

  messageIdShort(messageId: string): string {
    return messageId.length > 12 ? messageId.slice(-12) : messageId;
  }

  shortException(exceptionType: string | null | undefined): string {
    return (exceptionType ?? 'Exception').replace('Exception', '');
  }

  copyPayload(message: DeadLetterMessage): void {
    navigator.clipboard?.writeText(message.payloadPreview ?? '').catch(() => undefined);
  }

  relTime = relTime;
}
