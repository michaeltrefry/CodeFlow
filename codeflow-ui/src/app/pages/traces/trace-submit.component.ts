import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { map, retry, switchMap, timer } from 'rxjs';
import { WorkflowsApi } from '../../core/workflows.api';
import { TracesApi } from '../../core/traces.api';
import { WorkflowDetail, WorkflowInput, WorkflowSummary } from '../../core/models';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';
import { CardComponent } from '../../ui/card.component';

interface InputFieldState {
  key: string;
  definition: WorkflowInput;
  value: string;
  error: string | null;
}

@Component({
  selector: 'cf-trace-submit',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink,
    PageHeaderComponent, ButtonComponent, ChipComponent, CardComponent,
  ],
  template: `
    <div class="page">
    <cf-page-header title="Submit run" subtitle="Kick off a workflow run with a configured input payload.">
      <a routerLink="/traces"><button type="button" cf-button variant="ghost" icon="back">Cancel</button></a>
    </cf-page-header>

    <form (submit)="submit($event)">
      <cf-card title="Workflow selection">
        <div class="form-grid">
          <div class="field">
            <span class="field-label">Workflow</span>
            <select class="select" [ngModel]="workflowKey()" (ngModelChange)="onWorkflowChanged($event)" name="workflowKey" required>
              <option [ngValue]="null">Select workflow…</option>
              @for (wf of workflows(); track wf.key) {
                <option [value]="wf.key">{{ wf.name }} ({{ wf.key }})</option>
              }
            </select>
          </div>
          <div class="field">
            <span class="field-label">Version (blank = latest)</span>
            <input type="number" class="input mono" [(ngModel)]="workflowVersion" name="workflowVersion" min="1" />
          </div>
        </div>
      </cf-card>

      @if (workflowDetail(); as wf) {
        @if (inputFields().length > 0) {
          <cf-card title="Workflow inputs">
            <p class="muted small" style="margin-bottom: 10px">
              These values are handed to the run. The <code>input</code> field becomes the Start agent's input artifact;
              the rest are available to agent prompt templates as <code>{{'{{'}}context.&lt;key&gt;{{'}}'}}</code>
              (including nested JSON like <code>{{'{{'}}context.target.path{{'}}'}}</code>) and to Logic nodes as <code>context.&lt;key&gt;</code>.
            </p>
            @for (field of inputFields(); track field.key) {
              <div class="field" style="margin-bottom: 14px">
                <span class="field-label">
                  {{ field.definition.displayName || field.definition.key }}
                  @if (field.definition.required) { <span style="color: var(--sem-red); margin-left: 4px">*</span> }
                  <cf-chip mono style="margin-left: 6px">{{ field.definition.kind }}</cf-chip>
                </span>
                @if (field.definition.description) {
                  <span class="field-hint">{{ field.definition.description }}</span>
                }
                @if (field.definition.kind === 'Text') {
                  @if (field.definition.key === 'input') {
                    <textarea class="textarea" rows="8"
                              [ngModel]="field.value"
                              (ngModelChange)="updateInputValue(field.key, $event)"
                              [name]="'input_' + field.key"
                              placeholder="Content passed to the Start agent…"></textarea>
                  } @else {
                    <input type="text" class="input"
                           [ngModel]="field.value"
                           (ngModelChange)="updateInputValue(field.key, $event)"
                           [name]="'input_' + field.key"
                           [placeholder]="field.definition.defaultValueJson ?? ''" />
                  }
                } @else {
                  <textarea class="textarea mono" rows="5"
                            [ngModel]="field.value"
                            (ngModelChange)="updateInputValue(field.key, $event)"
                            [name]="'input_' + field.key"
                            placeholder='{"key":"value"}'></textarea>
                }
                @if (field.error) {
                  <cf-chip variant="err" dot>{{ field.error }}</cf-chip>
                }
              </div>
            }
          </cf-card>
        } @else {
          <div class="muted small">This workflow declares no inputs.</div>
        }
      }

      @if (error()) {
        <div class="trace-failure"><strong>Submit failed:</strong> {{ error() }}</div>
      }

      <div class="row" style="margin-top: 14px; justify-content: flex-end">
        <button type="submit" cf-button variant="primary" icon="play"
                [disabled]="submitting() || !workflowKey() || !workflowDetail()">
          {{ submitting() ? 'Submitting…' : 'Submit run' }}
        </button>
      </div>
    </form>
    </div>
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
  readonly workflowDetail = signal<WorkflowDetail | null>(null);
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
      .sort((a, b) => {
        // Always surface the baked-in `input` first.
        if (a.key === 'input' && b.key !== 'input') return -1;
        if (b.key === 'input' && a.key !== 'input') return 1;
        return a.ordinal - b.ordinal;
      })
      .map(definition => ({
        key: definition.key,
        definition,
        value: values[definition.key] ?? defaultValueFor(definition),
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

  submit(event: Event): void {
    event.preventDefault();
    if (!this.workflowKey()) { return; }

    const inputs: Record<string, unknown> = {};
    const errors: Record<string, string> = {};
    let startInput = '';

    for (const field of this.inputFields()) {
      const raw = field.value ?? '';
      const trimmed = raw.trim();

      if (field.definition.kind === 'Text') {
        if (!trimmed && field.definition.required && !field.definition.defaultValueJson) {
          errors[field.key] = 'Required.';
          continue;
        }
        if (trimmed) {
          inputs[field.key] = raw;
          if (field.key === 'input') {
            startInput = raw;
          }
        }
      } else {
        if (!trimmed) {
          if (field.definition.required && !field.definition.defaultValueJson) {
            errors[field.key] = 'Required.';
          }
          continue;
        }
        try {
          inputs[field.key] = JSON.parse(trimmed);
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
      input: startInput,
      inputs: Object.keys(inputs).length > 0 ? inputs : undefined
    }).pipe(
      switchMap(response =>
        this.tracesApi.get(response.traceId).pipe(
          retry({
            count: 10,
            delay: (err, attempt) =>
              err instanceof HttpErrorResponse && err.status === 404
                ? timer(500 * Math.min(attempt, 4))
                : (() => { throw err; })()
          }),
          map(() => response)
        )
      )
    ).subscribe({
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

function defaultValueFor(definition: WorkflowInput): string {
  if (!definition.defaultValueJson) return '';
  if (definition.kind === 'Text') {
    try {
      const parsed = JSON.parse(definition.defaultValueJson);
      return typeof parsed === 'string' ? parsed : definition.defaultValueJson;
    } catch {
      return definition.defaultValueJson;
    }
  }
  return definition.defaultValueJson;
}
