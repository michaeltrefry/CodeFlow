import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AgentConfig } from '../../core/models';
import { AgentFormComponent, AgentFormSaveRequest } from './agent-form.component';

describe('AgentFormComponent', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AgentFormComponent],
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

  it('hydrates existing agent config and emits a typed save payload', () => {
    const fixture = TestBed.createComponent(AgentFormComponent);
    const saves: AgentFormSaveRequest[] = [];
    const initialConfig: AgentConfig = {
      type: 'agent',
      name: 'Technical reviewer',
      description: 'Reviews changes',
      provider: 'anthropic',
      model: 'claude-sonnet-4-6',
      systemPrompt: 'Review carefully.',
      promptTemplate: 'Check {{ input }}',
      partialPins: [
        { key: '@codeflow/last-round-reminder', version: 2 },
        { key: '@codeflow/bad-version', version: 'latest' },
        { version: 3 },
      ],
    };

    fixture.componentRef.setInput('key', 'reviewer/main');
    fixture.componentRef.setInput('initialType', 'agent');
    fixture.componentRef.setInput('initialConfig', initialConfig);
    fixture.componentInstance.saveRequested.subscribe(save => saves.push(save));
    fixture.detectChanges();

    httpMock.expectOne('/api/llm-providers/models').flush([
      { provider: 'anthropic', model: 'claude-sonnet-4-6' },
    ]);

    fixture.componentInstance.submit(preventableSubmitEvent());

    expect(saves).toHaveLength(1);
    expect(saves[0]).toMatchObject({
      key: 'reviewer/main',
      type: 'agent',
      config: {
        name: 'Technical reviewer',
        description: 'Reviews changes',
        provider: 'anthropic',
        model: 'claude-sonnet-4-6',
        systemPrompt: 'Review carefully.',
        promptTemplate: 'Check {{ input }}',
      },
    });
    expect(saves[0].config['partialPins']).toEqual([
      { key: '@codeflow/last-round-reminder', version: 2 },
    ]);
  });
});

function preventableSubmitEvent(): Event {
  return { preventDefault: vi.fn() } as unknown as Event;
}
