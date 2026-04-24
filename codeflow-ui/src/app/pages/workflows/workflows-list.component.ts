import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { WorkflowPackageImportAction, WorkflowPackageImportPreview, WorkflowsApi } from '../../core/workflows.api';
import { WORKFLOW_CATEGORIES, WorkflowCategory, WorkflowSummary } from '../../core/models';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent, ChipVariant } from '../../ui/chip.component';
import { TagInputComponent } from '../../ui/tag-input.component';

type SortDirection = 'asc' | 'desc';
type SortKey = 'category' | 'name' | 'key' | 'latestVersion' | 'createdAtUtc';

/**
 * Priority used when sorting by category so the default view always shows
 * Workflow rows first, then Subflows, then Loops.
 */
const CATEGORY_ORDER: Record<WorkflowCategory, number> = {
  Workflow: 0,
  Subflow: 1,
  Loop: 2
};

const CATEGORY_FILTER_ALL = 'All' as const;
type CategoryFilter = WorkflowCategory | typeof CATEGORY_FILTER_ALL;

@Component({
  selector: 'cf-workflows-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    DatePipe,
    PageHeaderComponent,
    ButtonComponent,
    ChipComponent,
    TagInputComponent
  ],
  template: `
    <div class="page">
      <cf-page-header
        title="Workflows"
        subtitle="Versioned graphs of agents, logic, and human checkpoints.">
        <input
          #packageInput
          class="file-input"
          type="file"
          accept="application/json,.json"
          (change)="previewPackageImport($event)" />
        <button type="button" cf-button [disabled]="importLoading()" (click)="packageInput.click()">Import JSON</button>
        <a routerLink="/workflows/new">
          <button type="button" cf-button variant="primary" icon="plus">New workflow</button>
        </a>
      </cf-page-header>

      @if (importError()) {
        <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ importError() }}</cf-chip></div></div>
      }

      @if (importPreview(); as preview) {
        <div class="card import-preview">
          <div class="card-body">
            <div class="import-preview-head">
              <div>
                <h2>Import preview</h2>
                <div class="muted small">
                  {{ preview.entryPoint.key }} v{{ preview.entryPoint.version }}
                </div>
              </div>
              <div class="preview-actions">
                <div class="preview-counts">
                  <cf-chip variant="ok">{{ preview.createCount }} create</cf-chip>
                  <cf-chip>{{ preview.reuseCount }} reuse</cf-chip>
                  <cf-chip [variant]="preview.conflictCount > 0 ? 'err' : 'default'">{{ preview.conflictCount }} conflict</cf-chip>
                  @if (preview.warningCount > 0) {
                    <cf-chip variant="warn">{{ preview.warningCount }} warning</cf-chip>
                  }
                </div>
                <button
                  type="button"
                  cf-button
                  variant="primary"
                  size="sm"
                  [disabled]="!preview.canApply || importApplyLoading()"
                  (click)="applyPackageImport()">
                  Apply import
                </button>
              </div>
            </div>

            @if (importSuccess()) {
              <div class="success-message">
                <span>{{ importSuccess() }}</span>
                @if (importPreview(); as successPreview) {
                  <button
                    type="button"
                    cf-button
                    size="sm"
                    variant="ghost"
                    (click)="open(successPreview.entryPoint.key)">
                    Open workflow
                  </button>
                }
              </div>
            }

            @if (!preview.canApply) {
              <div class="warning-list">
                Resolve conflicts before applying this import.
              </div>
            }

            @if (preview.warnings.length > 0) {
              <div class="warning-list">
                @for (warning of preview.warnings; track warning) {
                  <div>{{ warning }}</div>
                }
              </div>
            }

            <table class="table import-table">
              <thead>
                <tr><th>Action</th><th>Kind</th><th>Key</th><th>Version</th><th>Message</th></tr>
              </thead>
              <tbody>
                @for (item of preview.items; track item.kind + ':' + item.key + ':' + (item.version ?? '')) {
                  <tr>
                    <td><cf-chip [variant]="importActionVariant(item.action)">{{ item.action }}</cf-chip></td>
                    <td>{{ item.kind }}</td>
                    <td class="mono">{{ item.key }}</td>
                    <td class="mono muted">{{ item.version ?? '—' }}</td>
                    <td class="small muted">{{ item.message }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      }

      @if (loading()) {
        <div class="card"><div class="card-body muted">Loading workflows…</div></div>
      } @else if (error()) {
        <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ error() }}</cf-chip></div></div>
      } @else if (workflows().length === 0) {
        <div class="card"><div class="card-body muted">No workflows yet. Create one to get started.</div></div>
      } @else {
        @if (exportError()) {
          <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ exportError() }}</cf-chip></div></div>
        }

        <div class="filters-bar">
          <label class="filter">
            <span>Category</span>
            <select [ngModel]="categoryFilter()" (ngModelChange)="categoryFilter.set($event)">
              <option [ngValue]="'All'">All</option>
              @for (opt of categoryOptions; track opt) {
                <option [ngValue]="opt">{{ opt }}</option>
              }
            </select>
          </label>
          <label class="filter grow">
            <span>Filter by tags</span>
            <cf-tag-input
              [tags]="tagFilter()"
              [suggestions]="allTags()"
              [maxTags]="0"
              [showCounter]="false"
              placeholder="Type a tag and press Enter…"
              (tagsChange)="tagFilter.set($event)"></cf-tag-input>
          </label>
          @if (tagFilter().length > 0 || categoryFilter() !== 'All') {
            <button type="button" cf-button variant="ghost" size="sm" (click)="clearFilters()">Clear</button>
          }
          <div class="filter-count muted xsmall">
            Showing {{ visibleWorkflows().length }} of {{ workflows().length }}
          </div>
        </div>

        @if (visibleWorkflows().length === 0) {
          <div class="card"><div class="card-body muted">No workflows match the current filters.</div></div>
        } @else {
          <div class="card" style="overflow: hidden">
            <table class="table">
              <thead>
                <tr>
                  <th class="sortable" (click)="toggleSort('name')">
                    Name {{ sortIndicator('name') }}
                  </th>
                  <th class="sortable" (click)="toggleSort('key')">
                    Key {{ sortIndicator('key') }}
                  </th>
                  <th class="sortable" (click)="toggleSort('category')">
                    Category {{ sortIndicator('category') }}
                  </th>
                  <th>Tags</th>
                  <th class="sortable" (click)="toggleSort('latestVersion')">
                    Version {{ sortIndicator('latestVersion') }}
                  </th>
                  <th>Nodes</th>
                  <th>Edges</th>
                  <th>Inputs</th>
                  <th class="sortable" (click)="toggleSort('createdAtUtc')">
                    Created {{ sortIndicator('createdAtUtc') }}
                  </th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (wf of visibleWorkflows(); track wf.key) {
                  <tr (click)="open(wf.key)">
                    <td>{{ wf.name }}</td>
                    <td class="mono" style="font-weight: 500">{{ wf.key }}</td>
                    <td>
                      <cf-chip [variant]="categoryVariant(displayCategory(wf))">{{ displayCategory(wf) }}</cf-chip>
                    </td>
                    <td class="tags-cell">
                      @if ((wf.tags ?? []).length === 0) {
                        <span class="muted xsmall">—</span>
                      } @else {
                        <span class="tag-chip-row">
                          @for (tag of wf.tags; track tag) {
                            <span class="tag-chip-mini">{{ tag }}</span>
                          }
                        </span>
                      }
                    </td>
                    <td><cf-chip mono>v{{ wf.latestVersion }}</cf-chip></td>
                    <td class="mono muted">{{ wf.nodeCount }}</td>
                    <td class="mono muted">{{ wf.edgeCount }}</td>
                    <td class="mono muted">{{ wf.inputCount }}</td>
                    <td class="muted small">{{ wf.createdAtUtc | date:'medium' }}</td>
                    <td class="actions">
                      <button
                        type="button"
                        cf-button
                        size="sm"
                        variant="ghost"
                        (click)="downloadPackage($event, wf)">
                        Export
                      </button>
                      <a [routerLink]="['/workflows', wf.key, 'edit']" (click)="$event.stopPropagation()">
                        <button type="button" cf-button size="sm">Edit</button>
                      </a>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      }
    </div>
  `,
  styles: [`
    .filters-bar {
      display: flex;
      gap: 1rem;
      align-items: flex-end;
      flex-wrap: wrap;
      padding: 0.75rem 0;
      margin-bottom: 0.75rem;
    }
    .filter {
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
      font-size: 0.75rem;
      color: var(--muted);
      min-width: 180px;
    }
    .filter.grow { flex: 1; min-width: 260px; }
    .filter select {
      padding: 0.4rem 0.5rem;
      border-radius: 6px;
      border: 1px solid var(--border);
      background: var(--surface);
      color: inherit;
      min-width: 180px;
    }
    .filter-count { align-self: center; }
    .file-input { display: none; }
    .import-preview { margin-bottom: 1rem; }
    .import-preview-head {
      display: flex;
      justify-content: space-between;
      gap: 1rem;
      align-items: flex-start;
      margin-bottom: 0.75rem;
    }
    .import-preview h2 {
      margin: 0;
      font-size: 1rem;
      letter-spacing: 0;
    }
    .preview-actions {
      display: flex;
      gap: 0.5rem;
      flex-wrap: wrap;
      justify-content: flex-end;
      align-items: center;
    }
    .preview-counts {
      display: flex;
      gap: 0.375rem;
      flex-wrap: wrap;
      justify-content: flex-end;
    }
    .success-message {
      border: 1px solid color-mix(in oklab, var(--sem-green) 35%, var(--border));
      background: color-mix(in oklab, var(--sem-green) 10%, transparent);
      border-radius: 6px;
      padding: 0.5rem 0.75rem;
      margin-bottom: 0.75rem;
      font-size: 0.8rem;
      display: flex;
      justify-content: space-between;
      gap: 0.75rem;
      align-items: center;
      flex-wrap: wrap;
    }
    .warning-list {
      border: 1px solid color-mix(in oklab, var(--sem-amber) 40%, var(--border));
      background: var(--warn-bg);
      color: var(--text);
      border-radius: 6px;
      padding: 0.5rem 0.75rem;
      margin-bottom: 0.75rem;
      font-size: 0.8rem;
    }
    .import-table { margin-top: 0.25rem; }
    th.sortable { cursor: pointer; user-select: none; }
    th.sortable:hover { color: var(--accent); }
    .tag-chip-row { display: inline-flex; gap: 0.25rem; flex-wrap: wrap; }
    .tag-chip-mini {
      display: inline-block;
      padding: 0.1rem 0.4rem;
      background: color-mix(in oklab, var(--accent) 15%, transparent);
      color: var(--accent);
      border: 1px solid color-mix(in oklab, var(--accent) 35%, transparent);
      border-radius: 3px;
      font-size: 0.7rem;
      font-weight: 500;
    }
    .tags-cell { max-width: 240px; }
    .muted { color: var(--muted); }
    .xsmall { font-size: 0.72rem; }
    .small { font-size: 0.8rem; }
  `]
})
export class WorkflowsListComponent {
  private readonly api = inject(WorkflowsApi);
  private readonly router = inject(Router);

  readonly workflows = signal<WorkflowSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly exportError = signal<string | null>(null);
  readonly importError = signal<string | null>(null);
  readonly importLoading = signal(false);
  readonly importApplyLoading = signal(false);
  readonly importSuccess = signal<string | null>(null);
  readonly importPreview = signal<WorkflowPackageImportPreview | null>(null);
  private pendingImportPackage: unknown = null;

  readonly categoryOptions = WORKFLOW_CATEGORIES;
  readonly categoryFilter = signal<CategoryFilter>('All');
  readonly tagFilter = signal<string[]>([]);
  readonly sortKey = signal<SortKey>('category');
  readonly sortDirection = signal<SortDirection>('asc');

  /** Secondary ordering always falls back to name ASC so category-grouping stays readable. */
  readonly visibleWorkflows = computed<WorkflowSummary[]>(() => {
    const cat = this.categoryFilter();
    const tags = this.tagFilter().map(t => t.toLowerCase());

    const filtered = this.workflows().filter(wf => {
      if (cat !== 'All' && this.displayCategory(wf) !== cat) return false;
      if (tags.length > 0) {
        const wfTags = (wf.tags ?? []).map(t => t.toLowerCase());
        for (const needle of tags) {
          if (!wfTags.includes(needle)) return false;
        }
      }
      return true;
    });

    const key = this.sortKey();
    const dir = this.sortDirection() === 'asc' ? 1 : -1;

    return filtered.slice().sort((a, b) => {
      const primary = this.compareBy(a, b, key) * dir;
      if (primary !== 0) return primary;
      if (key !== 'name') return a.name.localeCompare(b.name);
      return 0;
    });
  });

  readonly allTags = computed<string[]>(() => {
    const seen = new Set<string>();
    const result: string[] = [];
    for (const wf of this.workflows()) {
      for (const tag of wf.tags ?? []) {
        const key = tag.toLowerCase();
        if (seen.has(key)) continue;
        seen.add(key);
        result.push(tag);
      }
    }
    return result.sort((a, b) => a.localeCompare(b));
  });

  constructor() {
    this.api.list().subscribe({
      next: wfs => { this.workflows.set(wfs); this.loading.set(false); },
      error: err => { this.error.set(err?.message ?? 'Failed to load'); this.loading.set(false); },
    });
  }

  open(key: string): void {
    this.router.navigate(['/workflows', key]);
  }

  downloadPackage(event: Event, workflow: WorkflowSummary): void {
    event.stopPropagation();
    this.exportError.set(null);

    this.api.downloadPackage(workflow.key, workflow.latestVersion).subscribe({
      next: response => this.saveBlob(
        response.body,
        this.fileNameFromResponse(response.headers.get('content-disposition'))
          ?? `${workflow.key}-v${workflow.latestVersion}-package.json`),
      error: err => this.exportError.set(err?.message ?? 'Failed to export workflow package.')
    });
  }

  previewPackageImport(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';

    if (!file) {
      return;
    }

    this.importError.set(null);
    this.importSuccess.set(null);
    this.importPreview.set(null);
    this.pendingImportPackage = null;
    this.importLoading.set(true);

    file.text()
      .then(text => {
        let parsed: unknown;
        try {
          parsed = JSON.parse(text);
        } catch {
          this.importError.set('Selected file is not valid JSON.');
          this.importLoading.set(false);
          return;
        }

        this.api.previewPackageImport(parsed).subscribe({
          next: preview => {
            this.importPreview.set(preview);
            this.pendingImportPackage = parsed;
            this.importLoading.set(false);
          },
          error: err => {
            this.importError.set(this.errorMessage(err, 'Failed to preview workflow package.'));
            this.importLoading.set(false);
          }
        });
      })
      .catch(() => {
        this.importError.set('Failed to read selected file.');
        this.importLoading.set(false);
      });
  }

  applyPackageImport(): void {
    if (!this.pendingImportPackage || !this.importPreview()?.canApply) {
      return;
    }

    if (!window.confirm('Apply this workflow package import?')) {
      return;
    }

    this.importError.set(null);
    this.importSuccess.set(null);
    this.importApplyLoading.set(true);

    this.api.applyPackageImport(this.pendingImportPackage).subscribe({
      next: result => {
        this.importSuccess.set(
          `Import applied: ${result.createCount} created, ${result.reuseCount} reused.`
        );
        this.categoryFilter.set('All');
        this.tagFilter.set([]);
        this.importApplyLoading.set(false);
        this.api.list().subscribe({
          next: workflows => this.workflows.set(workflows)
        });
      },
      error: err => {
        this.importError.set(this.errorMessage(err, 'Failed to apply workflow package.'));
        this.importApplyLoading.set(false);
      }
    });
  }

  importActionVariant(action: WorkflowPackageImportAction): ChipVariant {
    switch (action) {
      case 'Create': return 'ok';
      case 'Reuse': return 'default';
      case 'Conflict': return 'err';
    }
  }

  private saveBlob(blob: Blob | null, fileName: string): void {
    if (!blob) {
      return;
    }

    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
    URL.revokeObjectURL(url);
  }

  private fileNameFromResponse(disposition: string | null): string | null {
    if (!disposition) {
      return null;
    }

    const match = /filename\*?=(?:UTF-8''|")?([^";]+)/i.exec(disposition);
    return match ? decodeURIComponent(match[1]) : null;
  }

  private errorMessage(err: unknown, fallback: string): string {
    const error = err as { error?: unknown; message?: string };
    if (error?.error && typeof error.error === 'object' && 'errors' in error.error) {
      const validation = error.error as { errors?: Record<string, string[]> };
      const first = Object.values(validation.errors ?? {}).flat()[0];
      if (first) {
        return first;
      }
    }

    return error?.message ?? fallback;
  }

  clearFilters(): void {
    this.categoryFilter.set('All');
    this.tagFilter.set([]);
  }

  toggleSort(key: SortKey): void {
    if (this.sortKey() === key) {
      this.sortDirection.update(d => d === 'asc' ? 'desc' : 'asc');
    } else {
      this.sortKey.set(key);
      this.sortDirection.set('asc');
    }
  }

  sortIndicator(key: SortKey): string {
    if (this.sortKey() !== key) return '';
    return this.sortDirection() === 'asc' ? '▲' : '▼';
  }

  categoryVariant(category: WorkflowCategory): ChipVariant {
    switch (category) {
      case 'Workflow': return 'accent';
      case 'Subflow': return 'ok';
      case 'Loop': return 'warn';
      default: return 'default';
    }
  }

  /** Fallback so rows written before the category column existed still render sensibly. */
  displayCategory(wf: WorkflowSummary): WorkflowCategory {
    return wf.category ?? 'Workflow';
  }

  private compareBy(a: WorkflowSummary, b: WorkflowSummary, key: SortKey): number {
    switch (key) {
      case 'category':
        return (CATEGORY_ORDER[this.displayCategory(a)] ?? 99)
             - (CATEGORY_ORDER[this.displayCategory(b)] ?? 99);
      case 'name':
        return a.name.localeCompare(b.name);
      case 'key':
        return a.key.localeCompare(b.key);
      case 'latestVersion':
        return a.latestVersion - b.latestVersion;
      case 'createdAtUtc':
        return a.createdAtUtc.localeCompare(b.createdAtUtc);
    }
  }
}
