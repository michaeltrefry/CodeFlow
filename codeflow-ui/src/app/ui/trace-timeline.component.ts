import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  Output,
  computed,
  signal,
} from '@angular/core';
import { CommonModule, DatePipe, JsonPipe } from '@angular/common';
import { Observable, finalize } from 'rxjs';
import { ChipComponent } from './chip.component';
import { IconComponent, IconName } from './icon.component';
import {
  TraceTimelineDotState,
  TraceTimelineEvent,
  TraceTimelineExtraLink,
  dotStateFor,
  variantForKind,
} from './trace-timeline.types';

interface ArtifactLoadState {
  loading: boolean;
  content?: string;
  error?: string;
}

/**
 * Shared trace-timeline renderer used by both the saga trace-detail page and the
 * dry-run page. Each {@link TraceTimelineEvent} renders as one vertical row with a
 * status dot, title chips, optional message, and an expand affordance for inline
 * input/output previews or ref-based artifact lazy loads.
 *
 * Single chip-variant map ({@link variantForKind}) means new event kinds added to
 * either surface immediately get a sensible color on both.
 */
@Component({
  selector: 'cf-trace-timeline',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, DatePipe, JsonPipe, ChipComponent, IconComponent],
  template: `
    <div class="timeline">
      @for (entry of events; track entry.id) {
        <div class="tl-step" [attr.data-state]="dotState(entry)">
          <div class="tl-dot">
            @switch (dotIcon(entry)) {
              @case ('check') { <cf-icon name="check"></cf-icon> }
              @case ('x')     { <cf-icon name="x"></cf-icon> }
              @case ('hitl')  { <cf-icon name="hitl"></cf-icon> }
              @case ('play')  { <cf-icon name="play"></cf-icon> }
              @case ('alert') { <cf-icon name="alert"></cf-icon> }
              @default        { <cf-icon name="chevR"></cf-icon> }
            }
          </div>
          <div class="tl-body">
            <button
              type="button"
              class="timeline-toggle"
              (click)="toggle(entry)"
              [disabled]="!isExpandable(entry)"
              [attr.aria-expanded]="isExpanded(entry.id)">
              <div class="tl-title">
                @if (entry.ordinal != null) {
                  <cf-chip mono>#{{ entry.ordinal }}</cf-chip>
                }
                <span class="tl-agent">{{ entry.title }}</span>
                @if (entry.decision) {
                  <cf-chip [variant]="variantForKind(entry.decision)" dot>{{ entry.decision }}</cf-chip>
                } @else {
                  <cf-chip [variant]="variantForKind(entry.kind)" mono>{{ entry.kind }}</cf-chip>
                }
                @if (entry.reviewRound != null && entry.maxRounds != null) {
                  <cf-chip mono>round {{ entry.reviewRound }}/{{ entry.maxRounds }}</cf-chip>
                }
                @for (badge of entry.badges; track badge.label) {
                  <cf-chip
                    [variant]="badge.variant ?? 'default'"
                    [mono]="badge.mono ?? false"
                    [dot]="badge.dot ?? false"
                    [title]="badge.title ?? ''">{{ badge.label }}</cf-chip>
                }
                @if (isExpandable(entry)) {
                  <span class="caret">{{ isExpanded(entry.id) ? '▾' : '▸' }}</span>
                }
              </div>
              @if (entry.message) {
                <div class="tl-desc">{{ entry.message }}</div>
              }
            </button>

            @if (isExpanded(entry.id)) {
              <div class="tl-expand">
                @for (extra of entry.expandedExtras; track extra.ref) {
                  <p class="artifact-link-row">
                    <a class="mono-link" href="" (click)="onExtraLinkClick($event, extra)">{{ extra.label }} →</a>
                  </p>
                }
                @if (hasInput(entry)) {
                  <div class="artifact-block">
                    <div class="artifact-h">Input</div>
                    @if (entry.inputRef) {
                      <p class="artifact-link-row">
                        <a class="mono-link" href="" (click)="onDownload($event, entry.inputRef!)">Download input artifact</a>
                      </p>
                      @if (refContent(entry.inputRef); as state) {
                        @if (state.loading) { <p class="muted small">Loading…</p> }
                        @else if (state.error) { <cf-chip variant="err" dot>{{ state.error }}</cf-chip> }
                        @else if (state.content !== undefined) { <pre class="tl-payload">{{ state.content }}</pre> }
                      }
                    } @else {
                      <pre class="tl-payload">{{ entry.inputPreview }}</pre>
                    }
                  </div>
                }
                @if (hasOutput(entry)) {
                  <div class="artifact-block">
                    <div class="artifact-h">Output</div>
                    @if (entry.outputRef) {
                      @if (refContent(entry.outputRef); as state) {
                        @if (state.loading) { <p class="muted small">Loading…</p> }
                        @else if (state.error) { <cf-chip variant="err" dot>{{ state.error }}</cf-chip> }
                        @else if (state.content !== undefined) { <pre class="tl-payload">{{ state.content }}</pre> }
                      }
                    } @else {
                      <pre class="tl-payload">{{ entry.outputPreview }}</pre>
                    }
                  </div>
                }
                @if (entry.logs && entry.logs.length > 0) {
                  <div class="artifact-block">
                    <div class="artifact-h">Logs ({{ entry.logs.length }})</div>
                    <pre class="tl-payload">{{ entry.logs.join('\n') }}</pre>
                  </div>
                }
                @if (hasDecisionPayload(entry)) {
                  <div class="artifact-block">
                    <div class="artifact-h">Decision payload</div>
                    <pre class="tl-payload">{{ entry.decisionPayload | json }}</pre>
                  </div>
                }
              </div>
            }
          </div>
          @if (entry.timestampUtc) {
            <div class="tl-when">{{ entry.timestampUtc | date:'mediumTime' }}</div>
          } @else {
            <div class="tl-when"></div>
          }
        </div>
      }
    </div>
  `,
  styles: [`
    .timeline-toggle {
      all: unset;
      display: block;
      width: 100%;
      cursor: default;
    }
    .timeline-toggle:disabled { cursor: default; }
    .timeline-toggle:focus-visible {
      outline: none;
      box-shadow: var(--focus-ring);
      border-radius: var(--radius);
    }
    .caret { margin-left: 6px; color: var(--muted); font-size: var(--fs-sm); }
    .tl-expand { margin-top: 8px; display: flex; flex-direction: column; gap: 10px; }
    .artifact-block .artifact-h {
      font-size: var(--fs-xs);
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: var(--faint);
      margin-bottom: 4px;
    }
    .artifact-link-row { margin: 0 0 6px; font-size: var(--fs-sm); }
  `],
})
export class TraceTimelineComponent {
  @Input({ required: true }) events: TraceTimelineEvent[] = [];
  /**
   * Optional fetcher for ref-based artifact content. When set and a row exposes an
   * `inputRef` / `outputRef`, the row triggers `fetchArtifact(uri)` on first expand
   * and renders the streamed text. When unset, ref-based rows fall back to inline
   * `inputPreview` / `outputPreview` (or render nothing).
   */
  @Input() fetchArtifact: ((uri: string) => Observable<string>) | null = null;

  /**
   * Emits the artifact URI when an extra-link row (or the saga-only "download input
   * artifact" link) is activated. Parents handle the actual download via their own
   * trace API.
   */
  @Output() downloadRef = new EventEmitter<string>();

  // The parent doesn't need to manage expansion state; the timeline owns it locally.
  private readonly expanded = signal<Set<string>>(new Set());
  private readonly artifacts = signal<Map<string, ArtifactLoadState>>(new Map());

  readonly variantForKind = variantForKind;

  isExpanded(id: string): boolean {
    return this.expanded().has(id);
  }

  isExpandable(entry: TraceTimelineEvent): boolean {
    if (entry.expandable === false) return false;
    return (
      this.hasInput(entry)
      || this.hasOutput(entry)
      || (entry.logs?.length ?? 0) > 0
      || this.hasDecisionPayload(entry)
      || (entry.expandedExtras?.length ?? 0) > 0
    );
  }

  hasInput(entry: TraceTimelineEvent): boolean {
    return !!entry.inputRef || !!entry.inputPreview;
  }

  hasOutput(entry: TraceTimelineEvent): boolean {
    return !!entry.outputRef || !!entry.outputPreview;
  }

  hasDecisionPayload(entry: TraceTimelineEvent): boolean {
    return entry.decisionPayload !== undefined && entry.decisionPayload !== null;
  }

  toggle(entry: TraceTimelineEvent): void {
    if (!this.isExpandable(entry)) return;
    const next = new Set(this.expanded());
    if (next.has(entry.id)) {
      next.delete(entry.id);
    } else {
      next.add(entry.id);
      // Lazy-fetch any ref-based content the first time the row opens.
      if (entry.inputRef) this.loadArtifact(entry.inputRef);
      if (entry.outputRef) this.loadArtifact(entry.outputRef);
    }
    this.expanded.set(next);
  }

  refContent(uri: string | null | undefined): ArtifactLoadState | undefined {
    if (!uri) return undefined;
    return this.artifacts().get(uri);
  }

  dotState(entry: TraceTimelineEvent): TraceTimelineDotState {
    return dotStateFor(entry);
  }

  dotIcon(entry: TraceTimelineEvent): IconName {
    if (entry.dotIcon) return entry.dotIcon;
    const state = this.dotState(entry);
    switch (state) {
      case 'ok': return 'check';
      case 'err': return 'x';
      case 'warn': return 'alert';
      case 'run': return 'play';
      case 'hitl': return 'hitl';
      default: return 'chevR';
    }
  }

  onDownload(event: Event, ref: string): void {
    event.preventDefault();
    this.downloadRef.emit(ref);
  }

  onExtraLinkClick(event: Event, extra: TraceTimelineExtraLink): void {
    event.preventDefault();
    this.downloadRef.emit(extra.ref);
  }

  private loadArtifact(uri: string): void {
    if (!this.fetchArtifact) return;
    const existing = this.artifacts().get(uri);
    if (existing && (existing.content !== undefined || existing.loading)) return;

    const next = new Map(this.artifacts());
    next.set(uri, { loading: true });
    this.artifacts.set(next);

    this.fetchArtifact(uri).pipe(
      finalize(() => {
        const map = this.artifacts();
        if (map.get(uri)?.loading) {
          const m = new Map(map);
          m.set(uri, { loading: false });
          this.artifacts.set(m);
        }
      }),
    ).subscribe({
      next: content => {
        const m = new Map(this.artifacts());
        m.set(uri, { loading: false, content });
        this.artifacts.set(m);
      },
      error: err => {
        const m = new Map(this.artifacts());
        m.set(uri, { loading: false, error: err?.message ?? 'Failed to load artifact.' });
        this.artifacts.set(m);
      },
    });
  }
}
