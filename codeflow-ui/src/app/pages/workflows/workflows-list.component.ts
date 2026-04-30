import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { formatHttpError } from '../../core/format-error';
import { WorkflowPackageDocument, WorkflowPackageImportAction, WorkflowPackageImportPreview, WorkflowPackageReference, WorkflowsApi } from '../../core/workflows.api';
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

interface ExportPreviewWorkflow { key: string; version: number; name: string; bytes: number; }
interface ExportPreviewAgent { key: string; version: number; kind: string | null; bytes: number; }
interface ExportPreviewRole { key: string; displayName: string; bytes: number; }
interface ExportPreviewSkill { name: string; bytes: number; }
interface ExportPreviewMcpServer { key: string; displayName: string; bytes: number; }

interface ExportPreview {
  package: WorkflowPackageDocument;
  manifest: WorkflowPackageDocument['manifest'] | null;
  summary: WorkflowSummary;
  fileName: string;
  totalBytes: number;
  workflows: ExportPreviewWorkflow[];
  agents: ExportPreviewAgent[];
  roles: ExportPreviewRole[];
  skills: ExportPreviewSkill[];
  mcpServers: ExportPreviewMcpServer[];
  assignmentCount: number;
}

interface MissingExportReference {
  kind: string;
  key: string;
  version: number | null;
  referencedBy: string | null;
}

function byteLengthOfJson(value: unknown): number {
  // TextEncoder gives accurate byte length (multi-byte chars handled correctly).
  return new TextEncoder().encode(JSON.stringify(value)).byteLength;
}

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

      @if (exportLoading()) {
        <div class="card"><div class="card-body muted">Loading export preview…</div></div>
      }

      @if (exportMissingRefs(); as missing) {
        <div class="card export-preview-card">
          <div class="card-body">
            <div class="export-preview-head">
              <div>
                <h2>Export blocked</h2>
                <div class="muted small">Package is not self-contained.</div>
              </div>
              <button type="button" cf-button size="sm" variant="ghost" (click)="cancelExportPreview()">Close</button>
            </div>
            <p class="muted small">
              The dependency tree references entities not resolvable in the current database. Resolve every
              missing reference below before re-exporting. Bumping the affected agents/subflows usually fixes this.
            </p>
            <table class="table">
              <thead>
                <tr><th>Kind</th><th>Key</th><th>Version</th><th>Referenced by</th></tr>
              </thead>
              <tbody>
                @for (ref of missing; track ref.kind + ':' + ref.key + ':' + (ref.version ?? '')) {
                  <tr>
                    <td><cf-chip variant="err">{{ ref.kind }}</cf-chip></td>
                    <td class="mono">{{ ref.key }}</td>
                    <td class="mono muted">{{ ref.version ?? '—' }}</td>
                    <td class="muted small">{{ ref.referencedBy ?? '—' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      } @else if (exportPreview(); as preview) {
        <div class="card export-preview-card">
          <div class="card-body">
            <div class="export-preview-head">
              <div>
                <h2>Export preview</h2>
                <div class="muted small">
                  {{ preview.summary.name }} —
                  <code class="mono">{{ preview.summary.key }}</code> v{{ preview.summary.latestVersion }}
                </div>
              </div>
              <div class="preview-actions">
                <div class="preview-counts">
                  <cf-chip variant="ok">Self-contained</cf-chip>
                  <cf-chip>{{ formatBytes(preview.totalBytes) }} total</cf-chip>
                  <cf-chip>{{ preview.workflows.length }} workflows</cf-chip>
                  <cf-chip>{{ preview.agents.length }} agents</cf-chip>
                  <cf-chip>{{ preview.roles.length }} roles</cf-chip>
                </div>
                <button type="button" cf-button size="sm" variant="ghost" (click)="cancelExportPreview()">Cancel</button>
                <button type="button" cf-button variant="primary" size="sm" (click)="saveExportPackage()">Download package</button>
              </div>
            </div>

            <p class="muted xsmall export-preview-help">
              Sizes are estimated from the JSON encoding of each entity (UTF-8 byte length); the package on disk is the same JSON
              with whitespace per server defaults. The V8 self-containment check passed — every transitive reference is included
              at the version pinned in the entry point.
            </p>

            <details class="export-section" open>
              <summary>Workflows ({{ preview.workflows.length }})</summary>
              <table class="table export-table">
                <thead>
                  <tr><th>Key</th><th>Version</th><th>Name</th><th class="num">Size</th></tr>
                </thead>
                <tbody>
                  @for (w of preview.workflows; track w.key + ':' + w.version) {
                    <tr>
                      <td class="mono">{{ w.key }}</td>
                      <td class="mono muted">v{{ w.version }}</td>
                      <td>{{ w.name }}</td>
                      <td class="mono muted num">{{ formatBytes(w.bytes) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </details>

            <details class="export-section">
              <summary>Agents ({{ preview.agents.length }})</summary>
              <table class="table export-table">
                <thead>
                  <tr><th>Key</th><th>Version</th><th>Kind</th><th class="num">Size</th></tr>
                </thead>
                <tbody>
                  @for (a of preview.agents; track a.key + ':' + a.version) {
                    <tr>
                      <td class="mono">{{ a.key }}</td>
                      <td class="mono muted">v{{ a.version }}</td>
                      <td>{{ a.kind ?? '—' }}</td>
                      <td class="mono muted num">{{ formatBytes(a.bytes) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </details>

            @if (preview.roles.length > 0) {
              <details class="export-section">
                <summary>Roles ({{ preview.roles.length }}) <span class="muted xsmall">— {{ preview.assignmentCount }} agent assignments</span></summary>
                <table class="table export-table">
                  <thead>
                    <tr><th>Key</th><th>Display name</th><th class="num">Size</th></tr>
                  </thead>
                  <tbody>
                    @for (r of preview.roles; track r.key) {
                      <tr>
                        <td class="mono">{{ r.key }}</td>
                        <td>{{ r.displayName }}</td>
                        <td class="mono muted num">{{ formatBytes(r.bytes) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </details>
            }

            @if (preview.skills.length > 0) {
              <details class="export-section">
                <summary>Skills ({{ preview.skills.length }})</summary>
                <table class="table export-table">
                  <thead>
                    <tr><th>Name</th><th class="num">Size</th></tr>
                  </thead>
                  <tbody>
                    @for (s of preview.skills; track s.name) {
                      <tr>
                        <td class="mono">{{ s.name }}</td>
                        <td class="mono muted num">{{ formatBytes(s.bytes) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </details>
            }

            @if (preview.mcpServers.length > 0) {
              <details class="export-section">
                <summary>MCP servers ({{ preview.mcpServers.length }})</summary>
                <table class="table export-table">
                  <thead>
                    <tr><th>Key</th><th>Display name</th><th class="num">Size</th></tr>
                  </thead>
                  <tbody>
                    @for (m of preview.mcpServers; track m.key) {
                      <tr>
                        <td class="mono">{{ m.key }}</td>
                        <td>{{ m.displayName }}</td>
                        <td class="mono muted num">{{ formatBytes(m.bytes) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </details>
            }
          </div>
        </div>
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
    .export-preview-card { margin-bottom: 1rem; }
    .export-preview-head {
      display: flex;
      justify-content: space-between;
      gap: 1rem;
      align-items: flex-start;
      margin-bottom: 0.75rem;
    }
    .export-preview-card h2 {
      margin: 0;
      font-size: 1rem;
      letter-spacing: 0;
    }
    .export-preview-help { margin: 0 0 0.5rem 0; }
    .export-section {
      margin-top: 0.5rem;
      border: 1px solid var(--border);
      border-radius: 6px;
      padding: 0.4rem 0.5rem;
      background: color-mix(in oklab, var(--surface) 95%, transparent);
    }
    .export-section > summary {
      cursor: pointer;
      user-select: none;
      font-size: 0.85rem;
      padding: 0.15rem 0.1rem;
    }
    .export-section[open] > summary { margin-bottom: 0.25rem; }
    .export-table { margin-top: 0.25rem; }
    .export-table .num { text-align: right; }
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

  /** E5 / R5.5: parsed package + size estimates for the export-preview card.
   *  Computed once when the author hits Export; the cached package + bytes feed both
   *  the dependency-tree view and the eventual Download click (no second network call). */
  readonly exportPreview = signal<ExportPreview | null>(null);
  readonly exportLoading = signal(false);
  readonly exportMissingRefs = signal<MissingExportReference[] | null>(null);

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

  /** E5: open the export-preview dialog. The dependency tree is pulled from the V8 manifest
   *  on the package; total + per-entity sizes are computed locally from the JSON. */
  downloadPackage(event: Event, workflow: WorkflowSummary): void {
    event.stopPropagation();
    this.exportError.set(null);
    this.exportMissingRefs.set(null);
    this.exportPreview.set(null);
    this.exportLoading.set(true);

    this.api.getPackage(workflow.key, workflow.latestVersion).subscribe({
      next: response => {
        const pkg = response.body!;
        this.exportLoading.set(false);
        this.exportPreview.set(this.buildExportPreview(workflow, pkg, response.headers.get('content-disposition')));
      },
      error: err => {
        this.exportLoading.set(false);
        const missing = this.extractMissingRefs(err);
        if (missing) {
          this.exportMissingRefs.set(missing);
        }
        this.exportError.set(this.errorMessage(err, 'Failed to export workflow package.'));
      }
    });
  }

  /** E5: download the package the author already previewed. We re-stringify the parsed
   *  JSON rather than re-fetching — bytes are already in memory and match what was shown. */
  saveExportPackage(): void {
    const preview = this.exportPreview();
    if (!preview) return;
    const blob = new Blob([JSON.stringify(preview.package)], { type: 'application/json' });
    this.saveBlob(blob, preview.fileName);
  }

  cancelExportPreview(): void {
    this.exportPreview.set(null);
    this.exportMissingRefs.set(null);
    this.exportError.set(null);
  }

  private buildExportPreview(
    summary: WorkflowSummary,
    pkg: WorkflowPackageDocument,
    contentDisposition: string | null
  ): ExportPreview {
    const totalBytes = byteLengthOfJson(pkg);
    // Per-entity byte estimates by stringifying the typed collection elements. Cheap on
    // typical packages (tens of entities); recompute happens once per Export click.
    const workflowBytes = (pkg.workflows ?? []).map(w => byteLengthOfJson(w));
    const agentBytes = (pkg.agents ?? []).map(a => byteLengthOfJson(a));
    const roleBytes = (pkg.roles ?? []).map(r => byteLengthOfJson(r));
    const skillBytes = (pkg.skills ?? []).map(s => byteLengthOfJson(s));
    const mcpBytes = (pkg.mcpServers ?? []).map(m => byteLengthOfJson(m));

    const manifest = pkg.manifest ?? null;
    return {
      package: pkg,
      manifest,
      summary,
      fileName: this.fileNameFromResponse(contentDisposition)
        ?? `${summary.key}-v${summary.latestVersion}-package.json`,
      totalBytes,
      workflows: (pkg.workflows ?? []).map((w, i) => ({
        key: w.key, version: w.version, name: w.name, bytes: workflowBytes[i] ?? 0
      })),
      agents: (pkg.agents ?? []).map((a, i) => ({
        key: a.key, version: a.version, kind: a.kind ?? null, bytes: agentBytes[i] ?? 0
      })),
      roles: (pkg.roles ?? []).map((r, i) => ({
        key: r.key, displayName: r.displayName, bytes: roleBytes[i] ?? 0
      })),
      skills: (pkg.skills ?? []).map((s, i) => ({
        name: s.name, bytes: skillBytes[i] ?? 0
      })),
      mcpServers: (pkg.mcpServers ?? []).map((m, i) => ({
        key: m.key, displayName: m.displayName, bytes: mcpBytes[i] ?? 0
      })),
      assignmentCount: (pkg.agentRoleAssignments ?? []).length
    };
  }

  /** Pull V8's missing-refs payload out of a 422 ProblemDetails response (extensions). */
  private extractMissingRefs(err: unknown): MissingExportReference[] | null {
    const body = (err as { error?: unknown })?.error;
    if (!body || typeof body !== 'object') return null;
    const extensions = (body as { extensions?: { missingReferences?: unknown } }).extensions;
    const refs = extensions?.missingReferences;
    if (!Array.isArray(refs) || refs.length === 0) return null;
    return refs.map(r => ({
      kind: typeof r?.kind === 'string' ? r.kind : 'Unknown',
      key: typeof r?.key === 'string' ? r.key : '(unknown)',
      version: typeof r?.version === 'number' ? r.version : null,
      referencedBy: typeof r?.referencedBy === 'string' ? r.referencedBy : null
    }));
  }

  formatBytes(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
    return `${(bytes / 1024 / 1024).toFixed(2)} MiB`;
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
    return formatHttpError(err, fallback);
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
