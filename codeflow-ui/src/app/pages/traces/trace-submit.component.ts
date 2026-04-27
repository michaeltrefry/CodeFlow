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

interface RepositoryInputRow {
  url: string;
  branch: string;
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
                @if (descriptionFor(field); as description) {
                  <span class="field-hint">{{ description }}</span>
                }
                @if (isRepositoriesField(field)) {
                  <div class="repository-editor">
                    @for (repo of repositoryRowsFor(field); track $index; let i = $index) {
                      <div class="repository-row">
                        <div class="field repository-url">
                          <span class="field-label">Repository URL</span>
                          <input type="url" class="input mono"
                                 [ngModel]="repo.url"
                                 (ngModelChange)="updateRepositoryRow(field.key, i, { url: $event })"
                                 [name]="'repo_url_' + field.key + '_' + i"
                                 placeholder="https://github.com/org/repo.git" />
                        </div>
                        <div class="field repository-branch">
                          <span class="field-label">Branch (optional)</span>
                          <input type="text" class="input mono"
                                 [ngModel]="repo.branch"
                                 (ngModelChange)="updateRepositoryRow(field.key, i, { branch: $event })"
                                 [name]="'repo_branch_' + field.key + '_' + i"
                                 placeholder="Default branch" />
                        </div>
                        <button type="button" cf-button variant="ghost" icon="trash" iconOnly
                                [disabled]="repositoryRowsFor(field).length === 1 && !repo.url && !repo.branch"
                                [attr.aria-label]="'Remove repository ' + (i + 1)"
                                title="Remove repository"
                                (click)="removeRepositoryRow(field.key, i)"></button>
                      </div>
                    }
                    <button type="button" cf-button variant="ghost" icon="plus" size="sm"
                            (click)="addRepositoryRow(field.key)">
                      Add Repository
                    </button>
                  </div>
                } @else if (field.definition.kind === 'Text') {
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
                            [placeholder]="jsonPlaceholderFor(field)"></textarea>
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
  styles: [`
    .repository-editor {
      display: flex;
      flex-direction: column;
      gap: 10px;
      align-items: flex-start;
    }

    .repository-row {
      display: grid;
      grid-template-columns: minmax(260px, 1fr) minmax(150px, 220px) 30px;
      gap: 10px;
      align-items: end;
      width: 100%;
    }

    .repository-row .btn {
      margin-bottom: 1px;
    }

    @media (max-width: 720px) {
      .repository-row {
        grid-template-columns: 1fr 30px;
      }

      .repository-url,
      .repository-branch {
        grid-column: 1;
      }

      .repository-row .btn {
        grid-column: 2;
        grid-row: 1 / span 2;
        align-self: center;
      }
    }
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

  isRepositoriesField(field: InputFieldState): boolean {
    return field.definition.key === 'repositories' && field.definition.kind === 'Json';
  }

  repositoryRowsFor(field: InputFieldState): RepositoryInputRow[] {
    return parseRepositoryRows(field.value);
  }

  addRepositoryRow(key: string): void {
    const rows = parseRepositoryRows(this.inputValueForKey(key));
    rows.push({ url: '', branch: '' });
    this.updateInputValue(key, stringifyRepositoryRows(rows, keepEmptyRows));
  }

  updateRepositoryRow(
    key: string,
    index: number,
    patch: Partial<RepositoryInputRow>
  ): void {
    const rows = parseRepositoryRows(this.inputValueForKey(key));
    const current = rows[index] ?? { url: '', branch: '' };
    rows[index] = { ...current, ...patch };
    this.updateInputValue(key, stringifyRepositoryRows(rows, keepEmptyRows));
  }

  removeRepositoryRow(key: string, index: number): void {
    const rows = parseRepositoryRows(this.inputValueForKey(key));
    rows.splice(index, 1);
    const nextRows = rows.length > 0 ? rows : [{ url: '', branch: '' }];
    this.updateInputValue(key, stringifyRepositoryRows(nextRows, keepEmptyRows));
  }

  descriptionFor(field: InputFieldState): string | null {
    if (field.definition.key === 'repositories' && field.definition.kind === 'Json') {
      return 'Add one row per repository. Leave Branch blank to use the repository default branch.';
    }

    return field.definition.description ?? null;
  }

  jsonPlaceholderFor(field: InputFieldState): string {
    if (field.definition.key === 'repositories') {
      return '[{"url":"https://github.com/org/repo.git","branch":"main"}]';
    }

    return '{"key":"value"}';
  }

  private inputValueForKey(key: string): string {
    return this.inputFields().find(field => field.key === key)?.value ?? '';
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
        if (this.isRepositoriesField(field)) {
          const rows = parseRepositoryRows(raw);
          const normalized = rows
            .map(row => ({
              url: row.url.trim(),
              branch: row.branch.trim()
            }))
            .filter(row => row.url.length > 0 || row.branch.length > 0);

          const missingUrlIndex = normalized.findIndex(row => row.url.length === 0);
          if (missingUrlIndex >= 0) {
            errors[field.key] = `Repository ${missingUrlIndex + 1} is missing a URL.`;
            continue;
          }

          if (normalized.length === 0) {
            if (field.definition.required && !field.definition.defaultValueJson) {
              errors[field.key] = 'Add at least one repository.';
            }
            continue;
          }

          inputs[field.key] = normalized.map(row => ({
            url: row.url,
            ...(row.branch.length > 0 ? { branch: row.branch } : {})
          }));
          continue;
        }

        if (!trimmed) {
          if (field.definition.required && !field.definition.defaultValueJson) {
            errors[field.key] = 'Required.';
          }
          continue;
        }
        try {
          const parsed = JSON.parse(trimmed);
          if (field.key === 'repositories') {
            const shapeError = validateRepositoriesInput(parsed);
            if (shapeError) {
              errors[field.key] = shapeError;
              continue;
            }
          }
          inputs[field.key] = parsed;
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
        this.error.set(extractSubmitError(err));
      }
    });
  }
}

const keepEmptyRows = true;

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

function validateRepositoriesInput(value: unknown): string | null {
  if (!Array.isArray(value)) {
    return 'Expected a JSON array like [{"url":"https://github.com/org/repo.git","branch":"main"}].';
  }

  for (let index = 0; index < value.length; index++) {
    const entry = value[index];
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) {
      return `Entry [${index}] must be an object with at least a url string.`;
    }

    const record = entry as Record<string, unknown>;
    if (typeof record['url'] !== 'string' || record['url'].trim().length === 0) {
      return `Entry [${index}] is missing a non-empty url string.`;
    }

    if ('branch' in record && record['branch'] !== null && typeof record['branch'] !== 'string') {
      return `Entry [${index}] branch must be a string when present.`;
    }
  }

  return null;
}

function parseRepositoryRows(value: string): RepositoryInputRow[] {
  const trimmed = value.trim();
  if (!trimmed) {
    return [{ url: '', branch: '' }];
  }

  try {
    const parsed = JSON.parse(trimmed);
    if (!Array.isArray(parsed) || parsed.length === 0) {
      return [{ url: '', branch: '' }];
    }

    return parsed.map(item => {
      if (!item || typeof item !== 'object' || Array.isArray(item)) {
        return { url: '', branch: '' };
      }

      const record = item as Record<string, unknown>;
      return {
        url: typeof record['url'] === 'string' ? record['url'] : '',
        branch: typeof record['branch'] === 'string' ? record['branch'] : ''
      };
    });
  } catch {
    return [{ url: '', branch: '' }];
  }
}

function stringifyRepositoryRows(rows: RepositoryInputRow[], includeEmptyRows = false): string {
  const payload = rows
    .map(row => ({
      url: row.url,
      branch: row.branch
    }))
    .filter(row => includeEmptyRows || row.url.trim().length > 0 || row.branch.trim().length > 0)
    .map(row => ({
      url: row.url,
      ...(row.branch.trim().length > 0 ? { branch: row.branch } : {})
    }));

  return JSON.stringify(payload);
}

function extractSubmitError(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    const validation = firstValidationError(err.error, 'inputs') ?? firstValidationError(err.error);
    if (validation) return validation;

    if (typeof err.error === 'string' && err.error.trim().length > 0) {
      return err.error;
    }

    if (err.error && typeof err.error === 'object') {
      const body = err.error as Record<string, unknown>;
      if (typeof body['detail'] === 'string' && body['detail'].trim().length > 0) {
        return body['detail'];
      }
      if (typeof body['title'] === 'string' && body['title'].trim().length > 0) {
        return body['title'];
      }
      if (typeof body['error'] === 'string' && body['error'].trim().length > 0) {
        return body['error'];
      }
    }

    return err.message || 'Failed to submit';
  }

  return 'Failed to submit';
}

function firstValidationError(body: unknown, preferredKey?: string): string | null {
  if (!body || typeof body !== 'object') {
    return null;
  }

  const errors = (body as Record<string, unknown>)['errors'];
  if (!errors || typeof errors !== 'object') {
    return null;
  }

  const record = errors as Record<string, unknown>;
  if (preferredKey) {
    const preferred = firstString(record[preferredKey]);
    if (preferred) return preferred;
  }

  for (const value of Object.values(record)) {
    const found = firstString(value);
    if (found) return found;
  }

  return null;
}

function firstString(value: unknown): string | null {
  return Array.isArray(value) && typeof value[0] === 'string'
    ? value[0]
    : null;
}
