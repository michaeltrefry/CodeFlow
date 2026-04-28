import { ChipVariant } from './chip.component';
import { IconName } from './icon.component';

/**
 * One row rendered by the shared `<cf-trace-timeline>` component. Both saga-trace
 * decisions and DryRunExecutor events map onto this shape so the two surfaces share a
 * single chip palette, layout, and expand affordance.
 */
export interface TraceTimelineEvent {
  /** Stable unique id for tracking + expansion state. */
  id: string;
  /** Optional sequence index — shown as a small "#N" chip on dry-run events. */
  ordinal?: number | null;
  /** Event/decision kind — drives the chip variant via {@link variantForKind}. */
  kind: string;
  /**
   * Optional decision label (saga emits "Approved/Rejected/Failed/Completed"). When
   * present, the timeline shows it as the highlighted chip in place of `kind`.
   */
  decision?: string | null;
  /** Display name for the timeline title (agent key, node label, etc.). */
  title: string;
  /** Extra small chips after the title (port name, version, origin path, …). */
  badges?: TraceTimelineBadge[];
  /** Free-form message shown beneath the title in muted text. */
  message?: string | null;
  /** Inline preview content — already rendered text, no fetch needed. */
  inputPreview?: string | null;
  outputPreview?: string | null;
  /** Or refs that need fetching via the parent's `fetchArtifact` callback. */
  inputRef?: string | null;
  outputRef?: string | null;
  /** Optional structured payload to display when expanded. */
  decisionPayload?: unknown;
  /** Optional log lines to surface in a collapsible block. */
  logs?: string[] | null;
  /** Optional review-round indicator. Both must be present to render "N/M". */
  reviewRound?: number | null;
  maxRounds?: number | null;
  /** Optional ISO timestamp shown on the right of the row. */
  timestampUtc?: string | null;
  /** Override the auto-derived dot icon (defaults: ok→check, err→x, run→play, hitl→hitl, else→chevR). */
  dotIcon?: IconName | null;
  /** Override the auto-derived dot state (drives color via [data-state]). */
  dotState?: TraceTimelineDotState;
  /** Extra header rows in the expanded section (saga's HTTP-diagnostics download links). */
  expandedExtras?: TraceTimelineExtraLink[];
  /** When false, the row's expand affordance is hidden even if there's content to expand. */
  expandable?: boolean;
  /**
   * Token Usage Tracking [Slice 8]: per-row token-usage summary. The trace detail
   * page populates this by claiming records from the slice 6 aggregator that
   * belong to this row's node + invocation window. When present, the timeline
   * renders an inline badge in the title and a full breakdown in the expanded
   * section. Rows without an associated LLM call leave this null.
   */
  tokenUsage?: TraceTimelineTokenUsage | null;
}

/**
 * Compact representation of token usage matched to a single timeline row. Mirrors
 * the slice-5 `TokenUsageRollup` server contract but only carries the numbers the
 * timeline needs to render — `totals` is the same flattened-dotted-path format the
 * rest of the token-tracking surfaces use.
 */
export interface TraceTimelineTokenUsage {
  /** How many LLM calls fed into this row's totals (>=1 when present). */
  callCount: number;
  /** Flattened sum of every numeric leaf, keyed by dotted JSON path. */
  totals: Record<string, number>;
  /** Per-(provider, model) breakdown — populated when more than one combo
   *  contributed to this row's window. Empty when there's only one combo. */
  byProviderModel: Array<{
    provider: string;
    model: string;
    totals: Record<string, number>;
  }>;
}

export interface TraceTimelineBadge {
  label: string;
  variant?: ChipVariant;
  mono?: boolean;
  dot?: boolean;
  title?: string;
}

export interface TraceTimelineExtraLink {
  /** Visible link text. */
  label: string;
  /** Artifact URI passed back to the parent's `downloadArtifact` callback. */
  ref: string;
}

export type TraceTimelineDotState = 'ok' | 'err' | 'warn' | 'run' | 'hitl' | '';

/**
 * Single source of truth for chip-variant mapping across both surfaces. Covers saga
 * decision strings and DryRunExecutor event kinds. Unknown kinds fall back to
 * `default` via {@link variantForKind}.
 */
export const TRACE_KIND_VARIANTS: Record<string, ChipVariant> = {
  // Saga decisions
  Approved: 'ok',
  Completed: 'ok',
  Rejected: 'err',
  Failed: 'err',
  // Saga timeline kinds
  Requested: 'accent',
  // Dry-run event kinds (DryRunEventKind)
  NodeEntered: 'default',
  AgentMockApplied: 'ok',
  LogicEvaluated: 'default',
  HitlSuspended: 'warn',
  EdgeTraversed: 'default',
  SubflowEntered: 'accent',
  SubflowExited: 'accent',
  LoopIteration: 'accent',
  LoopExhausted: 'warn',
  WorkflowCompleted: 'ok',
  WorkflowFailed: 'err',
  StepLimitExceeded: 'err',
  BuiltinApplied: 'accent',
  Diagnostic: 'default',
  RetryContextHandoff: 'warn',
};

export function variantForKind(kind: string | null | undefined): ChipVariant {
  if (!kind) return 'default';
  return TRACE_KIND_VARIANTS[kind] ?? 'default';
}

const DECISION_TO_DOT_STATE: Record<string, TraceTimelineDotState> = {
  Completed: 'ok',
  Approved: 'ok',
  Failed: 'err',
  Rejected: 'err',
};

const KIND_TO_DOT_STATE: Record<string, TraceTimelineDotState> = {
  Requested: 'run',
  HitlSuspended: 'hitl',
  WorkflowFailed: 'err',
  StepLimitExceeded: 'err',
  WorkflowCompleted: 'ok',
  AgentMockApplied: 'ok',
  LoopExhausted: 'warn',
  RetryContextHandoff: 'warn',
};

/** Default dot color (and icon) lookup applied when a row doesn't override `dotState`. */
export function dotStateFor(event: TraceTimelineEvent): TraceTimelineDotState {
  if (event.dotState !== undefined) return event.dotState;
  if (event.decision && DECISION_TO_DOT_STATE[event.decision]) {
    return DECISION_TO_DOT_STATE[event.decision];
  }
  return KIND_TO_DOT_STATE[event.kind] ?? '';
}
