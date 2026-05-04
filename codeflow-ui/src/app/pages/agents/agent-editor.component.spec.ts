import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter, Router } from '@angular/router';
import { AgentFormComponent } from './agent-form.component';
import { AgentEditorPageComponent } from './agent-editor.component';

describe('AgentEditorPageComponent', () => {
  let httpMock: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AgentEditorPageComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('loads an existing agent and saves a new immutable version through the form', () => {
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(AgentEditorPageComponent);
    fixture.componentRef.setInput('key', 'reviewer/main');
    fixture.detectChanges();

    httpMock.expectOne('/api/llm-providers/models').flush([
      { provider: 'openai', model: 'gpt-5.4' },
    ]);
    httpMock.expectOne('/api/agents/reviewer%2Fmain').flush({
      key: 'reviewer/main',
      version: 4,
      type: 'agent',
      tags: ['review', 'ops'],
      config: {
        type: 'agent',
        name: 'Reviewer',
        provider: 'openai',
        model: 'gpt-5.4',
        systemPrompt: 'Review carefully.',
      },
      createdAtUtc: '2026-04-30T18:00:00Z',
      isRetired: false,
    });
    fixture.detectChanges();

    const form = fixture.debugElement.query(By.directive(AgentFormComponent))
      .componentInstance as AgentFormComponent;
    form.submit(preventableSubmitEvent());

    const save = httpMock.expectOne('/api/agents/reviewer%2Fmain');
    expect(save.request.method).toBe('PUT');
    expect(save.request.body.config).toMatchObject({
      name: 'Reviewer',
      provider: 'openai',
      model: 'gpt-5.4',
      systemPrompt: 'Review carefully.',
    });
    expect(save.request.body.tags).toEqual(['review', 'ops']);
    save.flush({ key: 'reviewer/main', version: 5 });

    expect(navigate).toHaveBeenCalledWith(['/agents', 'reviewer/main']);
  });
});

function preventableSubmitEvent(): Event {
  return { preventDefault: vi.fn() } as unknown as Event;
}
