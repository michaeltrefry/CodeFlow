import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { formatHttpError } from '../../core/format-error';
import { TracesApi } from '../../core/traces.api';
import {
  RecordedDecisionRef,
  ReplayDriftLevel,
  ReplayEdit,
  ReplayEvent,
  ReplayResponse,
  WorkflowDetail,
} from '../../core/models';
import { ButtonComponent } from '../../ui/button.component';
import { CardComponent } from '../../ui/card.component';
import { ChipComponent } from '../../ui/chip.component';
import { TraceTimelineComponent } from '../../ui/trace-timeline.component';
import { TraceTimelineEvent } from '../../ui/trace-timeline.types';

interface EditRowState {
  decision: RecordedDecisionRef;
  newDecision: string;       // blank string = "leave as-is"
  newOutput: string;         // blank string = "leave as-is"
  outputDirty: boolean;      // distinguishes blank-by-author from blank-by-default
}

/**
 * Replay-with-edit panel mounted inline on the trace-detail page for terminal traces.
 *
 * Workflow:
 * 1. On open, fetch the recorded-decision list + a "no-edits" baseline replay via
 *    POST /api/traces/{id}/replay with an empty body. The baseline response also acts as the
 *    "Original" timeline so the side-by-side comparison is apples-to-apples.
 * 2. Author flips entries in the edit table (per-agent ordinal addresses individual recordings).
 * 3. "Run replay" re-posts with the collected edits. The result lands as the "Replay" timeline.
 * 4. Drift / queue-exhaustion / hard-refused responses surface as banners with a Force opt-in.
 *
 * The panel never writes to the original saga state; everything is ephemeral per-request.
 */
@Component({
  selector: 'cf-trace-replay-panel',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonComponent, CardComponent, ChipComponent, TraceTimelineComponent],
  template: `
    <cf-card title="Replay with edit" flush>
      <ng-template #cardRight>
        <button type="button" cf-button variant="ghost" size="sm" icon="x" (click)="close.emit()">Close</button>
      </ng-template>

      @if (loading()) {
        <div class="muted" style="padding: 14px 16px">Loading recorded decisions…</div>
      } @else if (loadError(); as err) {
        <div class="trace-failure" style="margin: 12px 16px">
          <strong>Couldn't load:</strong> {{ err }}
          <div style="margin-top: 6px">
            <button type="button" cf-button variant="ghost" size="sm" (click)="loadBaseline()">Retry</button>
          </div>
        </div>
      } @else {
        <div class="replay-body">
          <p class="muted small" style="padding: 0 16px; margin: 10px 0">
            Substitute one or more recorded responses and re-run the trace through the dry-run
            executor. The original trace is not modified. Leave a row's <em>new decision</em>
            blank to keep its recorded value.
          </p>

          @if (edits().length === 0) {
            <div class="muted" style="padding: 0 16px 14px">
              This trace has no recorded agent decisions to edit.
            </div>
          } @else {
            <table class="table replay-edit-table">
              <thead>
                <tr>
                  <th>Agent</th>
                  <th>#</th>
                  <th>Recorded</th>
                  <th>New decision</th>
                  <th>New output (optional)</th>
                </tr>
              </thead>
              <tbody>
                @for (row of edits(); track $index) {
                  <tr [class.dirty]="rowIsDirty(row)">
                    <td class="mono small">{{ row.decision.agentKey }}</td>
                    <td class="mono small muted">#{{ row.decision.ordinalPerAgent }}</td>
                    <td>
                      <cf-chip variant="default" mono>{{ row.decision.originalDecision }}</cf-chip>
                    </td>
                    <td>
                      <select [(ngModel)]="row.newDecision" class="cf-input">
                        <option value="">— keep —</option>
                        @for (port of portsFor(row.decision.agentKey); track port) {
                          <option [value]="port">{{ port }}</option>
                        }
                      </select>
                    </td>
                    <td>
                      <textarea
                        rows="2"
                        class="cf-textarea"
                        [ngModel]="row.newOutput"
                        (ngModelChange)="setOutput(row, $event)"
                        placeholder="leave blank to keep recorded output"></textarea>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          }

          <div class="replay-actions">
            <button
              type="button"
              cf-button
              variant="primary"
              icon="play"
              [disabled]="running()"
              (click)="runReplay(false)">
              {{ running() ? 'Running…' : 'Run replay' }}
            </button>
            @if (anyDirty()) {
              <button
                type="button"
                cf-button
                variant="ghost"
                size="sm"
                [disabled]="running()"
                (click)="resetEdits()">
                Reset edits
              </button>
            }
            <span class="muted xsmall">
              {{ dirtyCount() }} {{ dirtyCount() === 1 ? 'edit' : 'edits' }} pending
            </span>
          </div>

          @if (replayError(); as err) {
            <div class="trace-failure" style="margin: 0 16px 14px">
              <strong>Replay failed:</strong> {{ err }}
            </div>
          }

          @if (replayResult(); as res) {
            <div class="replay-result-banners">
              @if (res.drift.level !== 'None') {
                <div class="drift-banner" [class.hard]="res.drift.level === 'Hard'">
                  <div class="drift-banner-head">
                    <cf-chip [variant]="res.drift.level === 'Hard' ? 'err' : 'warn'" dot>
                      {{ res.drift.level }} drift
                    </cf-chip>
                    @if (res.drift.level === 'Hard') {
                      <button
                        type="button"
                        cf-button
                        variant="danger"
                        size="sm"
                        [disabled]="running()"
                        (click)="runReplay(true)">
                        Force best-effort replay
                      </button>
                    }
                  </div>
                  <ul class="drift-warnings">
                    @for (warning of res.drift.warnings; track $index) {
                      <li class="muted small">{{ warning }}</li>
                    }
                  </ul>
                </div>
              }

              @if (res.failureCode === 'queue_exhausted' && res.exhaustedAgent) {
                <div class="trace-failure">
                  <strong>Queue exhausted:</strong> agent
                  <code>{{ res.exhaustedAgent.agentKey }}</code> ran out of recorded responses
                  after {{ res.exhaustedAgent.recordedResponses }}
                  {{ res.exhaustedAgent.recordedResponses === 1 ? 'response' : 'responses' }}. Either shorten the edit so the run terminates earlier, or supply
                  additional mocks via the API.
                </div>
              } @else if (res.replayState === 'Failed' && res.failureReason) {
                <div class="trace-failure">
                  <strong>Replay failed:</strong> {{ res.failureReason }}
                </div>
              }

              <div class="replay-summary">
                <cf-chip [variant]="terminalChipVariant(res)" dot>
                  {{ replayStateLabel(res) }}
                </cf-chip>
                @if (res.replayTerminalPort) {
                  <cf-chip mono variant="accent">{{ res.replayTerminalPort }}</cf-chip>
                }
              </div>
            </div>

            <div class="replay-side-by-side">
              <div class="timeline-col">
                <div class="timeline-col-head">
                  <strong>Original</strong>
                  <span class="muted xsmall">{{ baselineEvents().length }} hops</span>
                </div>
                <cf-trace-timeline [events]="baselineEvents()"></cf-trace-timeline>
              </div>
              <div class="timeline-col">
                <div class="timeline-col-head">
                  <strong>Replay</strong>
                  <span class="muted xsmall">
                    {{ replayEvents().length }} hops
                    @if (divergenceOrdinal() !== null) {
                      · diverges at #{{ divergenceOrdinal() }}
                    } @else if (anyDirty()) {
                      · identical to original
                    }
                  </span>
                </div>
                <cf-trace-timeline [events]="replayEvents()"></cf-trace-timeline>
              </div>
            </div>
          }
        </div>
      }
    </cf-card>
  `,
  styles: [`
    .replay-body { display: flex; flex-direction: column; gap: 12px; padding-bottom: 14px; }
    .replay-edit-table { table-layout: fixed; }
    .replay-edit-table th, .replay-edit-table td { vertical-align: top; }
    .replay-edit-table tr.dirty { background: var(--surface-2, rgba(255, 200, 0, 0.05)); }
    .replay-edit-table .cf-input,
    .replay-edit-table .cf-textarea {
      width: 100%;
      box-sizing: border-box;
      background: var(--surface);
      color: var(--text);
      border: 1px solid var(--border);
      border-radius: 6px;
      padding: 4px 8px;
      font-size: 13px;
      font-family: var(--font-sans);
    }
    .replay-edit-table .cf-textarea {
      font-family: var(--font-mono);
      resize: vertical;
      min-height: 36px;
    }
    .replay-actions {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 0 16px;
    }
    .replay-result-banners {
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 0 16px;
    }
    .drift-banner {
      border: 1px solid var(--border);
      background: var(--warn-bg, rgba(255, 200, 0, 0.06));
      border-radius: 6px;
      padding: 10px 12px;
    }
    .drift-banner.hard {
      background: var(--err-bg, rgba(255, 80, 80, 0.07));
      border-color: var(--err-border, rgba(255, 80, 80, 0.25));
    }
    .drift-banner-head {
      display: flex;
      gap: 10px;
      align-items: center;
      margin-bottom: 6px;
    }
    .drift-warnings { margin: 0; padding-left: 18px; list-style: disc; }
    .replay-summary { display: flex; gap: 8px; align-items: center; }
    .replay-side-by-side {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 12px;
      padding: 0 16px;
      align-items: start;
    }
    .timeline-col {
      display: flex;
      flex-direction: column;
      min-width: 0;
      border: 1px solid var(--border);
      border-radius: 6px;
      overflow: hidden;
    }
    .timeline-col-head {
      display: flex;
      justify-content: space-between;
      align-items: baseline;
      padding: 8px 12px;
      background: var(--surface-2, var(--surface));
      border-bottom: 1px solid var(--border);
    }
    @media (max-width: 1100px) {
      .replay-side-by-side { grid-template-columns: 1fr; }
    }
  `]
})
export class TraceReplayPanelComponent implements OnChanges {
  private readonly api = inject(TracesApi);

  @Input({ required: true }) traceId!: string;
  /** The workflow definition the trace ran against. Used to populate decision-port dropdowns. */
  @Input({ required: true }) workflow!: WorkflowDetail | null;
  @Output() close = new EventEmitter<void>();

  readonly loading = signal(false);
  readonly loadError = signal<string | null>(null);
  readonly running = signal(false);
  readonly replayError = signal<string | null>(null);
  readonly replayResult = signal<ReplayResponse | null>(null);
  readonly baseline = signal<ReplayResponse | null>(null);
  readonly edits = signal<EditRowState[]>([]);

  /** Per-agent declared output ports, walked across the workflow + any subflows present in the
   *  loaded definition. The backend has the authoritative subflow walker; the UI only needs the
   *  ports of agents *actually present in the loaded workflow*, which is enough for nodes the
   *  trace actually visited (drift detection on the backend catches anything else). The Failed
   *  port is appended unconditionally because the runtime treats it as universally available. */
  readonly portsByAgent = computed<Map<string, string[]>>(() => {
    const wf = this.workflow;
    const map = new Map<string, string[]>();
    if (!wf) return map;
    for (const node of wf.nodes) {
      const key = node.agentKey?.trim();
      if (!key) continue;
      if (node.kind !== 'Agent' && node.kind !== 'Hitl') continue;
      const set = new Set(map.get(key) ?? []);
      for (const port of node.outputPorts) {
        if (port) set.add(port);
      }
      set.add('Failed');
      map.set(key, Array.from(set));
    }
    return map;
  });

  readonly baselineEvents = computed<TraceTimelineEvent[]>(() =>
    (this.baseline()?.replayEvents ?? []).map(this.mapReplayEvent),
  );

  readonly replayEvents = computed<TraceTimelineEvent[]>(() =>
    (this.replayResult()?.replayEvents ?? []).map(this.mapReplayEvent),
  );

  /** First ordinal at which the replay diverges from the baseline by (kind, nodeId, portName).
   *  Null when the run is identical (or hasn't run yet). */
  readonly divergenceOrdinal = computed<number | null>(() => {
    const a = this.baseline()?.replayEvents ?? [];
    const b = this.replayResult()?.replayEvents ?? [];
    if (b === a || (a.length === 0 && b.length === 0)) return null;
    const limit = Math.max(a.length, b.length);
    for (let i = 0; i < limit; i += 1) {
      const left = a[i];
      const right = b[i];
      if (!left || !right) return left?.ordinal ?? right?.ordinal ?? i;
      if (left.kind !== right.kind || left.nodeId !== right.nodeId || left.portName !== right.portName) {
        return right.ordinal;
      }
    }
    return null;
  });

  readonly anyDirty = computed(() => this.edits().some(this.rowIsDirty));
  readonly dirtyCount = computed(() => this.edits().filter(this.rowIsDirty).length);

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['traceId'] && this.traceId) {
      this.loadBaseline();
    }
  }

  loadBaseline(): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.replayResult.set(null);
    this.api.replay(this.traceId, {}).subscribe({
      next: res => {
        this.baseline.set(res);
        this.replayResult.set(res);
        this.edits.set(res.decisions.map(d => ({
          decision: d,
          newDecision: '',
          newOutput: '',
          outputDirty: false,
        })));
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.loadError.set(formatHttpError(err, 'Unknown error'));
      }
    });
  }

  runReplay(force: boolean): void {
    if (this.running()) return;
    this.running.set(true);
    this.replayError.set(null);

    const dirtyEdits: ReplayEdit[] = [];
    for (const row of this.edits()) {
      if (!this.rowIsDirty(row)) continue;
      dirtyEdits.push({
        agentKey: row.decision.agentKey,
        ordinal: row.decision.ordinalPerAgent,
        decision: row.newDecision || null,
        output: row.outputDirty && row.newOutput.length > 0 ? row.newOutput : null,
      });
    }

    this.api.replay(this.traceId, { edits: dirtyEdits, force }).subscribe({
      next: res => {
        this.replayResult.set(res);
        this.running.set(false);
      },
      error: (err: unknown) => {
        this.running.set(false);
        this.replayError.set(formatHttpError(err, 'Unknown error'));
      }
    });
  }

  resetEdits(): void {
    this.edits.set(this.edits().map(row => ({
      decision: row.decision,
      newDecision: '',
      newOutput: '',
      outputDirty: false,
    })));
    if (this.baseline()) {
      this.replayResult.set(this.baseline());
    }
  }

  setOutput(row: EditRowState, value: string): void {
    row.newOutput = value;
    row.outputDirty = value.length > 0;
  }

  rowIsDirty = (row: EditRowState): boolean =>
    row.newDecision.length > 0 || (row.outputDirty && row.newOutput.length > 0);

  portsFor(agentKey: string): string[] {
    return this.portsByAgent().get(agentKey) ?? ['Failed'];
  }

  terminalChipVariant(res: ReplayResponse): 'ok' | 'err' | 'warn' | 'accent' {
    switch (res.replayState) {
      case 'Completed': return 'ok';
      case 'HitlReached': return 'accent';
      case 'Failed': return 'err';
      case 'StepLimitExceeded':
      case 'DriftRefused':
      default: return 'warn';
    }
  }

  replayStateLabel(res: ReplayResponse): string {
    if (res.replayState === 'DriftRefused') return 'Drift refused';
    return res.replayState;
  }

  private mapReplayEvent = (ev: ReplayEvent): TraceTimelineEvent => ({
    id: `replay-${ev.ordinal}`,
    ordinal: ev.ordinal,
    kind: ev.kind,
    decision: ev.portName,
    title: ev.agentKey || ev.nodeKind,
    message: ev.message,
    inputPreview: ev.inputPreview,
    outputPreview: ev.outputPreview,
    decisionPayload: ev.decisionPayload,
    logs: ev.logs,
    reviewRound: ev.reviewRound,
    maxRounds: ev.maxRounds,
    badges: this.eventBadges(ev),
    expandable: true,
  });

  private eventBadges(ev: ReplayEvent): { label: string; mono?: boolean }[] | undefined {
    const out: { label: string; mono?: boolean }[] = [];
    if (ev.subflowDepth && ev.subflowDepth > 0 && ev.subflowKey) {
      out.push({ label: `subflow ${ev.subflowKey}`, mono: true });
    }
    if (ev.reviewRound != null && ev.maxRounds != null) {
      out.push({ label: `round ${ev.reviewRound}/${ev.maxRounds}`, mono: true });
    }
    return out.length > 0 ? out : undefined;
  }

}
