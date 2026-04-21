import { Component, OnInit, computed, effect, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { WorkflowsApi } from '../../core/workflows.api';
import { TracesApi } from '../../core/traces.api';
import { WorkflowDetail, WorkflowInput, WorkflowSummary } from '../../core/models';

interface InputFieldState {
  key: string;
  definition: WorkflowInput;
  value: string;
  error: string | null;
}

@Component({
  selector: 'cf-trace-submit',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  template: `
    <header class="page-header">
      <h1>Submit run</h1>
      <a routerLink="/traces"><button class="secondary">Cancel</button></a>
    </header>

    <form (submit)="submit($event)">
      <div class="grid-two">
        <div class="form-field">
          <label>Workflow</label>
          <select [ngModel]="workflowKey()" (ngModelChange)="onWorkflowChanged($event)" name="workflowKey" required>
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
        <textarea [(ngModel)]="input" name="input" rows="6" placeholder="Content passed to the Start agent…" required></textarea>
      </div>

      <div class="form-field">
        <label>Input file (optional)</label>
        <input type="file" (change)="readFile($event)" />
      </div>

      <div class="form-field">
        <label>Filename (optional)</label>
        <input [(ngModel)]="fileName" name="fileName" placeholder="draft.md" />
      </div>

      @if (inputFields().length > 0) {
        <section class="card">
          <h3>Workflow inputs</h3>
          @for (field of inputFields(); track field.key) {
            <div class="form-field">
              <label>
                {{ field.definition.displayName }}
                @if (field.definition.required) { <span class="required">*</span> }
                <span class="tag small">{{ field.definition.kind }}</span>
              </label>
              @if (field.definition.description) {
                <p class="muted small">{{ field.definition.description }}</p>
              }
              @if (field.definition.kind === 'Text') {
                <input type="text" [ngModel]="field.value"
                       (ngModelChange)="updateInputValue(field.key, $event)"
                       [name]="'input_' + field.key"
                       [placeholder]="field.definition.defaultValueJson ?? ''" />
              } @else {
                <textarea rows="5" class="mono"
                          [ngModel]="field.value"
                          (ngModelChange)="updateInputValue(field.key, $event)"
                          [name]="'input_' + field.key"
                          placeholder='{"key":"value"}'></textarea>
              }
              @if (field.error) {
                <span class="tag error">{{ field.error }}</span>
              }
            </div>
          }
        </section>
      }

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
  styles: [`
    .required { color: #f85149; margin-left: 0.25rem; }
    .tag.small { margin-left: 0.5rem; }
    .muted { color: var(--color-muted); }
    .small { font-size: 0.8rem; }
    .mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
    .tag.error { background: rgba(248, 81, 73, 0.15); color: #f85149; padding: 0.25rem 0.5rem; border-radius: 3px; display: inline-block; margin-top: 0.25rem; }
  `]
})
export class TraceSubmitComponent implements OnInit {
  private readonly workflowsApi = inject(WorkflowsApi);
  private readonly tracesApi = inject(TracesApi);
  private readonly router = inject(Router);

  readonly workflowParam = input<string | undefined>(undefined, { alias: 'workflow' });

  readonly workflows = signal<WorkflowSummary[]>([]);
  readonly workflowKey = signal<string | null>(null);
  readonly workflowVersion = signal<number | null>(null);
  readonly workflowDetail = signal<WorkflowDetail | null>(null);
  readonly input = signal('');
  readonly fileName = signal('');
  readonly submitting = signal(false);
  readonly error = signal<string | null>(null);
  readonly inputValues = signal<Record<string, string>>({});
  readonly inputErrors = signal<Record<string, string>>({});

  readonly inputFields = computed<InputFieldState[]>(() => {
    const detail = this.workflowDetail();
    if (!detail) return [];
    const values = this.inputValues();
    const errors = this.inputErrors();
    return detail.inputs
      .slice()
      .sort((a, b) => a.ordinal - b.ordinal)
      .map(definition => ({
        key: definition.key,
        definition,
        value: values[definition.key] ?? definition.defaultValueJson ?? '',
        error: errors[definition.key] ?? null
      }));
  });

  ngOnInit(): void {
    this.workflowsApi.list().subscribe({
      next: wfs => {
        this.workflows.set(wfs);
        const preset = this.workflowParam();
        if (preset && wfs.some(w => w.key === preset)) {
          this.onWorkflowChanged(preset);
        }
      }
    });
  }

  onWorkflowChanged(key: string | null): void {
    this.workflowKey.set(key);
    this.workflowDetail.set(null);
    this.inputValues.set({});
    this.inputErrors.set({});
    if (!key) return;

    this.workflowsApi.getLatest(key).subscribe({
      next: detail => this.workflowDetail.set(detail),
      error: err => this.error.set(`Failed to load workflow: ${err?.message ?? err}`)
    });
  }

  updateInputValue(key: string, value: string): void {
    this.inputValues.set({ ...this.inputValues(), [key]: value });
    if (this.inputErrors()[key]) {
      const { [key]: _, ...rest } = this.inputErrors();
      this.inputErrors.set(rest);
    }
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

    const inputs: Record<string, unknown> = {};
    const errors: Record<string, string> = {};
    for (const field of this.inputFields()) {
      const raw = field.value?.trim() ?? '';
      if (!raw) {
        if (field.definition.required && !field.definition.defaultValueJson) {
          errors[field.key] = 'Required.';
        }
        continue;
      }
      if (field.definition.kind === 'Text') {
        inputs[field.key] = raw;
      } else {
        try {
          inputs[field.key] = JSON.parse(raw);
        } catch {
          errors[field.key] = 'Invalid JSON.';
        }
      }
    }

    if (Object.keys(errors).length > 0) {
      this.inputErrors.set(errors);
      return;
    }

    this.submitting.set(true);
    this.error.set(null);

    this.tracesApi.create({
      workflowKey: this.workflowKey()!,
      workflowVersion: this.workflowVersion() ?? null,
      input: this.input(),
      inputFileName: this.fileName() || undefined,
      inputs: Object.keys(inputs).length > 0 ? inputs : undefined
    }).subscribe({
      next: response => {
        this.submitting.set(false);
        this.router.navigate(['/traces', response.traceId]);
      },
      error: err => {
        this.submitting.set(false);
        this.error.set(err?.error?.errors?.inputs?.[0] ?? err?.message ?? 'Failed to submit');
      }
    });
  }
}
