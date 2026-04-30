import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { TraceSubmitComponent } from './trace-submit.component';

/**
 * sc-274 phase 3 — covers the workflow-launch preflight refusal banner. Ensures a 422
 * `workflow-preflight-ambiguous` response from POST /api/traces surfaces the
 * clarification questions inline rather than as a generic error message, and the banner
 * clears on a fresh attempt.
 */
describe('TraceSubmitComponent — preflight banner', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [TraceSubmitComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // Stub route for the success-path navigate('/traces', traceId) so the second test
        // doesn't trip Router.NG04002 when the success branch fires.
        provideRouter([{ path: 'traces/:id', children: [] }]),
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('renders clarification questions from a 422 workflow-preflight-ambiguous response', () => {
    const fixture = TestBed.createComponent(TraceSubmitComponent);
    fixture.detectChanges();

    // Drain the mount-time workflow list call so the rest of the test doesn't fight it.
    httpMock.expectOne('/api/workflows').flush([]);

    // Drive the component into a state that lets submit() POST to /api/traces.
    fixture.componentInstance['workflowKey'].set('greenfield-prd');
    fixture.componentInstance['workflowVersion'].set(1);
    fixture.componentInstance['workflowDetail'].set({
      key: 'greenfield-prd',
      version: 1,
      name: 'Greenfield PRD',
      maxRoundsPerRound: 3,
      nodes: [],
      edges: [],
      inputs: [],
      isLatest: true,
    } as never);
    fixture.componentInstance['inputValues'].set({ '__startInput__': 'PRD for auth' });
    fixture.detectChanges();

    fixture.componentInstance.submit({ preventDefault: () => {} } as Event);
    fixture.detectChanges();

    const submitReq = httpMock.expectOne('/api/traces');
    expect(submitReq.request.method).toBe('POST');
    submitReq.flush(
      {
        workflowKey: 'greenfield-prd',
        code: 'workflow-preflight-ambiguous',
        mode: 'GreenfieldDraft',
        overallScore: 0.2,
        threshold: 0.7,
        dimensions: [
          { dimension: 'goal', score: 0.2, reason: 'input is 3 word(s) — not enough to act on for a greenfield drafting launch' },
          { dimension: 'constraints', score: 1.0, reason: null },
          { dimension: 'success_criteria', score: 1.0, reason: null },
          { dimension: 'context', score: 1.0, reason: null },
        ],
        missingFields: ['input.too-short'],
        clarificationQuestions: [
          'Describe what you want drafted in at least 1-2 sentences (the goal, who it\'s for, why it matters).',
        ],
      },
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    fixture.detectChanges();

    const banner = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="preflight-banner"]');
    expect(banner).not.toBeNull();
    const text = banner!.textContent ?? '';
    expect(text).toContain('preflight');
    expect(text).toContain('clarity 20%');
    expect(text).toContain('needs 70%');
    expect(text).toContain('greenfield drafting');
    expect(text).toContain('at least 1-2 sentences');

    // Generic error path is NOT engaged — the structured banner replaces the "Submit failed"
    // line so the user sees the clarification questions instead of a flat error message.
    expect(fixture.componentInstance.error()).toBeNull();
  });

  it('clears the preflight banner when the user re-submits', () => {
    const fixture = TestBed.createComponent(TraceSubmitComponent);
    fixture.detectChanges();
    httpMock.expectOne('/api/workflows').flush([]);

    fixture.componentInstance['workflowKey'].set('greenfield-prd');
    fixture.componentInstance['workflowVersion'].set(1);
    fixture.componentInstance['workflowDetail'].set({
      key: 'greenfield-prd',
      version: 1,
      name: 'Greenfield PRD',
      maxRoundsPerRound: 3,
      nodes: [],
      edges: [],
      inputs: [],
      isLatest: true,
    } as never);
    fixture.componentInstance['inputValues'].set({ '__startInput__': 'PRD for auth' });
    fixture.detectChanges();

    fixture.componentInstance.submit({ preventDefault: () => {} } as Event);
    httpMock.expectOne('/api/traces').flush(
      {
        workflowKey: 'greenfield-prd',
        code: 'workflow-preflight-ambiguous',
        mode: 'GreenfieldDraft',
        overallScore: 0.2,
        threshold: 0.7,
        dimensions: [],
        missingFields: ['input.too-short'],
        clarificationQuestions: ['Describe what you want drafted in at least 1-2 sentences.'],
      },
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    fixture.detectChanges();
    expect(fixture.componentInstance.preflightError()).not.toBeNull();

    // Refining the input and re-submitting should clear the banner immediately on the new
    // attempt (before the response lands), so the user doesn't see stale clarifications.
    fixture.componentInstance['inputValues'].set({
      '__startInput__': 'Draft a PRD for the new SSO authentication flow with rollout plan',
    });
    fixture.componentInstance.submit({ preventDefault: () => {} } as Event);
    expect(fixture.componentInstance.preflightError()).toBeNull();

    // Drain the second POST so HttpTestingController.verify() doesn't fail. The success
    // path then polls GET /api/traces/{id} to confirm the trace landed; drain that too.
    httpMock.expectOne('/api/traces').flush({ traceId: '00000000-0000-0000-0000-000000000001' });
    httpMock.expectOne('/api/traces/00000000-0000-0000-0000-000000000001').flush(
      { traceId: '00000000-0000-0000-0000-000000000001' });
  });
});
