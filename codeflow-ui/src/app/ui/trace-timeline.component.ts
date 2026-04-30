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
  TraceTimelineTokenUsage,
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
                @if (entry.tokenUsage; as tu) {
                  <cf-chip mono variant="accent"
                           [title]="tokenUsageHoverSummary(tu)">{{ tokenUsageInlineLabel(tu) }}</cf-chip>
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
                      <p class="artifact-link-row">
                        <a class="mono-link" href="" (click)="onDownload($event, entry.outputRef!)">Download output artifact</a>
                      </p>
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
                @if (entry.tokenUsage; as tu) {
                  <div class="artifact-block">
                    <div class="artifact-h">
                      Token usage · {{ tu.callCount }} {{ tu.callCount === 1 ? 'call' : 'calls' }}
                    </div>
                    <ul class="tl-token-totals">
                      @for (pair of tokenUsageTotalsAsPairs(tu.totals); track pair.key) {
                        <li>
                          <span class="mono xsmall faint">{{ pair.key }}</span>
                          <span class="mono small">{{ pair.value | number }}</span>
                        </li>
                      }
                    </ul>
                    @if (tu.byProviderModel.length > 1) {
                      <div class="tl-token-combos">
                        @for (combo of tu.byProviderModel; track combo.provider + combo.model) {
                          <div class="tl-token-combo">
                            <cf-chip mono>{{ combo.provider }} · {{ combo.model }}</cf-chip>
                            <ul class="tl-token-totals nested">
                              @for (pair of tokenUsageTotalsAsPairs(combo.totals); track pair.key) {
                                <li>
                                  <span class="mono xsmall faint">{{ pair.key }}</span>
                                  <span class="mono xsmall">{{ pair.value | number }}</span>
                                </li>
                              }
                            </ul>
                          </div>
                        }
                      </div>
                    }
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
    .tl-token-totals {
      list-style: none;
      margin: 0;
      padding: 0;
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
      gap: 2px 14px;
    }
    .tl-token-totals li { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; }
    .tl-token-totals.nested { margin-top: 4px; }
    .tl-token-combos {
      margin-top: 8px;
      padding-top: 8px;
      border-top: 1px dashed var(--border);
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .tl-token-combo > cf-chip { margin-bottom: 4px; display: inline-block; }
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
      || !!entry.tokenUsage
    );
  }

  /** Slice 8 inline-badge label: compact "↑input · ↓output · Nc" form so the row
   *  can show usage at a glance without overwhelming the title bar. Falls back to
   *  the call count when neither input nor output tokens are reported. */
  tokenUsageInlineLabel(tu: TraceTimelineTokenUsage): string {
    const input = tu.totals['input_tokens'] ?? tu.totals['prompt_tokens'] ?? 0;
    const output = tu.totals['output_tokens'] ?? tu.totals['completion_tokens'] ?? 0;
    const callSuffix = tu.callCount > 1 ? ` · ${tu.callCount}c` : '';
    if (input === 0 && output === 0) {
      return `${tu.callCount} ${tu.callCount === 1 ? 'call' : 'calls'}`;
    }
    return `↑${formatCount(input)} · ↓${formatCount(output)}${callSuffix}`;
  }

  /** Hover/title attribute for the inline chip — full input/output and call count
   *  in a single line so the user can see exact numbers without expanding. */
  tokenUsageHoverSummary(tu: TraceTimelineTokenUsage): string {
    const input = tu.totals['input_tokens'] ?? tu.totals['prompt_tokens'] ?? 0;
    const output = tu.totals['output_tokens'] ?? tu.totals['completion_tokens'] ?? 0;
    return `${input.toLocaleString()} input · ${output.toLocaleString()} output · ${tu.callCount} ${tu.callCount === 1 ? 'call' : 'calls'}`;
  }

  /** Stable display order for token totals — input fields first, then output,
   *  total_tokens, cache fields, reasoning fields, then alphabetical. Mirrors the
   *  slice 6 panel's ordering so the same field list renders consistently in
   *  both surfaces. */
  tokenUsageTotalsAsPairs(totals: Record<string, number>): { key: string; value: number }[] {
    const pairs = Object.entries(totals).map(([key, value]) => ({ key, value }));
    pairs.sort((a, b) => fieldSortKey(a.key) - fieldSortKey(b.key) || a.key.localeCompare(b.key));
    return pairs;
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

  private formatCount(value: number): string {
    return formatCount(value);
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

/** Compact integer formatter for inline badges. Keeps small counts exact and
 *  prefixes at thousand boundaries so 12,345 renders as "12.3k". The expanded
 *  block always shows full precision via the `number` pipe — this is a
 *  display-only optimization for the at-a-glance chip. */
function formatCount(value: number): string {
  if (!Number.isFinite(value) || value === 0) return '0';
  const abs = Math.abs(value);
  if (abs < 1000) return String(value);
  if (abs < 1_000_000) return `${(value / 1000).toFixed(value >= 10_000 ? 0 : 1)}k`;
  return `${(value / 1_000_000).toFixed(1)}M`;
}

/** Lower = sorted earlier (mirrors token-usage-panel.component.ts). */
function fieldSortKey(field: string): number {
  if (field.startsWith('input_tokens') || field === 'prompt_tokens') return 0;
  if (field.startsWith('output_tokens') || field === 'completion_tokens') return 1;
  if (field === 'total_tokens') return 2;
  if (field.includes('cache')) return 3;
  if (field.includes('reasoning') || field.includes('thinking')) return 4;
  return 5;
}
