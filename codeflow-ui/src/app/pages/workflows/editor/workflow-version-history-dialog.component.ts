import {
  AfterViewInit,
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  ElementRef,
  EventEmitter,
  Input,
  OnChanges,
  OnDestroy,
  Output,
  SimpleChanges,
  ViewChildren,
  QueryList,
  computed,
  inject,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { ButtonComponent } from '../../../ui/button.component';
import { ChipComponent } from '../../../ui/chip.component';
import { DialogComponent } from '../../../ui/dialog.component';
import { ThemeService } from '../../../core/theme.service';
import { WorkflowsApi } from '../../../core/workflows.api';
import { WorkflowDetail } from '../../../core/models';
import {
  computeWorkflowVersionDiff,
  NodeChangedDiff,
  NodeFieldChange,
  summarizeDiff,
  WorkflowVersionDiff,
} from './workflow-version-diff';
import { ensureMonacoEnvironment } from './monaco-environment';

interface VersionItem {
  version: number;
  createdAtUtc: string;
  detail?: WorkflowDetail;
}

/**
 * T3: Side-by-side workflow version diff. Loads the list of versions for `workflowKey`, lets the
 * author pick two, and renders a structured diff (metadata, nodes, edges) plus Monaco diff
 * editors for any modified scripts. Same component is reusable inside the package preview (E5).
 */
@Component({
  selector: 'cf-workflow-version-history-dialog',
  standalone: true,
  imports: [CommonModule, DialogComponent, ButtonComponent, ChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cf-dialog
      [open]="open"
      [title]="'Version history — ' + (workflowKey || '')"
      maxWidth="1100px"
      (close)="closed.emit()">

      <div class="vh-layout">
        <aside class="vh-sidebar">
          <div class="vh-sidebar-head muted small">Pick two versions to compare</div>
          @if (loadingVersions()) {
            <div class="muted small">Loading…</div>
          } @else if (versions().length === 0) {
            <div class="muted small">No versions yet.</div>
          } @else {
            <ul class="vh-version-list">
              @for (v of versions(); track v.version) {
                <li>
                  <button type="button"
                          class="vh-version-row"
                          [attr.data-selected]="isSelected(v.version) ? 'true' : null"
                          (click)="toggleSelect(v.version)">
                    <span class="vh-version-id">v{{ v.version }}</span>
                    <span class="vh-version-time">{{ formatTime(v.createdAtUtc) }}</span>
                    @if (selectedA() === v.version) { <cf-chip mono variant="accent">A</cf-chip> }
                    @else if (selectedB() === v.version) { <cf-chip mono variant="accent">B</cf-chip> }
                  </button>
                </li>
              }
            </ul>
          }
        </aside>

        <section class="vh-diff">
          @if (diffStatus() === 'idle') {
            <div class="vh-empty muted">Select two versions on the left to compare.</div>
          } @else if (diffStatus() === 'loading') {
            <div class="vh-empty muted">Loading versions…</div>
          } @else if (diff(); as d) {
            <div class="vh-diff-head">
              <strong>{{ workflowKey }}</strong>
              <span class="muted small">v{{ d.beforeVersion }} → v{{ d.afterVersion }}</span>
              <cf-chip mono>{{ summary() }}</cf-chip>
              @if (renderMs() !== null) { <span class="muted xsmall">rendered in {{ renderMs() }}ms</span> }
            </div>

            @if (d.metadata.length > 0) {
              <div class="vh-section">
                <h4>Metadata</h4>
                <ul class="vh-change-list">
                  @for (m of d.metadata; track m.field) {
                    <li>
                      <code class="mono">{{ m.field }}</code>:
                      <span class="vh-removed">{{ formatScalar(m.before) }}</span> →
                      <span class="vh-added">{{ formatScalar(m.after) }}</span>
                    </li>
                  }
                </ul>
              </div>
            }

            @if (d.nodes.length > 0) {
              <div class="vh-section">
                <h4>Nodes ({{ d.nodes.length }})</h4>
                <ul class="vh-change-list">
                  @for (nd of d.nodes; track nodeTrack(nd)) {
                    @if (nd.kind === 'added') {
                      <li>
                        <cf-chip mono variant="ok">added</cf-chip>
                        <code class="mono">{{ nd.node.id.slice(0, 8) }}</code> ({{ nd.node.kind }})
                        @if (nd.node.agentKey) { <span class="muted small">agent: {{ nd.node.agentKey }}@v{{ nd.node.agentVersion }}</span> }
                      </li>
                    } @else if (nd.kind === 'removed') {
                      <li>
                        <cf-chip mono variant="err">removed</cf-chip>
                        <code class="mono">{{ nd.node.id.slice(0, 8) }}</code> ({{ nd.node.kind }})
                      </li>
                    } @else {
                      <li class="vh-changed-node">
                        <div class="vh-changed-node-head">
                          <cf-chip mono variant="accent">changed</cf-chip>
                          <code class="mono">{{ nd.before.id.slice(0, 8) }}</code> ({{ nd.after.kind }})
                        </div>
                        <ul class="vh-field-list">
                          @for (c of nd.changes; track c.field) {
                            <li [class.cosmetic]="c.cosmetic">
                              <strong>{{ c.label }}</strong>:
                              @if (c.field === 'inputScript' || c.field === 'outputScript') {
                                <div class="vh-monaco-host"
                                     [attr.data-node]="nd.before.id"
                                     [attr.data-field]="c.field"
                                     #monacoHost></div>
                              } @else {
                                <span class="vh-removed">{{ formatScalar(c.before) }}</span> →
                                <span class="vh-added">{{ formatScalar(c.after) }}</span>
                              }
                            </li>
                          }
                        </ul>
                      </li>
                    }
                  }
                </ul>
              </div>
            }

            @if (d.edges.length > 0) {
              <div class="vh-section">
                <h4>Edges ({{ d.edges.length }})</h4>
                <ul class="vh-change-list">
                  @for (ed of d.edges; track edgeTrack(ed)) {
                    @if (ed.kind === 'added') {
                      <li>
                        <cf-chip mono variant="ok">added</cf-chip>
                        <code class="mono">{{ ed.edge.fromNodeId.slice(0,8) }}.{{ ed.edge.fromPort }} → {{ ed.edge.toNodeId.slice(0,8) }}.{{ ed.edge.toPort }}</code>
                      </li>
                    } @else if (ed.kind === 'removed') {
                      <li>
                        <cf-chip mono variant="err">removed</cf-chip>
                        <code class="mono">{{ ed.edge.fromNodeId.slice(0,8) }}.{{ ed.edge.fromPort }} → {{ ed.edge.toNodeId.slice(0,8) }}.{{ ed.edge.toPort }}</code>
                      </li>
                    } @else {
                      <li>
                        <cf-chip mono variant="accent">changed</cf-chip>
                        <code class="mono">{{ ed.before.fromNodeId.slice(0,8) }}.{{ ed.before.fromPort }} → {{ ed.before.toNodeId.slice(0,8) }}.{{ ed.before.toPort }}</code>
                        <span class="muted small">({{ ed.changedFields.join(', ') }})</span>
                      </li>
                    }
                  }
                </ul>
              </div>
            }
          } @else if (diffStatus() === 'error') {
            <div class="vh-empty error">Failed to load one or both versions.</div>
          }
        </section>
      </div>

      <div dialog-footer>
        <button type="button" cf-button variant="ghost" (click)="closed.emit()">Close</button>
      </div>
    </cf-dialog>
  `,
  styles: [`
    .vh-layout { display: grid; grid-template-columns: 240px 1fr; gap: 16px; min-height: 360px; }
    .vh-sidebar { border-right: 1px solid var(--border); padding-right: 12px; max-height: 70vh; overflow-y: auto; }
    .vh-sidebar-head { margin-bottom: 8px; }
    .vh-version-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 2px; }
    .vh-version-row {
      display: flex; align-items: center; gap: 6px; width: 100%;
      padding: 6px 8px; border: 1px solid transparent; border-radius: var(--radius);
      background: transparent; color: inherit; cursor: pointer; text-align: left;
      font: inherit;
    }
    .vh-version-row:hover { background: var(--surface-2); }
    .vh-version-row[data-selected="true"] { border-color: var(--accent, var(--fg)); background: var(--surface-2); }
    .vh-version-id { font-family: var(--font-mono); font-weight: 600; min-width: 36px; }
    .vh-version-time { flex: 1; color: var(--muted); font-size: var(--fs-sm); }
    .vh-diff { max-height: 70vh; overflow-y: auto; padding: 0 6px; }
    .vh-diff-head { display: flex; align-items: center; gap: 8px; margin-bottom: 12px; flex-wrap: wrap; }
    .vh-empty { padding: 32px; text-align: center; }
    .vh-empty.error { color: var(--sem-red); }
    .vh-section { margin-bottom: 18px; }
    .vh-section h4 { margin: 0 0 6px; font-size: var(--fs-md); }
    .vh-change-list { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 6px; }
    .vh-change-list li { padding: 6px 8px; border: 1px solid var(--border); border-radius: var(--radius); background: var(--surface-2); }
    .vh-changed-node { padding: 0; }
    .vh-changed-node-head { padding: 6px 8px; display: flex; align-items: center; gap: 6px; border-bottom: 1px solid var(--border); }
    .vh-field-list { list-style: none; padding: 6px 8px; margin: 0; display: flex; flex-direction: column; gap: 6px; }
    .vh-field-list li.cosmetic { opacity: 0.6; font-style: italic; }
    .vh-removed { color: var(--sem-red); font-family: var(--font-mono); }
    .vh-added { color: var(--sem-green, #3fb950); font-family: var(--font-mono); }
    .vh-monaco-host { width: 100%; height: 220px; border: 1px solid var(--border); border-radius: var(--radius); margin-top: 6px; }
  `]
})
export class WorkflowVersionHistoryDialogComponent implements OnChanges, AfterViewInit, OnDestroy {
  @Input() open = false;
  @Input() workflowKey = '';

  @Output() closed = new EventEmitter<void>();

  private readonly api = inject(WorkflowsApi);
  private readonly themeService = inject(ThemeService);
  private readonly cdr = inject(ChangeDetectorRef);

  readonly versions = signal<VersionItem[]>([]);
  readonly loadingVersions = signal(false);
  readonly selectedA = signal<number | null>(null);
  readonly selectedB = signal<number | null>(null);
  readonly diff = signal<WorkflowVersionDiff | null>(null);
  readonly diffStatus = signal<'idle' | 'loading' | 'ready' | 'error'>('idle');
  readonly renderMs = signal<number | null>(null);

  readonly summary = computed(() => {
    const d = this.diff();
    return d ? summarizeDiff(d) : '';
  });

  @ViewChildren('monacoHost') private monacoHosts?: QueryList<ElementRef<HTMLDivElement>>;

  private monacoApi?: typeof import('monaco-editor');
  private monacoEditors: { dispose: () => void }[] = [];

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['open'] && this.open) {
      this.loadVersions();
    }
    if (changes['open'] && !this.open) {
      this.disposeMonacoEditors();
      this.diff.set(null);
      this.selectedA.set(null);
      this.selectedB.set(null);
      this.diffStatus.set('idle');
    }
  }

  ngAfterViewInit(): void {
    // Render Monaco diff editors whenever the diff or its hosts change.
    if (this.monacoHosts) {
      this.monacoHosts.changes.subscribe(() => this.renderMonacoEditors());
    }
  }

  ngOnDestroy(): void {
    this.disposeMonacoEditors();
  }

  isSelected(version: number): boolean {
    return this.selectedA() === version || this.selectedB() === version;
  }

  toggleSelect(version: number): void {
    const a = this.selectedA();
    const b = this.selectedB();
    if (a === version) { this.selectedA.set(null); return this.recompute(); }
    if (b === version) { this.selectedB.set(null); return this.recompute(); }
    if (a === null) { this.selectedA.set(version); return this.recompute(); }
    if (b === null) { this.selectedB.set(version); return this.recompute(); }
    // Both filled — replace the older selection.
    this.selectedA.set(b);
    this.selectedB.set(version);
    this.recompute();
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      return d.toLocaleString();
    } catch {
      return iso;
    }
  }

  formatScalar(value: unknown): string {
    if (value === null || value === undefined) return '(none)';
    if (typeof value === 'string') return value;
    return JSON.stringify(value);
  }

  nodeTrack = (nd: WorkflowVersionDiff['nodes'][number]) => {
    return nd.kind === 'changed' ? `c-${nd.before.id}` : `${nd.kind}-${nd.node.id}`;
  };

  edgeTrack = (ed: WorkflowVersionDiff['edges'][number]) => {
    if (ed.kind === 'changed') return `c-${ed.before.fromNodeId}-${ed.before.fromPort}-${ed.before.toNodeId}-${ed.before.toPort}`;
    return `${ed.kind}-${ed.edge.fromNodeId}-${ed.edge.fromPort}-${ed.edge.toNodeId}-${ed.edge.toPort}`;
  };

  private loadVersions(): void {
    if (!this.workflowKey) return;
    this.loadingVersions.set(true);
    this.api.listVersions(this.workflowKey).subscribe({
      next: list => {
        const sorted = [...list].sort((a, b) => b.version - a.version);
        this.versions.set(sorted.map(v => ({
          version: v.version,
          createdAtUtc: v.createdAtUtc,
          detail: v,
        })));
        this.loadingVersions.set(false);
        // Auto-select the two most recent versions if available.
        if (sorted.length >= 2) {
          this.selectedA.set(sorted[1].version);
          this.selectedB.set(sorted[0].version);
          this.recompute();
        }
        this.cdr.markForCheck();
      },
      error: () => {
        this.loadingVersions.set(false);
        this.cdr.markForCheck();
      },
    });
  }

  private recompute(): void {
    const a = this.selectedA();
    const b = this.selectedB();
    if (a === null || b === null) {
      this.diff.set(null);
      this.diffStatus.set('idle');
      this.renderMs.set(null);
      return;
    }
    // Order chronologically — A is always the older version, B the newer.
    const before = Math.min(a, b);
    const after = Math.max(a, b);
    this.diffStatus.set('loading');
    const start = performance.now();
    const beforeDetail = this.versions().find(v => v.version === before)?.detail;
    const afterDetail = this.versions().find(v => v.version === after)?.detail;
    if (!beforeDetail || !afterDetail) {
      this.diffStatus.set('error');
      this.cdr.markForCheck();
      return;
    }
    const d = computeWorkflowVersionDiff(beforeDetail, afterDetail);
    this.diff.set(d);
    this.diffStatus.set('ready');
    this.renderMs.set(Math.round(performance.now() - start));
    this.cdr.markForCheck();
    // Defer Monaco render until the @ViewChildren picks up the new hosts.
    queueMicrotask(() => this.renderMonacoEditors());
  }

  private async renderMonacoEditors(): Promise<void> {
    this.disposeMonacoEditors();
    const d = this.diff();
    if (!d || !this.monacoHosts) return;

    if (!this.monacoApi) {
      try {
        ensureMonacoEnvironment();
        this.monacoApi = await import('monaco-editor');
      } catch {
        return; // Monaco unavailable; fall back to no script preview.
      }
    }
    const monaco = this.monacoApi;

    const hostsByKey = new Map<string, HTMLElement>();
    for (const ref of this.monacoHosts.toArray()) {
      const el = ref.nativeElement;
      const nodeId = el.getAttribute('data-node');
      const field = el.getAttribute('data-field');
      if (nodeId && field) hostsByKey.set(`${nodeId}|${field}`, el);
    }

    for (const nd of d.nodes) {
      if (nd.kind !== 'changed') continue;
      for (const c of (nd as NodeChangedDiff).changes) {
        if (c.field !== 'inputScript' && c.field !== 'outputScript') continue;
        const host = hostsByKey.get(`${nd.before.id}|${c.field}`);
        if (!host) continue;
        const original = monaco.editor.createModel(String(c.before ?? ''), 'javascript');
        const modified = monaco.editor.createModel(String(c.after ?? ''), 'javascript');
        const diffEditor = monaco.editor.createDiffEditor(host, {
          theme: this.themeService.theme() === 'light' ? 'vs' : 'vs-dark',
          readOnly: true,
          renderSideBySide: true,
          automaticLayout: true,
          scrollBeyondLastLine: false,
          fontSize: 12,
          minimap: { enabled: false },
        });
        diffEditor.setModel({ original, modified });
        this.monacoEditors.push({
          dispose: () => {
            diffEditor.dispose();
            original.dispose();
            modified.dispose();
          },
        });
      }
    }
  }

  private disposeMonacoEditors(): void {
    for (const e of this.monacoEditors) {
      try { e.dispose(); } catch { /* ignore */ }
    }
    this.monacoEditors = [];
  }
}

