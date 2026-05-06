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
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ensureMonacoEditorStyles, ensureMonacoEnvironment, loadMonacoEditor } from '../../pages/workflows/editor/monaco-environment';

/**
 * sc-795 (AA-4): read-only side sheet that previews an artifact's bytes inline. Mounts a
 * Monaco editor in read-only mode with JSON syntax highlighting (the only kinds AA-1/AA-2
 * produce are package drafts/snapshots, both JSON). Lazy-loaded — Monaco's bundle isn't
 * pulled until the first View click.
 *
 * Failure modes:
 * - 410 Gone (expired) → emits `expired` so the chat panel marks the pill stale.
 * - any other error → shows an inline error string, leaves the sheet open so the user can
 *   close it without losing context.
 */
@Component({
  selector: 'cf-artifact-preview',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="artifact-preview-backdrop" (click)="onClose()" data-testid="artifact-preview-backdrop">
      <div class="artifact-preview-sheet" (click)="$event.stopPropagation()" role="dialog" aria-modal="true">
        <header class="artifact-preview-head">
          <span class="artifact-preview-title">{{ name() }}</span>
          <button
            type="button"
            class="artifact-preview-close"
            aria-label="Close preview"
            (click)="onClose()"
          >×</button>
        </header>
        @if (loadState() === 'loading') {
          <div class="artifact-preview-status">Loading…</div>
        } @else if (loadState() === 'error') {
          <div class="artifact-preview-status artifact-preview-error">{{ errorMessage() }}</div>
        } @else if (loadState() === 'expired') {
          <div class="artifact-preview-status artifact-preview-expired">
            This artifact has been consumed and is no longer downloadable.
          </div>
        }
        <div #monacoHost class="artifact-preview-monaco" [style.display]="loadState() === 'ready' ? 'block' : 'none'"></div>
      </div>
    </div>
  `,
  styles: [`
    :host { display: contents; }
    .artifact-preview-backdrop {
      position: fixed;
      inset: 0;
      background: rgba(0, 0, 0, 0.5);
      display: flex;
      align-items: stretch;
      justify-content: flex-end;
      z-index: 1000;
    }
    .artifact-preview-sheet {
      display: flex;
      flex-direction: column;
      width: min(640px, 90vw);
      height: 100%;
      background: var(--surface, #131519);
      border-left: 1px solid var(--border, rgba(255,255,255,0.12));
      box-shadow: -8px 0 24px rgba(0,0,0,0.4);
    }
    .artifact-preview-head {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 10px 14px;
      border-bottom: 1px solid var(--border, rgba(255,255,255,0.08));
      flex: 0 0 auto;
    }
    .artifact-preview-title {
      flex: 1 1 auto;
      font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace);
      font-size: 12px;
      color: var(--text, #E7E9EE);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .artifact-preview-close {
      appearance: none;
      cursor: pointer;
      background: transparent;
      border: none;
      color: var(--text-muted, #9aa3b2);
      font-size: 18px;
      line-height: 1;
      padding: 4px 8px;
    }
    .artifact-preview-status {
      padding: 24px;
      font-size: 13px;
      color: var(--text-muted, #9aa3b2);
    }
    .artifact-preview-error { color: var(--err, #ff6b6b); }
    .artifact-preview-expired { color: var(--warn, #f5a623); }
    .artifact-preview-monaco {
      flex: 1 1 auto;
      min-height: 0;
    }
  `],
})
export class ArtifactPreviewComponent implements AfterViewInit, OnDestroy {
  private readonly destroyRef = inject(DestroyRef);

  /** Conversation id that owns the artifact — drives the download endpoint URL. */
  readonly conversationId = input.required<string>();
  /** Durable artifact event id — keys the download endpoint. */
  readonly eventId = input.required<string>();
  /** Display name shown in the header (e.g. `draft.cf-workflow-package.json`). */
  readonly name = input.required<string>();

  /** Emitted when the user closes the sheet. */
  @Output() readonly closed = new EventEmitter<void>();
  /** Emitted when the bytes are gone (410); chat panel uses this to flip the pill state. */
  @Output() readonly expired = new EventEmitter<void>();

  protected readonly loadState = signal<'loading' | 'ready' | 'expired' | 'error'>('loading');
  protected readonly errorMessage = signal<string>('');

  @ViewChild('monacoHost') private monacoHost!: ElementRef<HTMLDivElement>;
  private monacoEditor: { dispose: () => void } | null = null;
  private aborter: AbortController | null = null;

  constructor() {
    effect(() => {
      // Re-fetch when inputs change (e.g. user clicks View on a different pill while one is open).
      const id = this.eventId();
      const conv = this.conversationId();
      if (!id || !conv) return;
      this.loadState.set('loading');
      this.errorMessage.set('');
      this.fetchAndRender(conv, id);
    });
  }

  ngAfterViewInit(): void {
    // Initial fetch is kicked off by the effect above; nothing to do here today.
  }

  ngOnDestroy(): void {
    this.aborter?.abort();
    this.aborter = null;
    this.monacoEditor?.dispose();
    this.monacoEditor = null;
  }

  protected onClose(): void {
    this.closed.emit();
  }

  private async fetchAndRender(conversationId: string, eventId: string): Promise<void> {
    this.aborter?.abort();
    const aborter = new AbortController();
    this.aborter = aborter;

    let response: Response;
    try {
      response = await fetch(
        `/api/assistant/conversations/${encodeURIComponent(conversationId)}/artifacts/${encodeURIComponent(eventId)}`,
        {
          credentials: 'include',
          signal: aborter.signal,
        },
      );
    } catch (e) {
      if (aborter.signal.aborted) return;
      this.loadState.set('error');
      this.errorMessage.set(e instanceof Error ? e.message : 'Network error.');
      return;
    }

    if (response.status === 410) {
      this.loadState.set('expired');
      this.expired.emit();
      return;
    }
    if (!response.ok) {
      this.loadState.set('error');
      this.errorMessage.set(`Server returned ${response.status}.`);
      return;
    }

    let text: string;
    try {
      text = await response.text();
    } catch (e) {
      if (aborter.signal.aborted) return;
      this.loadState.set('error');
      this.errorMessage.set(e instanceof Error ? e.message : 'Failed to read response body.');
      return;
    }

    if (aborter.signal.aborted) return;

    // Pretty-print JSON for the package kinds — the on-disk bytes are minified by the
    // recorder. If parsing fails (corrupted file?) fall back to the raw bytes.
    let formatted = text;
    try {
      formatted = JSON.stringify(JSON.parse(text), null, 2);
    } catch {
      // Leave as-is; Monaco still renders plain text fine.
    }

    try {
      ensureMonacoEnvironment();
      await ensureMonacoEditorStyles();
      const monaco = await loadMonacoEditor();
      if (aborter.signal.aborted) return;

      this.monacoEditor?.dispose();
      this.monacoEditor = monaco.editor.create(this.monacoHost.nativeElement, {
        value: formatted,
        language: 'json',
        readOnly: true,
        domReadOnly: true,
        automaticLayout: true,
        minimap: { enabled: false },
        scrollBeyondLastLine: false,
        wordWrap: 'on',
        renderLineHighlight: 'none',
      });
      this.loadState.set('ready');
    } catch (e) {
      if (aborter.signal.aborted) return;
      this.loadState.set('error');
      this.errorMessage.set(e instanceof Error ? e.message : 'Failed to load editor.');
    }
  }
}
