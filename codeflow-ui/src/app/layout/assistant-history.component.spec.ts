import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AssistantConversationSummary } from '../core/assistant.api';
import { AssistantHistoryComponent } from './assistant-history.component';

describe('AssistantHistoryComponent', () => {
  let fixture: ComponentFixture<AssistantHistoryComponent>;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AssistantHistoryComponent],
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

  it('lists resumable conversations and filters empty homepage threads', () => {
    fixture = TestBed.createComponent(AssistantHistoryComponent);
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
    fixture.detectChanges();

    const text = textContent();
    expect(text).toContain('Continue the branch');
    expect(text).toContain('trace · trace-1');
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-history-row-used-home"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-history-row-trace-thread"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-history-row-empty-home"]')).toBeNull();
  });

  it('renders the empty state when no resumable threads exist', () => {
    fixture = TestBed.createComponent(AssistantHistoryComponent);
    fixture.detectChanges();

    expectListConversations().flush({
      conversations: [
        conversation({ id: 'empty-home', messageCount: 0, firstUserMessagePreview: null }),
      ],
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="assistant-history-empty"]')).not.toBeNull();
  });

  it('shows an inline error when the conversations endpoint fails', () => {
    fixture = TestBed.createComponent(AssistantHistoryComponent);
    fixture.detectChanges();

    expectListConversations().flush(
      { error: 'service unavailable' },
      { status: 503, statusText: 'Service Unavailable' },
    );
    fixture.detectChanges();

    const errorEl = fixture.nativeElement.querySelector('[data-testid="assistant-history-error"]');
    expect(errorEl).not.toBeNull();
    expect((errorEl as HTMLElement).textContent).toContain('service unavailable');
  });

  it('emits selected when a conversation row is clicked', () => {
    fixture = TestBed.createComponent(AssistantHistoryComponent);
    const emitted: AssistantConversationSummary[] = [];
    fixture.componentInstance.selected.subscribe(c => emitted.push(c));
    fixture.detectChanges();

    expectListConversations().flush({
      conversations: [conversation({ id: 'used-home', messageCount: 2, firstUserMessagePreview: 'hi' })],
    });
    fixture.detectChanges();

    const row = fixture.nativeElement.querySelector('[data-testid="assistant-history-row-used-home"]') as HTMLElement;
    row.click();

    expect(emitted.length).toBe(1);
    expect(emitted[0].id).toBe('used-home');
  });

  function expectListConversations() {
    const req = httpMock.expectOne(request =>
      request.url === '/api/assistant/conversations' && request.params.get('limit') === '20');
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
