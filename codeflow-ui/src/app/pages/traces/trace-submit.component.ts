import { Component, inject, input, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { WorkflowsApi } from '../../core/workflows.api';
import { TracesApi } from '../../core/traces.api';
import { WorkflowSummary } from '../../core/models';

@Component({
  selector: 'cf-trace-submit',
  standalone: true,
  imports: [FormsModule, RouterLink],
  template: `
    <header class="page-header">
      <h1>Submit run</h1>
      <a routerLink="/traces"><button class="secondary">Cancel</button></a>
    </header>

    <form (submit)="submit($event)">
      <div class="grid-two">
        <div class="form-field">
          <label>Workflow</label>
          <select [(ngModel)]="workflowKey" name="workflowKey" required>
            <option [ngValue]="null">Select workflow…</option>
            @for (wf of workflows(); track wf.key) {
              <option [value]="wf.key">{{ wf.name }} ({{ wf.key }})</option>
            }
          </select>
        </div>
        <div class="form-field">
          <label>Version (blank = latest)</label>
          <input type="number" [(ngModel)]="workflowVersion" name="workflowVersion" min="1" />
        </div>
      </div>

      <div class="form-field">
        <label>Input text</label>
        <textarea [(ngModel)]="input" name="input" rows="8" placeholder="Paste the content to send to the start agent…" required></textarea>
      </div>

      <div class="form-field">
        <label>Input file (optional)</label>
        <input type="file" (change)="readFile($event)" />
      </div>

      <div class="form-field">
        <label>Filename (optional)</label>
        <input [(ngModel)]="fileName" name="fileName" placeholder="draft.md" />
      </div>

      @if (error()) {
        <div class="tag error">{{ error() }}</div>
      }

      <div class="row" style="margin-top: 1rem;">
        <button type="submit" [disabled]="submitting() || !workflowKey()">
          {{ submitting() ? 'Submitting…' : 'Submit run' }}
        </button>
      </div>
    </form>
  `,
  styles: [``]
})
export class TraceSubmitComponent implements OnInit {
  private readonly workflowsApi = inject(WorkflowsApi);
  private readonly tracesApi = inject(TracesApi);
  private readonly router = inject(Router);

  readonly workflowParam = input<string | undefined>(undefined, { alias: 'workflow' });

  readonly workflows = signal<WorkflowSummary[]>([]);
  readonly workflowKey = signal<string | null>(null);
  readonly workflowVersion = signal<number | null>(null);
  readonly input = signal('');
  readonly fileName = signal('');
  readonly submitting = signal(false);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.workflowsApi.list().subscribe({
      next: wfs => {
        this.workflows.set(wfs);
        const preset = this.workflowParam();
        if (preset && wfs.some(w => w.key === preset)) {
          this.workflowKey.set(preset);
        }
      }
    });
  }

  readFile(event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) { return; }
    this.fileName.set(file.name);
    const reader = new FileReader();
    reader.onload = () => this.input.set(reader.result?.toString() ?? '');
    reader.readAsText(file);
  }

  submit(event: Event): void {
    event.preventDefault();
    if (!this.workflowKey()) { return; }
    this.submitting.set(true);
    this.error.set(null);

    this.tracesApi.create({
      workflowKey: this.workflowKey()!,
      workflowVersion: this.workflowVersion() ?? null,
      input: this.input(),
      inputFileName: this.fileName() || undefined
    }).subscribe({
      next: response => {
        this.submitting.set(false);
        this.router.navigate(['/traces', response.traceId]);
      },
      error: err => {
        this.submitting.set(false);
        this.error.set(err?.message ?? 'Failed to submit');
      }
    });
  }
}
