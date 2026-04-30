import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { HomeRailComponent } from './home-rail.component';
import { AssistantConversationSummary, AssistantTokenUsageSummary } from '../../core/assistant.api';
import { TraceSummary, WorkflowSummary } from '../../core/models';
import { RecentWorkflow } from '../../core/workflows.api';

describe('HomeRailComponent', () => {
  let fixture: ComponentFixture<HomeRailComponent>;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HomeRailComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('renders resumable conversations, token summary, traces, and recent workflows', () => {
    fixture = TestBed.createComponent(HomeRailComponent);
    fixture.detectChanges();

    expectListConversations().flush({
      conversations: [
        conversation({ id: 'empty-home', messageCount: 0, firstUserMessagePreview: null }),
        conversation({ id: 'used-home', messageCount: 2, firstUserMessagePreview: 'Continue the branch' }),
        conversation({
          id: 'trace-thread',
          scope: { kind: 'entity', entityType: 'trace', entityId: 'trace-1' },
          messageCount: 0,
          firstUserMessagePreview: null,
        }),
      ],
    });
    expectTokenSummary().flush(tokenSummary());
    expectTraces().flush([trace('trace-1', 'Running'), trace('trace-2', 'Completed')]);
    expectRecentWorkflows().flush([recentWorkflow('wf-a', 'Review loop')]);
    fixture.detectChanges();

    const text = textContent();
    expect(text).toContain('10 in');
    expect(text).toContain('5 out');
    expect(text).toContain('Continue the branch');
    expect(text).toContain('trace · trace-1');
    expect(text).not.toContain('empty-home');
    expect(text).toContain('workflow-a');
    expect(text).toContain('Running');
    expect(text).toContain('Review loop');
    expect(fixture.nativeElement.querySelector('[data-testid="rail-trace-trace-1"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="rail-workflow-wf-a"]')).not.toBeNull();
  });

  it('hides real-data sections in demo mode', () => {
    fixture = TestBed.createComponent(HomeRailComponent);
    fixture.componentRef.setInput('demoMode', true);
    fixture.detectChanges();

    expectListConversations().flush({ conversations: [] });
    expectTokenSummary().flush(tokenSummary({ callCount: 0 }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="rail-section-recent-traces"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="rail-section-recent-workflows"]')).toBeNull();
  });

  it('shows section-scoped errors without collapsing the rail', () => {
    fixture = TestBed.createComponent(HomeRailComponent);
    fixture.detectChanges();

    expectListConversations().flush(
      { error: 'conversation service unavailable' },
      { status: 503, statusText: 'Service Unavailable' },
    );
    expectTokenSummary().flush(tokenSummary());
    expectTraces().flush(
      { error: 'trace service unavailable' },
      { status: 503, statusText: 'Service Unavailable' },
    );
    expectRecentWorkflows().flush(
      { error: 'workflow service unavailable' },
      { status: 503, statusText: 'Service Unavailable' },
    );
    fixture.detectChanges();

    const text = textContent();
    expect(text).toContain('conversation service unavailable');
    expect(text).toContain('trace service unavailable');
    expect(text).toContain('workflow service unavailable');
    expect(text).toContain('10 in');
  });

  function expectListConversations() {
    const req = httpMock.expectOne(request =>
      request.url === '/api/assistant/conversations' && request.params.get('limit') === '20');
    expect(req.request.method).toBe('GET');
    return req;
  }

  function expectTokenSummary() {
    const req = httpMock.expectOne('/api/assistant/token-usage/summary');
    expect(req.request.method).toBe('GET');
    return req;
  }

  function expectTraces() {
    const req = httpMock.expectOne('/api/traces');
    expect(req.request.method).toBe('GET');
    return req;
  }

  function expectRecentWorkflows() {
    const req = httpMock.expectOne(request =>
      request.url === '/api/workflows/recent' && request.params.get('take') === '5');
    expect(req.request.method).toBe('GET');
    return req;
  }

  function textContent(): string {
    return (fixture.nativeElement as HTMLElement).textContent ?? '';
  }
});

function conversation(overrides: Partial<AssistantConversationSummary> = {}): AssistantConversationSummary {
  return {
    id: 'conversation-1',
    scope: { kind: 'homepage' },
    syntheticTraceId: 'synthetic-1',
    createdAtUtc: '2026-04-30T10:00:00Z',
    updatedAtUtc: '2026-04-30T11:00:00Z',
    messageCount: 1,
    firstUserMessagePreview: 'Conversation',
    ...overrides,
  };
}

function tokenSummary(overrides: { callCount?: number } = {}): AssistantTokenUsageSummary {
  const callCount = overrides.callCount ?? 1;
  return {
    today: {
      totals: callCount > 0 ? { input_tokens: 10, output_tokens: 5 } : {},
      callCount,
      byProviderModel: [],
    },
    allTime: {
      totals: callCount > 0 ? { input_tokens: 20, output_tokens: 10 } : {},
      callCount,
      byProviderModel: [],
    },
    perConversation: [],
  };
}

function trace(traceId: string, currentState: string): TraceSummary {
  return {
    traceId,
    workflowKey: 'workflow-a',
    workflowVersion: 2,
    currentState,
    currentAgentKey: 'agent-a',
    roundCount: 1,
    createdAtUtc: '2026-04-30T10:00:00Z',
    updatedAtUtc: '2026-04-30T11:00:00Z',
    parentTraceId: null,
    parentNodeId: null,
    parentReviewRound: null,
    parentReviewMaxRounds: null,
  };
}

function recentWorkflow(key: string, name: string): RecentWorkflow {
  return {
    summary: workflowSummary(key, name),
    lastUsedAtUtc: '2026-04-30T11:00:00Z',
  };
}

function workflowSummary(key: string, name: string): WorkflowSummary {
  return {
    key,
    name,
    latestVersion: 3,
    category: 'Workflow',
    tags: [],
    nodeCount: 2,
    edgeCount: 1,
    inputCount: 1,
    createdAtUtc: '2026-04-30T09:00:00Z',
  };
}
