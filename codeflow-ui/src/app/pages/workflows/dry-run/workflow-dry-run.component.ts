import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { CommonModule, JsonPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  DryRunEvent,
  DryRunMockResponse,
  DryRunResponse,
  DryRunState,
  WorkflowFixtureDetail,
  WorkflowFixtureSummary,
  WorkflowsApi,
} from '../../../core/workflows.api';
import { PageHeaderComponent } from '../../../ui/page-header.component';
import { ButtonComponent } from '../../../ui/button.component';
import { CardComponent } from '../../../ui/card.component';
import { ChipComponent, ChipVariant } from '../../../ui/chip.component';
import { TraceTimelineComponent } from '../../../ui/trace-timeline.component';
import { TraceTimelineBadge, TraceTimelineEvent } from '../../../ui/trace-timeline.types';

const STATE_CHIP: Record<DryRunState, ChipVariant> = {
  Completed: 'ok',
  HitlReached: 'accent',
  Failed: 'err',
  StepLimitExceeded: 'warn',
};

interface FixtureDraft {
  fixtureKey: string;
  displayName: string;
  startingInput: string;
  mockResponsesJson: string;
}

const EMPTY_DRAFT: FixtureDraft = {
  fixtureKey: '',
  displayName: '',
  startingInput: '',
  mockResponsesJson: '{\n  "agent-key": [\n    { "decision": "Approved", "output": "..." }\n  ]\n}',
};

function mapDryRunEventToTimeline(ev: DryRunEvent): TraceTimelineEvent {
  const titleParts: string[] = [];
  if (ev.agentKey) titleParts.push(ev.agentKey);
  titleParts.push(ev.nodeKind);
  const title = titleParts.join(' · ');

  const badges: TraceTimelineBadge[] = [];
  if (ev.portName) {
    badges.push({ label: `port: ${ev.portName}`, variant: 'accent', mono: true });
  }
  if (ev.subflowKey) {
    const ver = ev.subflowVersion != null ? ` v${ev.subflowVersion}` : '';
    badges.push({ label: `subflow: ${ev.subflowKey}${ver}`, mono: true });
  }
  if (ev.subflowDepth != null && ev.subflowDepth > 0) {
    badges.push({ label: `depth ${ev.subflowDepth}`, mono: true });
  }

  return {
    id: `dryrun-${ev.ordinal}`,
    ordinal: ev.ordinal,
    kind: ev.kind,
    title,
    badges,
    message: ev.message,
    inputPreview: ev.inputPreview,
    outputPreview: ev.outputPreview,
    decisionPayload: ev.decisionPayload,
    logs: ev.logs,
    reviewRound: ev.reviewRound,
    maxRounds: ev.maxRounds,
  };
}

@Component({
  selector: 'cf-workflow-dry-run',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink, JsonPipe,
    PageHeaderComponent, ButtonComponent, CardComponent, ChipComponent,
    TraceTimelineComponent,
  ],
  template: `
    <div class="page">
      <cf-page-header title="Dry-run workflow">
        <a [routerLink]="['/workflows', key()]">
          <button type="button" cf-button variant="ghost" icon="back">Back</button>
        </a>
        <div page-header-body>
          <div class="trace-header-meta">
            <cf-chip mono>{{ key() }}</cf-chip>
            <cf-chip>simulated · no LLM tokens spent</cf-chip>
          </div>
        </div>
      </cf-page-header>

      <div class="dry-run-grid">
        <!-- Fixture management -->
        <cf-card title="Fixtures">
          <div class="row">
            <select [ngModel]="selectedFixtureId()" (ngModelChange)="onFixtureSelected($event)" name="fixture">
              <option [ngValue]="null">(inline — no fixture)</option>
              @for (f of fixtures(); track f.id) {
                <option [ngValue]="f.id">{{ f.displayName }} ({{ f.fixtureKey }})</option>
              }
            </select>
            <button type="button" cf-button variant="ghost" (click)="newFixture()">+ New</button>
            @if (selectedFixtureId() !== null) {
              <button type="button" cf-button variant="ghost" (click)="deleteSelected()" [disabled]="busy()">Delete</button>
            }
          </div>

          <div class="form-field">
            <label>Fixture key</label>
            <input type="text" [(ngModel)]="draft.fixtureKey" placeholder="happy-path" />
          </div>
          <div class="form-field">
            <label>Display name</label>
            <input type="text" [(ngModel)]="draft.displayName" placeholder="Happy path" />
          </div>
          <div class="form-field">
            <label>Starting input</label>
            <textarea rows="4" [(ngModel)]="draft.startingInput" placeholder="Initial artifact text…"></textarea>
          </div>
          <div class="form-field">
            <label>
              Mock responses (JSON: <code>&#123; "agent-key": [&#123; "decision": "...", "output": "..." &#125;] &#125;</code>)
            </label>
            <textarea rows="14" class="mono" [(ngModel)]="draft.mockResponsesJson"></textarea>
            @if (mockJsonError()) {
              <p class="tag error">{{ mockJsonError() }}</p>
            }
          </div>

          <div class="row">
            <button type="button" cf-button variant="primary" (click)="saveFixture()" [disabled]="busy()">
              {{ selectedFixtureId() === null ? 'Save as new fixture' : 'Save changes' }}
            </button>
            @if (saveError()) {
              <span class="tag error">{{ saveError() }}</span>
            }
          </div>
        </cf-card>

        <!-- Run + result -->
        <cf-card title="Run">
          <div class="form-field">
            <label>Workflow version (blank = latest)</label>
            <input type="number" [(ngModel)]="versionOverride" min="1" />
          </div>
          <div class="form-field">
            <label>Override starting input (optional, overrides fixture)</label>
            <textarea rows="4" [(ngModel)]="inputOverride" placeholder="Leave blank to use fixture's starting input."></textarea>
          </div>

          <div class="row">
            <button type="button" cf-button variant="primary" (click)="runDryRun()" [disabled]="busy() || !canRun()">
              {{ busy() ? 'Running…' : 'Run dry-run' }}
            </button>
            @if (runError()) {
              <span class="tag error">{{ runError() }}</span>
            }
          </div>

          @if (result(); as r) {
            <div class="result-summary">
              <div class="row">
                <cf-chip [variant]="stateVariant(r.state)" mono>{{ r.state }}</cf-chip>
                @if (r.terminalPort) {
                  <cf-chip variant="accent" mono>port: {{ r.terminalPort }}</cf-chip>
                }
                <cf-chip mono>{{ r.events.length }} events</cf-chip>
              </div>

              @if (r.failureReason) {
                <p class="tag error">{{ r.failureReason }}</p>
              }

              @if (r.hitlPayload; as h) {
                <div class="callout">
                  <strong>HITL form would render</strong>
                  <div>Agent: <code>{{ h.agentKey }}</code></div>
                  <div>Node: <code class="small mono">{{ h.nodeId }}</code></div>
                  @if (h.input) {
                    <pre>{{ h.input }}</pre>
                  }
                </div>
              }

              @if (r.finalArtifact) {
                <details>
                  <summary>Final artifact</summary>
                  <pre>{{ r.finalArtifact }}</pre>
                </details>
              }

              @if (objectKeys(r.workflowVariables).length > 0) {
                <details>
                  <summary>Workflow variables ({{ objectKeys(r.workflowVariables).length }})</summary>
                  <pre>{{ r.workflowVariables | json }}</pre>
                </details>
              }
              @if (objectKeys(r.contextVariables).length > 0) {
                <details>
                  <summary>Context variables ({{ objectKeys(r.contextVariables).length }})</summary>
                  <pre>{{ r.contextVariables | json }}</pre>
                </details>
              }
            </div>
          }
        </cf-card>

        <!-- Events list -->
        @if (result(); as r) {
          <cf-card title="Trace events" flush>
            <ng-template #cardRight><cf-chip mono>{{ r.events.length }} events</cf-chip></ng-template>
            <cf-trace-timeline [events]="timelineEvents()"></cf-trace-timeline>
          </cf-card>
        }
      </div>
    </div>
  `,
  styles: [`
    .dry-run-grid {
      display: grid;
      grid-template-columns: minmax(360px, 1fr) minmax(360px, 1fr);
      gap: 1rem;
    }
    .dry-run-grid > :last-child {
      grid-column: 1 / -1;
    }
    .form-field {
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
      margin: 0.5rem 0;
    }
    .form-field label {
      font-size: 0.85rem;
      color: var(--color-fg-muted, #888);
    }
    .form-field input, .form-field textarea, .form-field select {
      padding: 0.4rem 0.5rem;
    }
    .form-field textarea.mono {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 0.8rem;
    }
    .row {
      display: flex;
      gap: 0.5rem;
      flex-wrap: wrap;
      align-items: center;
      margin: 0.5rem 0;
    }
    .result-summary {
      display: flex;
      flex-direction: column;
      gap: 0.5rem;
      margin-top: 1rem;
    }
    .callout {
      border-left: 3px solid var(--color-accent, #6cf);
      padding: 0.5rem 0.75rem;
      background: rgba(108, 170, 255, 0.06);
    }
    .small { font-size: 0.85rem; }
    .muted { opacity: 0.7; }
    .tag.error {
      color: var(--color-err-fg, #c44);
      font-size: 0.85rem;
    }
  `],
})
export class WorkflowDryRunComponent implements OnInit {
  private readonly api = inject(WorkflowsApi);

  readonly key = input.required<string>();
  readonly fixtures = signal<WorkflowFixtureSummary[]>([]);
  readonly selectedFixtureId = signal<number | null>(null);
  readonly result = signal<DryRunResponse | null>(null);
  readonly busy = signal(false);
  readonly runError = signal<string | null>(null);
  readonly saveError = signal<string | null>(null);

  draft: FixtureDraft = { ...EMPTY_DRAFT };
  versionOverride: number | null = null;
  inputOverride = '';

  readonly mockJsonError = computed(() => {
    try {
      const parsed = JSON.parse(this.draft.mockResponsesJson || '{}');
      if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
        return 'Mock responses must be a JSON object keyed by agent key.';
      }
      return null;
    } catch (err) {
      return `Invalid JSON: ${(err as Error).message}`;
    }
  });

  readonly canRun = () => true;

  ngOnInit(): void {
    this.refreshFixtures();
  }

  refreshFixtures(): void {
    this.api.listFixtures(this.key()).subscribe({
      next: list => this.fixtures.set(list),
      error: () => this.fixtures.set([]),
    });
  }

  newFixture(): void {
    this.selectedFixtureId.set(null);
    this.draft = { ...EMPTY_DRAFT };
    this.saveError.set(null);
  }

  onFixtureSelected(id: number | null): void {
    this.selectedFixtureId.set(id);
    this.saveError.set(null);
    if (id === null) {
      this.draft = { ...EMPTY_DRAFT };
      return;
    }
    this.api.getFixture(this.key(), id).subscribe({
      next: detail => {
        this.draft = {
          fixtureKey: detail.fixtureKey,
          displayName: detail.displayName,
          startingInput: detail.startingInput ?? '',
          mockResponsesJson: JSON.stringify(detail.mockResponses ?? {}, null, 2),
        };
      },
      error: err => this.saveError.set(this.errorMessage(err)),
    });
  }

  saveFixture(): void {
    if (this.mockJsonError()) {
      this.saveError.set('Fix the mock-responses JSON error before saving.');
      return;
    }
    let mockResponses: Record<string, DryRunMockResponse[]>;
    try {
      mockResponses = JSON.parse(this.draft.mockResponsesJson || '{}');
    } catch (err) {
      this.saveError.set(`Invalid JSON: ${(err as Error).message}`);
      return;
    }

    this.busy.set(true);
    this.saveError.set(null);

    const id = this.selectedFixtureId();
    if (id === null) {
      this.api.createFixture(this.key(), {
        workflowKey: this.key(),
        fixtureKey: this.draft.fixtureKey,
        displayName: this.draft.displayName,
        startingInput: this.draft.startingInput || null,
        mockResponses,
      }).subscribe({
        next: created => {
          this.busy.set(false);
          this.refreshFixtures();
          this.selectedFixtureId.set(created.id);
        },
        error: err => {
          this.busy.set(false);
          this.saveError.set(this.errorMessage(err));
        },
      });
    } else {
      this.api.updateFixture(this.key(), id, {
        fixtureKey: this.draft.fixtureKey,
        displayName: this.draft.displayName,
        startingInput: this.draft.startingInput || null,
        mockResponses,
      }).subscribe({
        next: () => {
          this.busy.set(false);
          this.refreshFixtures();
        },
        error: err => {
          this.busy.set(false);
          this.saveError.set(this.errorMessage(err));
        },
      });
    }
  }

  deleteSelected(): void {
    const id = this.selectedFixtureId();
    if (id === null) return;
    if (!confirm('Delete this fixture?')) return;
    this.busy.set(true);
    this.api.deleteFixture(this.key(), id).subscribe({
      next: () => {
        this.busy.set(false);
        this.selectedFixtureId.set(null);
        this.draft = { ...EMPTY_DRAFT };
        this.refreshFixtures();
      },
      error: err => {
        this.busy.set(false);
        this.saveError.set(this.errorMessage(err));
      },
    });
  }

  runDryRun(): void {
    this.busy.set(true);
    this.runError.set(null);
    this.result.set(null);

    let inlineMocks: Record<string, DryRunMockResponse[]> | null = null;
    if (this.selectedFixtureId() === null) {
      try {
        inlineMocks = JSON.parse(this.draft.mockResponsesJson || '{}');
      } catch (err) {
        this.busy.set(false);
        this.runError.set(`Invalid mock JSON: ${(err as Error).message}`);
        return;
      }
    }

    this.api.dryRun(this.key(), {
      fixtureId: this.selectedFixtureId(),
      workflowVersion: this.versionOverride,
      startingInput: this.inputOverride.trim()
        ? this.inputOverride
        : (this.selectedFixtureId() === null ? this.draft.startingInput : null),
      mockResponses: inlineMocks,
    }).subscribe({
      next: result => {
        this.busy.set(false);
        this.result.set(result);
      },
      error: err => {
        this.busy.set(false);
        this.runError.set(this.errorMessage(err));
      },
    });
  }

  stateVariant(state: DryRunState): ChipVariant {
    return STATE_CHIP[state] ?? 'default';
  }

  readonly timelineEvents = computed<TraceTimelineEvent[]>(() => {
    const r = this.result();
    if (!r) return [];
    return r.events.map(ev => mapDryRunEventToTimeline(ev));
  });

  objectKeys(value: Record<string, unknown> | null | undefined): string[] {
    return value ? Object.keys(value) : [];
  }

  private errorMessage(err: unknown): string {
    if (err && typeof err === 'object' && 'error' in err) {
      const e = (err as { error?: { error?: string } }).error;
      if (e?.error) return e.error;
    }
    return (err as { message?: string })?.message ?? 'Request failed';
  }
}
