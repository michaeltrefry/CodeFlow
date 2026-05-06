import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  EventEmitter,
  OnDestroy,
  Output,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { ArtifactEventView } from './chat-panel.component';
import {
  ensureMonacoEditorStyles,
  ensureMonacoEnvironment,
  loadMonacoEditor,
} from '../../pages/workflows/editor/monaco-environment';

/**
 * sc-798 (AA-7): read-only side sheet that shows a Monaco side-by-side diff between an
 * artifact and either (a) its immediately-superseded prior version, or (b) the current
 * library version of the workflow it ships. Lazy-loaded — Monaco's diff editor mounts only
 * when the user clicks Diff.
 *
 * Mode availability:
 * - **prior**: enabled when a prior superseded event with the same name exists in the
 *   conversation. Greyed out for snapshot kinds (each snapshot is unique-by-id, no prior).
 * - **library**: enabled when the package's entry-point key already exists in the library.
 *   Greyed out for entirely new workflows or when the version fetch returns 404.
 */
@Component({
  selector: 'cf-artifact-diff',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="artifact-diff-backdrop" (click)="onClose()" data-testid="artifact-diff-backdrop">
      <div class="artifact-diff-sheet" (click)="$event.stopPropagation()" role="dialog" aria-modal="true">
        <header class="artifact-diff-head">
          <span class="artifact-diff-title">Diff: {{ event().name }}</span>
          <span class="artifact-diff-modes" role="tablist">
            <button
              type="button"
              class="artifact-diff-mode"
              role="tab"
              [attr.aria-selected]="mode() === 'prior'"
              [attr.data-active]="mode() === 'prior'"
              [disabled]="!hasPriorEvent()"
              (click)="setMode('prior')"
            >Prior version</button>
            <button
              type="button"
              class="artifact-diff-mode"
              role="tab"
              [attr.aria-selected]="mode() === 'library'"
              [attr.data-active]="mode() === 'library'"
              [disabled]="!entryPointKey()"
              (click)="setMode('library')"
            >Library</button>
          </span>
          <button
            type="button"
            class="artifact-diff-close"
            aria-label="Close diff"
            (click)="onClose()"
          >×</button>
        </header>
        @if (loadState() === 'loading') {
          <div class="artifact-diff-status">Loading…</div>
        } @else if (loadState() === 'error') {
          <div class="artifact-diff-status artifact-diff-error">{{ errorMessage() }}</div>
        } @else if (loadState() === 'unavailable') {
          <div class="artifact-diff-status artifact-diff-warn">{{ unavailableReason() }}</div>
        }
        <div #monacoHost class="artifact-diff-monaco" [style.display]="loadState() === 'ready' ? 'block' : 'none'"></div>
      </div>
    </div>
  `,
  styles: [`
    :host { display: contents; }
    .artifact-diff-backdrop {
      position: fixed;
      inset: 0;
      background: rgba(0, 0, 0, 0.5);
      display: flex;
      align-items: stretch;
      justify-content: flex-end;
      z-index: 1000;
    }
    .artifact-diff-sheet {
      display: flex;
      flex-direction: column;
      width: min(960px, 95vw);
      height: 100%;
      background: var(--surface, #131519);
      border-left: 1px solid var(--border, rgba(255,255,255,0.12));
      box-shadow: -8px 0 24px rgba(0,0,0,0.4);
    }
    .artifact-diff-head {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 10px 14px;
      border-bottom: 1px solid var(--border, rgba(255,255,255,0.08));
      flex: 0 0 auto;
    }
    .artifact-diff-title {
      flex: 1 1 auto;
      font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace);
      font-size: 12px;
      color: var(--text, #E7E9EE);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .artifact-diff-modes {
      display: flex;
      gap: 4px;
    }
    .artifact-diff-mode {
      appearance: none;
      cursor: pointer;
      font-size: 11px;
      padding: 4px 10px;
      border-radius: 3px;
      border: 1px solid var(--border, rgba(255,255,255,0.16));
      background: transparent;
      color: var(--text, #E7E9EE);
    }
    .artifact-diff-mode[data-active='true'] {
      border-color: var(--accent, #5765ff);
      color: var(--accent, #5765ff);
    }
    .artifact-diff-mode:disabled {
      opacity: 0.4;
      cursor: not-allowed;
    }
    .artifact-diff-close {
      appearance: none;
      cursor: pointer;
      background: transparent;
      border: none;
      color: var(--text-muted, #9aa3b2);
      font-size: 18px;
      line-height: 1;
      padding: 4px 8px;
    }
    .artifact-diff-status {
      padding: 24px;
      font-size: 13px;
      color: var(--text-muted, #9aa3b2);
    }
    .artifact-diff-error { color: var(--err, #ff6b6b); }
    .artifact-diff-warn { color: var(--warn, #f5a623); }
    .artifact-diff-monaco {
      flex: 1 1 auto;
      min-height: 0;
    }
  `],
})
export class ArtifactDiffComponent implements AfterViewInit, OnDestroy {
  private readonly destroyRef = inject(DestroyRef);

  readonly conversationId = input.required<string>();
  /** The artifact event the user clicked Diff on (the "right" side of the diff). */
  readonly event = input.required<ArtifactEventView>();
  /**
   * Optional — the immediately-superseded artifact event with the same name. Computed by the
   * chat panel via `findPriorArtifactByName` and passed in. Null when no prior exists (the
   * Prior tab grays out).
   */
  readonly priorEvent = input<ArtifactEventView | null>(null);
  /**
   * Optional — the entry-point workflow key from the package summary. Used by the Library
   * mode to fetch `/api/workflows/{key}/{version}/package`. Null when the package's
   * entry-point isn't available client-side, in which case the Library tab grays out.
   */
  readonly entryPointKey = input<string | null>(null);
  /** Optional pinned starting version for the Library diff (max-version is the default). */
  readonly entryPointVersion = input<number | null>(null);

  @Output() readonly closed = new EventEmitter<void>();

  protected readonly mode = signal<'prior' | 'library'>('prior');
  protected readonly loadState = signal<'idle' | 'loading' | 'ready' | 'error' | 'unavailable'>('idle');
  protected readonly errorMessage = signal<string>('');
  protected readonly unavailableReason = signal<string>('');

  protected readonly hasPriorEvent = computed(() => this.priorEvent() != null);

  @ViewChild('monacoHost') private monacoHost!: ElementRef<HTMLDivElement>;
  private monacoEditor: { dispose: () => void } | null = null;
  private aborter: AbortController | null = null;

  constructor() {
    // Pick a default mode honoring availability: prior if it exists, else library, else
    // 'unavailable' state so the user sees why nothing rendered.
    effect(() => {
      const hasPrior = this.priorEvent() != null;
      const hasLibrary = this.entryPointKey() != null;
      if (!hasPrior && hasLibrary) {
        this.mode.set('library');
      } else if (!hasPrior && !hasLibrary) {
        // Neither side available — show a one-line explanation in the sheet.
        this.loadState.set('unavailable');
        this.unavailableReason.set(
          'No prior version is available for this artifact, and its entry-point workflow does not exist in the library yet.',
        );
        return;
      }
      this.mountAndDiff();
    });
  }

  ngAfterViewInit(): void { /* effect drives the first mount */ }

  ngOnDestroy(): void {
    this.aborter?.abort();
    this.aborter = null;
    this.monacoEditor?.dispose();
    this.monacoEditor = null;
  }

  protected onClose(): void {
    this.closed.emit();
  }

  protected setMode(mode: 'prior' | 'library'): void {
    if (mode === 'prior' && !this.hasPriorEvent()) return;
    if (mode === 'library' && !this.entryPointKey()) return;
    if (this.mode() === mode) return;
    this.mode.set(mode);
    this.mountAndDiff();
  }

  private async mountAndDiff(): Promise<void> {
    this.aborter?.abort();
    const aborter = new AbortController();
    this.aborter = aborter;
    this.loadState.set('loading');

    let leftBody: string;
    let rightBody: string;
    try {
      [leftBody, rightBody] = await Promise.all([
        this.fetchLeftSide(aborter.signal),
        this.fetchRightSide(aborter.signal),
      ]);
    } catch (e) {
      if (aborter.signal.aborted) return;
      this.loadState.set('error');
      this.errorMessage.set(e instanceof Error ? e.message : String(e));
      return;
    }

    if (aborter.signal.aborted) return;

    try {
      ensureMonacoEnvironment();
      await ensureMonacoEditorStyles();
      const monaco = await loadMonacoEditor();
      if (aborter.signal.aborted) return;

      this.monacoEditor?.dispose();
      const editor = monaco.editor.createDiffEditor(this.monacoHost.nativeElement, {
        readOnly: true,
        renderSideBySide: true,
        automaticLayout: true,
        minimap: { enabled: false },
      });
      editor.setModel({
        original: monaco.editor.createModel(leftBody, 'json'),
        modified: monaco.editor.createModel(rightBody, 'json'),
      });
      this.monacoEditor = editor;
      this.loadState.set('ready');
    } catch (e) {
      if (aborter.signal.aborted) return;
      this.loadState.set('error');
      this.errorMessage.set(e instanceof Error ? e.message : 'Failed to mount diff editor.');
    }
  }

  private async fetchLeftSide(signal: AbortSignal): Promise<string> {
    if (this.mode() === 'prior') {
      const prior = this.priorEvent();
      if (!prior) {
        throw new Error('No prior version available.');
      }
      return prettyFetchJson(this.artifactUrl(prior.id), signal);
    }
    // library
    const key = this.entryPointKey();
    if (!key) throw new Error('No library version available.');
    const version = this.entryPointVersion();
    const url = version != null
      ? `/api/workflows/${encodeURIComponent(key)}/${version}/package`
      : `/api/workflows/${encodeURIComponent(key)}`;
    return prettyFetchJson(url, signal);
  }

  private async fetchRightSide(signal: AbortSignal): Promise<string> {
    return prettyFetchJson(this.artifactUrl(this.event().id), signal);
  }

  private artifactUrl(eventId: string): string {
    return `/api/assistant/conversations/${encodeURIComponent(this.conversationId())}/artifacts/${encodeURIComponent(eventId)}`;
  }
}

async function prettyFetchJson(url: string, signal: AbortSignal): Promise<string> {
  const response = await fetch(url, { credentials: 'include', signal });
  if (!response.ok) {
    throw new Error(`${url} → ${response.status}`);
  }
  const text = await response.text();
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}

/**
 * sc-798 (AA-7): pure helper that picks the immediately-superseded prior event for a given
 * artifact view. "Prior" = the highest-sequence event with the same `name` that comes
 * before `current` AND is superseded. Returns null when no such event exists (typical for
 * the first event of a name, or for snapshots which never share a name). Exported for
 * unit-testing.
 */
export function findPriorArtifactByName(
  events: ArtifactEventView[],
  current: ArtifactEventView,
): ArtifactEventView | null {
  let best: ArtifactEventView | null = null;
  for (const e of events) {
    if (e.id === current.id) continue;
    if (e.name !== current.name) continue;
    if (e.sequence >= current.sequence) continue;
    if (!e.superseded) continue;
    if (best === null || e.sequence > best.sequence) {
      best = e;
    }
  }
  return best;
}
