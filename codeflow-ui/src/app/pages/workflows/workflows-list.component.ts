import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { useAsyncList } from '../../core/async-state';
import { formatHttpError } from '../../core/format-error';
import {
  IMPORT_HANDOFF_MAX_AGE_MS,
  IMPORT_HANDOFF_STORAGE_KEY,
  ImportHandoff,
} from '../../core/import-handoff';
import {
  WorkflowPackageDocument,
  WorkflowPackageImportAction,
  WorkflowPackageImportDriftConflict,
  WorkflowPackageImportItem,
  WorkflowPackageImportPreview,
  WorkflowPackageImportResolution,
  WorkflowPackageImportResolutionMode,
  WorkflowPackageReference,
  WorkflowsApi,
} from '../../core/workflows.api';
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

/** sc-396: per-row resolution choice the user has made on the imports page. The signal is
 *  keyed by `${kind}:${key}:${sourceVersion ?? ''}` so a single Map covers Agent, Workflow,
 *  Skill, Role, McpServer, and AgentRoleAssignment rows; the Bump/Copy modes only ever apply
 *  to the versioned kinds. `expectedExistingMaxVersion` is captured from the row's
 *  `existingMaxVersion` at the time the user picked the resolution and rides through to the
 *  apply request so the server's drift gate can fire on a stale choice. */
interface RowResolutionChoice {
  mode: WorkflowPackageImportResolutionMode;
  newKey?: string;
  expectedExistingMaxVersion?: number | null;
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
        @if (selectedCount() > 0) {
          <button type="button" cf-button variant="ghost" icon="trash" (click)="retireSelected()" [disabled]="retiring()">
            {{ retiring() ? 'Retiring…' : 'Retire selected' }}
          </button>
        }
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
                  @if (preview.refusedCount > 0) {
                    <cf-chip variant="err">{{ preview.refusedCount }} refused</cf-chip>
                  }
                  @if (preview.warningCount > 0) {
                    <cf-chip variant="warn">{{ preview.warningCount }} warning</cf-chip>
                  }
                </div>
                @if (resolutions().size > 0) {
                  <button
                    type="button"
                    cf-button
                    size="sm"
                    variant="ghost"
                    (click)="clearResolutions()">
                    Reset resolutions
                  </button>
                }
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

            @if (driftConflict(); as drift) {
              <div class="drift-banner">
                <div class="drift-banner-head">
                  <strong>Library moved between preview and apply</strong>
                  <button
                    type="button"
                    cf-button
                    size="sm"
                    variant="primary"
                    [disabled]="importApplyLoading()"
                    (click)="applyAcknowledgingDrift()">
                    Apply anyway
                  </button>
                </div>
                <ul>
                  @for (entry of drift.movedEntities; track entry.kind + ':' + entry.key + ':' + (entry.sourceVersion ?? '')) {
                    <li class="small">
                      {{ entry.kind }} <span class="mono">{{ entry.key }}</span>
                      moved from v{{ entry.expectedExistingMaxVersion ?? '—' }}
                      to v{{ entry.currentExistingMaxVersion ?? 'gone' }}
                    </li>
                  }
                </ul>
                <div class="muted small">{{ drift.error }}</div>
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
                <tr>
                  <th>Action</th>
                  <th>Kind</th>
                  <th>Key</th>
                  <th>Version</th>
                  <th>Message</th>
                  <th>Resolution</th>
                </tr>
              </thead>
              <tbody>
                @for (item of preview.items; track item.kind + ':' + item.key + ':' + (item.version ?? '')) {
                  <tr>
                    <td><cf-chip [variant]="importActionVariant(item.action)">{{ item.action }}</cf-chip></td>
                    <td>{{ item.kind }}</td>
                    <td class="mono">{{ item.key }}</td>
                    <td class="mono muted">{{ item.version ?? '—' }}</td>
                    <td class="small muted">{{ item.message }}</td>
                    <td>
                      @if (rowIsResolvable(item)) {
                        <select
                          class="resolution-select"
                          [value]="resolutionFor(item)?.mode ?? ''"
                          (change)="onResolutionChange(item, $any($event.target).value)">
                          <option value="">No resolution</option>
                          @for (opt of resolutionOptions(item); track opt.id) {
                            <option [value]="opt.id" [disabled]="opt.disabled">{{ opt.label }}</option>
                          }
                        </select>
                      } @else {
                        @if (resolutionFor(item); as resolved) {
                          <span class="resolved-pill small muted">Resolved: {{ resolved.mode }}</span>
                        }
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      }

      @if (useExistingPrompt(); as prompt) {
        <div class="modal-backdrop" (click)="cancelUseExistingPrompt()">
          <div class="modal" (click)="$event.stopPropagation()">
            <h3>Use existing library version?</h3>
            <p class="small">
              Switching to library v{{ prompt.libraryVersion }} rewrites every workflow node
              that pinned this entity. The library version may behave differently than the
              version the workflow was authored against. Continue?
            </p>
            <div class="modal-actions">
              <button type="button" cf-button size="sm" variant="ghost" (click)="cancelUseExistingPrompt()">Cancel</button>
              <button type="button" cf-button size="sm" variant="primary" (click)="confirmUseExistingPrompt()">Use existing</button>
            </div>
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
        @if (retireError()) {
          <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ retireError() }}</cf-chip></div></div>
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
                  <th style="width: 42px">
                    <input type="checkbox" [checked]="allVisibleSelected()" (change)="toggleAll($event)" />
                  </th>
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
                    <td (click)="$event.stopPropagation()">
                      <input type="checkbox" [checked]="isSelected(wf.key)" (change)="toggleSelected(wf.key, $event)" />
                    </td>
                    <td>{{ wf.name }}</td>
                    <td class="mono" style="font-weight: 500">{{ wf.key }}</td>
                    <td>
                      <cf-chip [variant]="categoryVariant(displayCategory(wf))">{{ displayCategory(wf) }}</cf-chip>
                    </td>
                    <td class="tags-cell">
                      @if (wf.tags.length === 0) {
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
    .resolution-select {
      font-size: 0.75rem;
      padding: 0.15rem 0.35rem;
      max-width: 220px;
    }
    .resolved-pill {
      font-style: italic;
    }
    /* sc-396: drift-409 banner — rendered above the items table when /package/apply returned
       409 because the library moved past the resolution's expectedExistingMaxVersion. */
    .drift-banner {
      border: 1px solid color-mix(in oklab, var(--sem-amber) 50%, var(--border));
      background: var(--warn-bg);
      border-radius: 6px;
      padding: 0.5rem 0.75rem;
      margin-bottom: 0.75rem;
      font-size: 0.8rem;
      display: flex;
      flex-direction: column;
      gap: 0.4rem;
    }
    .drift-banner-head {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 0.75rem;
    }
    .drift-banner ul {
      list-style: disc;
      padding-left: 1.25rem;
      margin: 0;
    }
    /* sc-396: UseExisting confirmation modal. The first time the user picks UseExisting in a
       session we surface this prompt; once acknowledged, subsequent picks skip the modal. */
    .modal-backdrop {
      position: fixed;
      inset: 0;
      background: color-mix(in oklab, black 50%, transparent);
      display: flex;
      align-items: center;
      justify-content: center;
      z-index: 100;
    }
    .modal {
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 1rem 1.25rem;
      max-width: 460px;
      width: 100%;
      box-shadow: 0 12px 40px rgba(0, 0, 0, 0.4);
    }
    .modal h3 { margin: 0 0 0.5rem 0; font-size: 1rem; }
    .modal p { margin: 0 0 0.75rem 0; }
    .modal-actions {
      display: flex;
      justify-content: flex-end;
      gap: 0.5rem;
    }
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
  private readonly workflowsList = useAsyncList(
    () => this.api.list(),
    { errorMessage: 'Failed to load' },
  );

  readonly workflows = this.workflowsList.items;
  readonly loading = this.workflowsList.loading;
  readonly error = this.workflowsList.error;
  readonly exportError = signal<string | null>(null);
  readonly importError = signal<string | null>(null);
  readonly importLoading = signal(false);
  readonly importApplyLoading = signal(false);
  readonly retiring = signal(false);
  readonly retireError = signal<string | null>(null);
  readonly importSuccess = signal<string | null>(null);
  readonly importPreview = signal<WorkflowPackageImportPreview | null>(null);
  private pendingImportPackage: unknown = null;

  /** sc-396: per-row resolution state. Keyed by `rowKey(item)`. Persists across re-previews
   *  so the user's choice survives even after the underlying Conflict row collapses to a
   *  Create / Reuse row in the next preview cycle. Cleared by `clearResolutions()`. */
  readonly resolutions = signal<Map<string, RowResolutionChoice>>(new Map());

  /** sc-396: shortHash suffix per Conflict/Refused row, precomputed when the preview lands
   *  so the Copy dropdown option can show `{key}-{6-hex-shortHash}` without a click-time
   *  round-trip to crypto.subtle. */
  readonly precomputedCopySuffixes = signal<Map<string, string>>(new Map());

  /** sc-396: pending UseExisting choice awaiting confirmation; null when no modal showing.
   *  Once the user OKs the modal once, `useExistingWarned` flips and subsequent UseExisting
   *  picks skip the prompt — the warning is informational, not a per-row safety gate. */
  readonly useExistingPrompt = signal<{ rowKey: string; choice: RowResolutionChoice; libraryVersion: number } | null>(null);
  readonly useExistingWarned = signal(false);

  /** sc-396: 409 from /package/apply when the live library moved beyond the expectedExistingMaxVersion
   *  we sent. Banner renders the moved entries; `applyAcknowledgingDrift()` retries with the flag set. */
  readonly driftConflict = signal<WorkflowPackageImportDriftConflict | null>(null);

  private rePreviewTimer: ReturnType<typeof setTimeout> | null = null;

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
  readonly selectedKeys = signal<Set<string>>(new Set());
  readonly selectedCount = computed(() => this.selectedKeys().size);
  readonly allVisibleSelected = computed(() => {
    const visible = this.visibleWorkflows();
    return visible.length > 0 && visible.every(wf => this.selectedKeys().has(wf.key));
  });

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
    this.reload();
    // sc-397: chat chip's "Resolve in imports page" handoff. Read + clear the stash on init
    // (the read happens once per page load — clearResolutions / new file uploads don't
    // re-trigger). On a stale stash we discard it and let the user upload normally.
    this.consumeImportHandoff();
  }

  reload(): void {
    this.selectedKeys.set(new Set());
    this.workflowsList.reload();
  }

  open(key: string): void {
    this.router.navigate(['/workflows', key]);
  }

  isSelected(key: string): boolean {
    return this.selectedKeys().has(key);
  }

  toggleSelected(key: string, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    const next = new Set(this.selectedKeys());
    checked ? next.add(key) : next.delete(key);
    this.selectedKeys.set(next);
  }

  toggleAll(event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.selectedKeys.set(checked ? new Set(this.visibleWorkflows().map(wf => wf.key)) : new Set());
  }

  retireSelected(): void {
    const keys = [...this.selectedKeys()];
    if (keys.length === 0 || this.retiring()) return;
    this.retiring.set(true);
    this.retireError.set(null);
    this.api.retireMany(keys).subscribe({
      next: () => {
        this.retiring.set(false);
        this.reload();
      },
      error: err => {
        this.retiring.set(false);
        this.retireError.set(formatHttpError(err, 'Failed to retire selected workflows.'));
      }
    });
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

  /**
   * sc-397: read + clear a chat-chip "Resolve in imports page" handoff from sessionStorage
   * and seed the imports-preview state from it. Two flavors:
   *   - inline: the chip stashed the package bytes alongside the conversationId; we just
   *     hydrate `pendingImportPackage` and run preview.
   *   - draft: the chip stashed only the conversationId; we GET the live draft from
   *     /api/workflows/package-draft, then run preview.
   * Stale stashes (older than IMPORT_HANDOFF_MAX_AGE_MS) are discarded silently — the user
   * navigated away from the chip and came back later; better to render a clean page than
   * import bytes from an old session.
   */
  private consumeImportHandoff(): void {
    let raw: string | null;
    try {
      raw = sessionStorage.getItem(IMPORT_HANDOFF_STORAGE_KEY);
    } catch {
      // sessionStorage can throw under privacy mode / quota exceeded; nothing to consume.
      return;
    }
    if (!raw) return;

    try {
      sessionStorage.removeItem(IMPORT_HANDOFF_STORAGE_KEY);
    } catch {
      // Failure to clear is non-fatal — proceed with hydration; worst case is the next page
      // load re-hydrates with the same stash and the user sees the same preview again.
    }

    let handoff: ImportHandoff | null;
    try {
      const parsed = JSON.parse(raw);
      if (!parsed || typeof parsed !== 'object' || parsed.v !== 1) return;
      handoff = parsed as ImportHandoff;
    } catch {
      return;
    }

    const ageMs = Date.now() - handoff.stashedAtMs;
    if (ageMs < 0 || ageMs > IMPORT_HANDOFF_MAX_AGE_MS) return;

    if (handoff.packageSource === 'inline') {
      if (!handoff.package) return;
      this.runHandoffPreview(handoff.package);
      return;
    }

    if (handoff.packageSource === 'draft' && handoff.conversationId) {
      this.importLoading.set(true);
      this.api.getPackageDraft(handoff.conversationId).subscribe({
        next: pkg => this.runHandoffPreview(pkg),
        error: err => {
          this.importLoading.set(false);
          this.importError.set(this.errorMessage(err, 'Could not load the conversation draft.'));
        },
      });
    }
  }

  /** sc-397: shared seed step for inline + draft handoffs. Mirrors the file-upload preview
   *  path (clears resolution state, populates pendingImportPackage, runs the initial preview,
   *  precomputes Copy suffixes) so the user lands in the same UI as a fresh upload. */
  private runHandoffPreview(pkg: unknown): void {
    this.importError.set(null);
    this.importSuccess.set(null);
    this.importPreview.set(null);
    this.pendingImportPackage = null;
    this.resolutions.set(new Map());
    this.precomputedCopySuffixes.set(new Map());
    this.driftConflict.set(null);
    this.useExistingPrompt.set(null);
    this.useExistingWarned.set(false);
    this.importLoading.set(true);

    this.api.previewPackageImport(pkg).subscribe({
      next: preview => {
        this.importPreview.set(preview);
        this.pendingImportPackage = pkg;
        this.importLoading.set(false);
        void this.precomputeCopySuffixesAsync(preview, pkg);
      },
      error: err => {
        this.importError.set(this.errorMessage(err, 'Failed to preview workflow package.'));
        this.importLoading.set(false);
      },
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
    // sc-396: a fresh upload invalidates any in-flight resolution state from a previous file.
    this.resolutions.set(new Map());
    this.precomputedCopySuffixes.set(new Map());
    this.driftConflict.set(null);
    this.useExistingPrompt.set(null);
    this.useExistingWarned.set(false);
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
            // sc-396: precompute Copy suffixes off the main thread so the dropdown's
            // "Copy as `{key}-abc123`" label is stable by the time the user opens it.
            void this.precomputeCopySuffixesAsync(preview, parsed);
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

  // ----------------------------------------------------------------------------------------
  // sc-396: per-conflict resolution UI — dropdown wiring, debounced re-preview, UseExisting
  // confirmation, and drift-409 retry. CR-2/CR-3 (sc-394, sc-395) own the importer + endpoint
  // semantics; this code is purely the imports-page surface.
  // ----------------------------------------------------------------------------------------

  /** Stable identity for a preview row: `kind:key:sourceVersion`. SourceVersion is null on
   *  unversioned kinds (Skill / Role / McpServer / AgentRoleAssignment) so the empty trail
   *  still makes a unique key for them. */
  rowKey(item: WorkflowPackageImportItem): string {
    return `${item.kind}:${item.key}:${item.sourceVersion ?? ''}`;
  }

  /** True when the row is a Conflict or Refused (the only rows that get a resolution control).
   *  Refused rows still get the dropdown so the user can swap to Bump or Copy after a port-
   *  shape refusal — UseExisting is the only mode the structural check rules out. */
  rowIsResolvable(item: WorkflowPackageImportItem): boolean {
    return item.action === 'Conflict' || item.action === 'Refused';
  }

  resolutionFor(item: WorkflowPackageImportItem): RowResolutionChoice | undefined {
    return this.resolutions().get(this.rowKey(item));
  }

  /** Choices for a row's dropdown. Three modes are wired today; UseExisting requires a
   *  library version, Bump/Copy require both a sourceVersion and a versioned kind. */
  resolutionOptions(item: WorkflowPackageImportItem): Array<{ id: WorkflowPackageImportResolutionMode; label: string; disabled?: boolean }> {
    const versionedKind = item.kind === 'Agent' || item.kind === 'Workflow';
    const out: Array<{ id: WorkflowPackageImportResolutionMode; label: string; disabled?: boolean }> = [];

    if (item.existingMaxVersion != null) {
      out.push({
        id: 'UseExisting',
        label: `Use existing v${item.existingMaxVersion}`,
        // Refused-on-UseExisting can't be re-resolved by picking UseExisting again — same
        // structural mismatch fires.
        disabled: item.action === 'Refused',
      });
    }

    if (versionedKind && item.sourceVersion != null) {
      const bumpTarget = (item.existingMaxVersion ?? item.sourceVersion) + 1;
      out.push({ id: 'Bump', label: `Bump to v${bumpTarget}` });

      const suffix = this.precomputedCopySuffixes().get(this.rowKey(item));
      out.push({
        id: 'Copy',
        label: suffix ? `Copy as ${item.key}-${suffix}` : 'Copy as new key (computing…)',
        disabled: !suffix,
      });
    }

    return out;
  }

  /** Dropdown change handler. UseExisting on the first pick prompts a confirmation modal
   *  to flag "the library version may behave differently than the version this workflow was
   *  authored against"; once the user OKs once, subsequent picks skip the prompt. */
  onResolutionChange(item: WorkflowPackageImportItem, modeId: string): void {
    const key = this.rowKey(item);
    if (modeId === '' || modeId === 'none') {
      this.removeResolution(key);
      return;
    }
    const mode = modeId as WorkflowPackageImportResolutionMode;
    const choice: RowResolutionChoice = {
      mode,
      expectedExistingMaxVersion: item.existingMaxVersion ?? null,
    };
    if (mode === 'Copy') {
      const suffix = this.precomputedCopySuffixes().get(key);
      if (!suffix) return;
      choice.newKey = `${item.key}-${suffix}`;
    }

    if (mode === 'UseExisting' && !this.useExistingWarned()) {
      this.useExistingPrompt.set({
        rowKey: key,
        choice,
        libraryVersion: item.existingMaxVersion ?? 0,
      });
      return;
    }

    this.commitResolution(key, choice);
  }

  confirmUseExistingPrompt(): void {
    const prompt = this.useExistingPrompt();
    if (!prompt) return;
    this.useExistingWarned.set(true);
    this.useExistingPrompt.set(null);
    this.commitResolution(prompt.rowKey, prompt.choice);
  }

  cancelUseExistingPrompt(): void {
    this.useExistingPrompt.set(null);
  }

  private commitResolution(key: string, choice: RowResolutionChoice): void {
    const next = new Map(this.resolutions());
    next.set(key, choice);
    this.resolutions.set(next);
    this.scheduleRePreview();
  }

  private removeResolution(key: string): void {
    if (!this.resolutions().has(key)) return;
    const next = new Map(this.resolutions());
    next.delete(key);
    this.resolutions.set(next);
    this.scheduleRePreview();
  }

  /** sc-396: clear every resolution and re-fetch the preview against the original package.
   *  Used when the user wants to start fresh — typical workflow is "I picked the wrong mode
   *  on a few rows, just reset and let me re-pick." */
  clearResolutions(): void {
    if (this.resolutions().size === 0) return;
    this.resolutions.set(new Map());
    this.scheduleRePreview();
  }

  /** Debounced re-preview. 300ms is plenty for a human dropdown change; faster than the
   *  preview round-trip (the server runs validators) so the UI doesn't fire mid-typing. */
  private scheduleRePreview(): void {
    if (this.rePreviewTimer !== null) clearTimeout(this.rePreviewTimer);
    this.rePreviewTimer = setTimeout(() => {
      this.rePreviewTimer = null;
      this.runRePreview();
    }, 300);
  }

  private runRePreview(): void {
    const pkg = this.pendingImportPackage;
    if (!pkg) return;

    const resolutions = this.buildResolutionList();
    this.importError.set(null);
    this.driftConflict.set(null);
    // Re-preview never blocks the apply button — we just refresh the rendered state.
    this.api.previewPackageImport(pkg, resolutions.length > 0 ? resolutions : undefined).subscribe({
      next: preview => this.importPreview.set(preview),
      error: err => this.importError.set(this.errorMessage(err, 'Failed to re-preview workflow package.')),
    });
  }

  /** Convert the per-row resolution Map into the wire-format list the API expects. The Map's
   *  key already encodes (kind, key, sourceVersion); we tear it back apart here. */
  private buildResolutionList(): WorkflowPackageImportResolution[] {
    const out: WorkflowPackageImportResolution[] = [];
    for (const [key, choice] of this.resolutions()) {
      const idx = key.indexOf(':');
      const kind = key.slice(0, idx);
      const rest = key.slice(idx + 1);
      const versionIdx = rest.lastIndexOf(':');
      const itemKey = rest.slice(0, versionIdx);
      const sourceVersionRaw = rest.slice(versionIdx + 1);
      const sourceVersion = sourceVersionRaw === '' ? null : Number(sourceVersionRaw);
      out.push({
        kind: kind as WorkflowPackageImportResolution['kind'],
        key: itemKey,
        sourceVersion,
        mode: choice.mode,
        newKey: choice.newKey ?? null,
        expectedExistingMaxVersion: choice.expectedExistingMaxVersion ?? null,
      });
    }
    return out;
  }

  /** Walk the just-loaded preview's resolvable rows (Conflict + Refused) and precompute a
   *  6-hex-char SHA-256 suffix from the resolved entity's body in the package. The suffix is
   *  what the Copy dropdown option labels show ("Copy as `{key}-abc123`") and what we send
   *  as NewKey on apply. Async; the dropdown options re-render once the Map is populated. */
  private async precomputeCopySuffixesAsync(preview: WorkflowPackageImportPreview, pkg: unknown): Promise<void> {
    const next = new Map<string, string>();
    const versioned = preview.items.filter(item =>
      this.rowIsResolvable(item) &&
      (item.kind === 'Agent' || item.kind === 'Workflow') &&
      item.sourceVersion != null);

    for (const item of versioned) {
      const body = this.findEntityBody(pkg, item.kind, item.key, item.sourceVersion!);
      if (!body) continue;
      try {
        const json = JSON.stringify(body);
        const bytes = new TextEncoder().encode(json);
        const digest = await crypto.subtle.digest('SHA-256', bytes);
        const hex = Array.from(new Uint8Array(digest, 0, 3))
          .map(b => b.toString(16).padStart(2, '0'))
          .join('');
        next.set(this.rowKey(item), hex);
      } catch {
        // crypto.subtle unavailable (older browsers) or digest failed — skip silently;
        // the Copy option remains disabled with the "computing…" label, which is harmless
        // on the modern-browser-only target this app already runs on.
      }
    }
    this.precomputedCopySuffixes.set(next);
  }

  private findEntityBody(pkg: unknown, kind: string, key: string, version: number): unknown {
    if (!pkg || typeof pkg !== 'object') return null;
    const root = pkg as Record<string, unknown>;
    const collectionName = kind === 'Agent' ? 'agents' : kind === 'Workflow' ? 'workflows' : null;
    if (!collectionName) return null;
    const collection = root[collectionName];
    if (!Array.isArray(collection)) return null;
    return collection.find(entry =>
      entry && typeof entry === 'object' &&
      (entry as Record<string, unknown>)['key'] === key &&
      (entry as Record<string, unknown>)['version'] === version) ?? null;
  }

  applyPackageImport(): void {
    if (!this.pendingImportPackage || !this.importPreview()?.canApply) {
      return;
    }

    if (!window.confirm('Apply this workflow package import?')) {
      return;
    }

    this.runApply(/* acknowledgeDrift */ false);
  }

  /** sc-396: invoked from the drift-conflict banner. Re-submits the apply with
   *  `acknowledgeDrift: true` so the importer accepts the live max versions. */
  applyAcknowledgingDrift(): void {
    if (!this.pendingImportPackage) return;
    this.runApply(/* acknowledgeDrift */ true);
  }

  private runApply(acknowledgeDrift: boolean): void {
    this.importError.set(null);
    this.importSuccess.set(null);
    this.driftConflict.set(null);
    this.importApplyLoading.set(true);

    const resolutions = this.buildResolutionList();
    this.api.applyPackageImport(
      this.pendingImportPackage,
      resolutions.length > 0 ? resolutions : undefined,
      acknowledgeDrift || undefined,
    ).subscribe({
      next: result => {
        this.importSuccess.set(
          `Import applied: ${result.createCount} created, ${result.reuseCount} reused.`
        );
        this.resolutions.set(new Map());
        this.precomputedCopySuffixes.set(new Map());
        this.useExistingWarned.set(false);
        this.categoryFilter.set('All');
        this.tagFilter.set([]);
        this.importApplyLoading.set(false);
        this.reload();
      },
      error: err => {
        const drift = this.extractDriftConflict(err);
        if (drift) {
          // Don't set importError — the banner is enough and surfaces "Apply anyway" inline.
          this.driftConflict.set(drift);
        } else {
          this.importError.set(this.errorMessage(err, 'Failed to apply workflow package.'));
        }
        this.importApplyLoading.set(false);
      }
    });
  }

  private extractDriftConflict(err: unknown): WorkflowPackageImportDriftConflict | null {
    const httpErr = err as { status?: number; error?: unknown };
    if (httpErr?.status !== 409) return null;
    const body = httpErr.error;
    if (!body || typeof body !== 'object') return null;
    const candidate = body as { error?: unknown; movedEntities?: unknown };
    if (typeof candidate.error !== 'string' || !Array.isArray(candidate.movedEntities)) return null;
    return body as WorkflowPackageImportDriftConflict;
  }

  importActionVariant(action: WorkflowPackageImportAction): ChipVariant {
    switch (action) {
      case 'Create': return 'ok';
      case 'Reuse': return 'default';
      case 'Conflict': return 'err';
      // sc-395: Refused is a structural-check failure (port-shape mismatch on UseExisting).
      // Hard apply-blocker, but visually distinct from a bare Conflict so the resolver UI
      // (CR-4) can suggest "pick Bump or Copy instead" rather than "re-emit the package."
      case 'Refused': return 'err';
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
