import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TraceReplayPanelComponent } from './trace-replay-panel.component';

/**
 * sc-274 phase 1 — covers the preflight refusal banner. Ensures a 422
 * `preflight-ambiguous` response from POST /api/traces/{id}/replay surfaces the
 * clarification questions inline rather than as a generic error message, and the
 * banner clears on a fresh attempt.
 */
describe('TraceReplayPanelComponent — preflight banner', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [TraceReplayPanelComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('renders clarification questions from a 422 preflight-ambiguous response', () => {
    const fixture = TestBed.createComponent(TraceReplayPanelComponent);
    fixture.componentRef.setInput('traceId', 'trace-x');
    fixture.componentRef.setInput('workflow', null);
    fixture.detectChanges();

    // Baseline replay (empty body) succeeds — round-trip identity is allowed.
    httpMock.expectOne('/api/traces/trace-x/replay').flush(buildEmptyReplayResponse('trace-x'));
    fixture.detectChanges();

    // Force a re-run by calling runReplay directly. Without dirty rows the call still
    // dispatches a POST — we simulate the user adding an edit by writing the row state.
    fixture.componentInstance['edits'].update(rows => [
      ...rows,
      {
        decision: { agentKey: 'echo', ordinalPerAgent: 1, sagaCorrelationId: 'cor', sagaOrdinal: 0, nodeId: null, roundId: 'round', originalDecision: 'Completed' },
        newDecision: 'Loop',
        newOutput: '',
        outputDirty: false,
      },
    ]);
    fixture.componentInstance.runReplay(false);
    fixture.detectChanges();

    const replayReq = httpMock.expectOne('/api/traces/trace-x/replay');
    replayReq.flush(
      {
        originalTraceId: 'trace-x',
        code: 'preflight-ambiguous',
        mode: 'ReplayEdit',
        overallScore: 0.4,
        threshold: 0.5,
        dimensions: [
          { dimension: 'success_criteria', score: 0.4, reason: 'no output for decision-changing edit' },
          { dimension: 'goal', score: 1.0, reason: null },
          { dimension: 'constraints', score: 1.0, reason: null },
          { dimension: 'context', score: 1.0, reason: null },
        ],
        missingFields: ['edits[0].output'],
        clarificationQuestions: [
          'Edit at echo/ord-1 changes the decision to "Loop" but provides no output. What should the agent\'s output have been?',
        ],
      },
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    fixture.detectChanges();

    const banner = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="preflight-banner"]');
    expect(banner).not.toBeNull();
    const text = banner!.textContent ?? '';
    expect(text).toContain('Edits need clarification before replay');
    expect(text).toContain('echo/ord-1');
    expect(text).toContain('success_criteria');
    expect(text).toContain('0.40');
    expect(text).toContain('0.50');
  });

  it('clears the preflight banner when the user resets edits', () => {
    const fixture = TestBed.createComponent(TraceReplayPanelComponent);
    fixture.componentRef.setInput('traceId', 'trace-y');
    fixture.componentRef.setInput('workflow', null);
    fixture.detectChanges();
    httpMock.expectOne('/api/traces/trace-y/replay').flush(buildEmptyReplayResponse('trace-y'));
    fixture.detectChanges();

    fixture.componentInstance['preflightRefusal'].set({
      originalTraceId: 'trace-y',
      code: 'preflight-ambiguous',
      mode: 'ReplayEdit',
      overallScore: 0.0,
      threshold: 0.5,
      dimensions: [],
      missingFields: [],
      clarificationQuestions: ['stub'],
    });
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="preflight-banner"]'))
      .not.toBeNull();

    fixture.componentInstance.resetEdits();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="preflight-banner"]'))
      .toBeNull();
  });
});

function buildEmptyReplayResponse(traceId: string) {
  return {
    originalTraceId: traceId,
    replayState: 'Completed',
    replayTerminalPort: 'Completed',
    failureReason: null,
    failureCode: null,
    exhaustedAgent: null,
    decisions: [],
    replayEvents: [],
    hitlPayload: null,
    drift: { level: 'None', warnings: [] },
  };
}
