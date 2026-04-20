import { Component, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TracesApi } from '../../core/traces.api';
import { AgentDecisionKind, HitlTask } from '../../core/models';

@Component({
  selector: 'cf-hitl-review',
  standalone: true,
  imports: [FormsModule],
  template: `
    <article class="hitl-card">
      <header class="row" style="justify-content: space-between;">
        <div>
          <strong>{{ task().agentKey }}</strong>
          <span class="muted small"> v{{ task().agentVersion }}</span>
        </div>
        <span class="tag warn">Pending</span>
      </header>

      @if (task().inputPreview) {
        <pre class="monospace preview">{{ task().inputPreview }}</pre>
      } @else {
        <p class="muted small">(no preview — see artifact {{ task().inputRef }})</p>
      }

      <div class="form-field">
        <label>Decision</label>
        <select [(ngModel)]="decision" name="decision-{{ task().id }}">
          <option value="Approved">Approve</option>
          <option value="ApprovedWithActions">Approve with actions</option>
          <option value="Rejected">Reject</option>
          <option value="Completed">Mark completed</option>
          <option value="Failed">Mark failed</option>
        </select>
      </div>

      @if (decision() === 'Rejected') {
        <div class="form-field">
          <label>Reasons (one per line)</label>
          <textarea [(ngModel)]="reasonText" name="reasons-{{ task().id }}" rows="3"></textarea>
        </div>
      }

      @if (decision() === 'ApprovedWithActions') {
        <div class="form-field">
          <label>Actions (one per line)</label>
          <textarea [(ngModel)]="reasonText" name="actions-{{ task().id }}" rows="3"></textarea>
        </div>
      }

      <div class="form-field">
        <label>Output text (optional)</label>
        <textarea [(ngModel)]="outputText" name="output-{{ task().id }}" rows="4" placeholder="Explain the decision or paste updated content…"></textarea>
      </div>

      @if (error()) {
        <div class="tag error">{{ error() }}</div>
      }

      <div class="row">
        <button (click)="submit()" [disabled]="submitting()">
          {{ submitting() ? 'Submitting…' : 'Submit decision' }}
        </button>
      </div>
    </article>
  `,
  styles: [`
    .hitl-card {
      border: 1px solid var(--color-border);
      border-radius: 6px;
      padding: 1rem;
      margin-bottom: 1rem;
    }
    .preview {
      white-space: pre-wrap;
      background: var(--color-surface-alt);
      padding: 0.75rem;
      border-radius: 4px;
      max-height: 240px;
      overflow: auto;
    }
    .muted { color: var(--color-muted); }
    .small { font-size: 0.8rem; }
  `]
})
export class HitlReviewComponent {
  private readonly api = inject(TracesApi);

  readonly task = input.required<HitlTask>();
  readonly decided = output<void>();

  readonly decision = signal<AgentDecisionKind>('Approved');
  readonly reasonText = signal('');
  readonly outputText = signal('');
  readonly submitting = signal(false);
  readonly error = signal<string | null>(null);

  submit(): void {
    this.submitting.set(true);
    this.error.set(null);

    const lines = this.reasonText()
      .split('\n')
      .map(line => line.trim())
      .filter(line => line.length > 0);

    this.api.submitHitlDecision(this.task().traceId, {
      decision: this.decision(),
      reasons: this.decision() === 'Rejected' ? lines : undefined,
      actions: this.decision() === 'ApprovedWithActions' ? lines : undefined,
      outputText: this.outputText() || undefined
    }).subscribe({
      next: () => {
        this.submitting.set(false);
        this.decided.emit();
      },
      error: err => {
        this.submitting.set(false);
        this.error.set(err?.message ?? 'Failed to submit');
      }
    });
  }
}
