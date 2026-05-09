import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Output,
  computed,
  input,
  signal,
} from '@angular/core';
import { ArtifactEventView } from './chat-panel.component';

/**
 * sc-796 (AA-5): pinned artifact rail above the composer. Always-visible surface for the
 * conversation's artifact events, independent of how far the user has scrolled the thread.
 *
 * Behavior:
 * - Lists all non-superseded events newest-first by sequence.
 * - When count > {@link COLLAPSE_THRESHOLD}, collapses to a single chip ("N artifacts");
 *   click expands.
 * - Toggle "Show superseded" reveals tombstoned events muted-strikethrough.
 * - Per-row: kind icon, name, relative age, Download (`<a download>`), View (emits to parent
 *   so the existing Monaco side sheet can be reused). Expired rows swap actions for an
 *   "Expired" status pill.
 *
 * The rail does NOT host its own Monaco preview — `viewRequested` bubbles to the chat panel
 * which already owns the side sheet for inline pills (AA-4).
 */
@Component({
  selector: 'cf-artifact-rail',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (visibleEvents().length > 0 || hasSuperseded()) {
      <div class="artifact-rail" data-testid="artifact-rail">
        @if (showCollapsedChip()) {
          <button
            type="button"
            class="artifact-rail-collapsed"
            data-testid="artifact-rail-collapsed-toggle"
            (click)="expand()"
          >
            <span class="artifact-rail-collapsed-icon" aria-hidden="true">⎘</span>
            <span>{{ activeCount() }} artifact{{ activeCount() === 1 ? '' : 's' }}</span>
            <span class="artifact-rail-collapsed-hint">Show</span>
          </button>
        } @else {
          <div class="artifact-rail-head">
            <span class="artifact-rail-title">Artifacts</span>
            @if (hasSuperseded()) {
              <button
                type="button"
                class="artifact-rail-toggle"
                data-testid="artifact-rail-show-superseded"
                (click)="toggleShowSuperseded()"
              >{{ showSuperseded() ? 'Hide superseded' : 'Show superseded' }}</button>
            }
            @if (activeCount() > collapseThreshold) {
              <button
                type="button"
                class="artifact-rail-toggle"
                data-testid="artifact-rail-collapse"
                (click)="collapse()"
              >Collapse</button>
            }
          </div>
          <ul class="artifact-rail-list">
            @for (e of visibleEvents(); track e.id) {
              <li
                class="artifact-rail-row"
                [attr.data-state]="e.superseded ? 'superseded' : (e.expired ? 'expired' : 'active')"
                [attr.data-artifact-id]="e.id"
                data-testid="artifact-rail-row"
              >
                <span class="artifact-rail-icon" aria-hidden="true">⎘</span>
                <span class="artifact-rail-body">
                  <span class="artifact-rail-name">{{ e.name }}</span>
                  <span class="artifact-rail-meta">
                    <span class="artifact-rail-kind">{{ e.artifactKind }}</span>
                    <span class="artifact-rail-age">{{ formatArtifactAge(e.createdAtUtc, now()) }}</span>
                  </span>
                </span>
                <span class="artifact-rail-actions">
                  @if (e.expired) {
                    <span class="artifact-rail-status">Expired</span>
                  } @else {
                    <button
                      type="button"
                      class="artifact-rail-action"
                      (click)="onDownload(e)"
                    >Download</button>
                    <button
                      type="button"
                      class="artifact-rail-action"
                      (click)="onView(e)"
                    >View</button>
                    @if (isPackageKind(e)) {
                      <button
                        type="button"
                        class="artifact-rail-action"
                        data-testid="artifact-rail-diff"
                        (click)="onDiff(e)"
                      >Diff</button>
                    }
                    @if (isPackageKind(e) && !e.superseded) {
                      <button
                        type="button"
                        class="artifact-rail-action artifact-rail-action-primary"
                        data-testid="artifact-rail-save"
                        (click)="onSave(e)"
                      >Save to library</button>
                    }
                  }
                  <button
                    type="button"
                    class="artifact-rail-dismiss"
                    title="Hide from rail"
                    aria-label="Dismiss artifact"
                    data-testid="artifact-rail-dismiss"
                    (click)="onDismiss(e)"
                  >×</button>
                </span>
              </li>
            }
          </ul>
        }
      </div>
    }
  `,
  styles: [`
    :host { display: block; }
    .artifact-rail {
      flex: 0 0 auto;
      margin: 8px 12px 0;
      padding: 6px 10px;
      border: 1px solid var(--border, rgba(255,255,255,0.12));
      border-radius: var(--radius-sm, 6px);
      background: color-mix(in oklab, var(--accent, #5765ff) 4%, var(--surface, #131519));
    }
    .artifact-rail-collapsed {
      display: flex;
      align-items: center;
      gap: 8px;
      width: 100%;
      padding: 4px 6px;
      appearance: none;
      cursor: pointer;
      background: transparent;
      border: none;
      color: var(--text, #E7E9EE);
      font-size: 12px;
    }
    .artifact-rail-collapsed-icon { color: var(--accent, #5765ff); }
    .artifact-rail-collapsed-hint {
      margin-left: auto;
      color: var(--text-muted, #9aa3b2);
      font-size: 11px;
    }
    .artifact-rail-head {
      display: flex;
      align-items: center;
      gap: 8px;
      padding-bottom: 4px;
      border-bottom: 1px solid var(--border, rgba(255,255,255,0.06));
      margin-bottom: 4px;
    }
    .artifact-rail-title {
      font-size: 11px;
      font-weight: 600;
      color: var(--text-muted, #9aa3b2);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .artifact-rail-toggle {
      appearance: none;
      cursor: pointer;
      background: transparent;
      border: none;
      color: var(--text-muted, #9aa3b2);
      font-size: 11px;
      padding: 0 4px;
    }
    .artifact-rail-toggle + .artifact-rail-toggle {
      border-left: 1px solid var(--border, rgba(255,255,255,0.12));
    }
    .artifact-rail-toggle:hover { color: var(--text, #E7E9EE); }
    .artifact-rail-list {
      list-style: none;
      margin: 0;
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .artifact-rail-row {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 4px 6px;
      border-radius: 4px;
      font-size: 12px;
      color: var(--text, #E7E9EE);
    }
    .artifact-rail-row[data-state='superseded'] {
      opacity: 0.5;
      text-decoration: line-through;
      text-decoration-color: var(--text-muted, #9aa3b2);
    }
    .artifact-rail-row[data-state='expired'] {
      opacity: 0.4;
      filter: grayscale(0.4);
    }
    .artifact-rail-icon { color: var(--accent, #5765ff); font-size: 13px; }
    .artifact-rail-body {
      display: flex;
      flex-direction: column;
      gap: 1px;
      flex: 1 1 auto;
      min-width: 0;
    }
    .artifact-rail-name {
      font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace);
      font-size: 11px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .artifact-rail-meta {
      display: flex;
      gap: 6px;
      font-size: 10px;
      color: var(--text-muted, #9aa3b2);
    }
    .artifact-rail-kind { font-weight: 600; }
    .artifact-rail-actions {
      display: flex;
      gap: 4px;
      flex: 0 0 auto;
    }
    .artifact-rail-action {
      appearance: none;
      cursor: pointer;
      font-size: 11px;
      padding: 2px 8px;
      border-radius: 3px;
      border: 1px solid var(--border, rgba(255,255,255,0.16));
      background: transparent;
      color: var(--text, #E7E9EE);
      text-decoration: none;
      display: inline-flex;
      align-items: center;
      line-height: 1;
    }
    .artifact-rail-action:hover {
      background: color-mix(in oklab, var(--accent, #5765ff) 12%, transparent);
    }
    .artifact-rail-action-primary {
      border-color: var(--accent, #5765ff);
      color: var(--accent, #5765ff);
    }
    .artifact-rail-action-primary:hover {
      background: color-mix(in oklab, var(--accent, #5765ff) 18%, transparent);
    }
    .artifact-rail-status {
      font-size: 11px;
      color: var(--warn, #f5a623);
      padding: 2px 8px;
    }
    .artifact-rail-dismiss {
      appearance: none;
      cursor: pointer;
      font-size: 14px;
      line-height: 1;
      padding: 0 4px;
      margin-left: 2px;
      border: none;
      background: transparent;
      color: var(--text-muted, #9aa3b2);
    }
    .artifact-rail-dismiss:hover {
      color: var(--text, #E7E9EE);
    }
  `],
})
export class ArtifactRailComponent {
  /** All artifact events for the conversation. The component filters by superseded/expired internally. */
  readonly events = input.required<ArtifactEventView[]>();
  /** Conversation id used to compose the download URL. */
  readonly conversationId = input.required<string>();
  /**
   * Anchor for relative-age computation. Tests can pin it for determinism; in production the
   * default `Date.now()` re-evaluates per change-detection cycle (the input is reactive).
   */
  readonly nowMs = input<number>(Date.now());

  /** Bubbles up so the chat panel can mount its existing Monaco side sheet. */
  @Output() readonly viewRequested = new EventEmitter<ArtifactEventView>();
  /**
   * sc-797 (AA-6): bubbles up "Save to library" intent so the chat panel can run the
   * preview-then-confirm flow through its existing chip infrastructure. The rail itself
   * does NOT call the save API directly — it stays a pure presentation component.
   */
  @Output() readonly saveRequested = new EventEmitter<ArtifactEventView>();
  /**
   * sc-798 (AA-7): bubbles up "Diff" intent. The chat panel computes the prior superseded
   * event + the entry-point key from the package summary and mounts the diff side sheet.
   */
  @Output() readonly diffRequested = new EventEmitter<ArtifactEventView>();
  /**
   * Download bubbles up so the chat panel can JS-drive the save-as via Angular HttpClient.
   * Bubbling instead of inlining `<a download>` because the production auth is JwtBearer
   * only — a plain link request comes in unauthenticated and the download endpoint 404s.
   */
  @Output() readonly downloadRequested = new EventEmitter<ArtifactEventView>();
  /**
   * Bubbles up "hide from rail" intent. Distinct from the inline pill's dismiss (which only
   * affects the in-thread surface): rail dismiss is the user saying "I'm done with this
   * artifact, stop pinning it." The chat panel keeps the dismissal in a per-conversation set
   * so it survives re-renders but resets on conversation change.
   */
  @Output() readonly dismissRequested = new EventEmitter<ArtifactEventView>();

  protected readonly collapseThreshold = COLLAPSE_THRESHOLD;
  protected readonly showSuperseded = signal<boolean>(false);
  protected readonly collapsed = signal<boolean>(false);

  protected readonly activeCount = computed(() =>
    this.events().filter(e => !e.superseded).length);

  protected readonly hasSuperseded = computed(() =>
    this.events().some(e => e.superseded));

  protected readonly visibleEvents = computed(() =>
    filterRailEvents(this.events(), this.showSuperseded()));

  protected readonly showCollapsedChip = computed(() =>
    this.collapsed() && this.activeCount() > this.collapseThreshold);

  protected readonly now = computed(() => this.nowMs());

  protected toggleShowSuperseded(): void {
    this.showSuperseded.update(v => !v);
  }

  protected expand(): void {
    this.collapsed.set(false);
  }

  protected collapse(): void {
    this.collapsed.set(true);
  }

  protected onDownload(view: ArtifactEventView): void {
    this.downloadRequested.emit(view);
  }

  protected onView(view: ArtifactEventView): void {
    this.viewRequested.emit(view);
  }

  protected onSave(view: ArtifactEventView): void {
    this.saveRequested.emit(view);
  }

  protected onDiff(view: ArtifactEventView): void {
    this.diffRequested.emit(view);
  }

  protected onDismiss(view: ArtifactEventView): void {
    this.dismissRequested.emit(view);
  }

  /** sc-797 (AA-6): only package-kind artifacts are saveable today. AA-9 (Phase 3) widens
   *  this for trace-diagnostic + evidence-bundle producers (which have nothing to save). */
  protected isPackageKind(view: ArtifactEventView): boolean {
    return isPackageArtifactKind(view.artifactKind);
  }

  protected formatArtifactAge(iso: string, now: number): string {
    return formatArtifactAge(iso, now);
  }
}

/** Default collapse threshold — exported so the chat panel + tests can reference it. */
export const COLLAPSE_THRESHOLD = 4;

/** AP-8 (sc-839): which artifact kinds get the rail's Diff + Save buttons. Workflow
 *  package kinds (sc-797 AA-6) and agent-package kinds (AP-3) qualify; trace-diagnostic
 *  and evidence-bundle kinds don't (AA-9 reserved them but they have nothing to save).
 *  Pure function so the rail spec can exercise the predicate without mounting the
 *  component. The Save-from-rail handoff routes through the imports page, which dispatches
 *  on `schemaVersion` (AP-7) — both kinds end up on the right backend automatically. */
export function isPackageArtifactKind(kind: string): boolean {
  return kind === 'WorkflowPackageDraft'
    || kind === 'WorkflowPackageSnapshot'
    || kind === 'AgentPackageDraft'
    || kind === 'AgentPackageSnapshot';
}

/**
 * sc-796 (AA-5): pure filter for the rail's visible events. Newest-first by sequence,
 * superseded entries hidden unless `showSuperseded` is true. Exported for unit testing.
 */
export function filterRailEvents(
  events: ArtifactEventView[],
  showSuperseded: boolean,
): ArtifactEventView[] {
  const filtered = showSuperseded
    ? events.slice()
    : events.filter(e => !e.superseded);
  filtered.sort((a, b) => b.sequence - a.sequence);
  return filtered;
}

/**
 * sc-796 (AA-5): one-line relative-age string for the rail. Coarse buckets — the rail is a
 * scan surface, not a clock. Returns 'just now' under a minute, 'Nm' / 'Nh' / 'Nd' otherwise.
 */
export function formatArtifactAge(iso: string, nowMs: number): string {
  const ts = Date.parse(iso);
  if (Number.isNaN(ts)) return '';
  const deltaMs = Math.max(0, nowMs - ts);
  const seconds = Math.floor(deltaMs / 1000);
  if (seconds < 60) return 'just now';
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h`;
  const days = Math.floor(hours / 24);
  return `${days}d`;
}
