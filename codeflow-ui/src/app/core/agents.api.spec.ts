import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AgentsApi } from './agents.api';

describe('AgentsApi', () => {
  let api: AgentsApi;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    api = TestBed.inject(AgentsApi);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('URL-encodes agent keys when loading pinned versions', () => {
    api.getVersion('reviewer/main', 7).subscribe();

    const req = httpMock.expectOne('/api/agents/reviewer%2Fmain/7');
    expect(req.request.method).toBe('GET');
    req.flush({});
  });

  it('posts agent config creation and version updates using API contract bodies', () => {
    const config = { name: 'Reviewer', provider: 'openai' as const, model: 'gpt-test' };

    api.create('reviewer', config, ['review', 'ops']).subscribe();
    let req = httpMock.expectOne('/api/agents');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ key: 'reviewer', config, tags: ['review', 'ops'] });
    req.flush({ key: 'reviewer', version: 1 });

    api.addVersion('reviewer/main', config, ['review']).subscribe();
    req = httpMock.expectOne('/api/agents/reviewer%2Fmain');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ config, tags: ['review'] });
    req.flush({ key: 'reviewer/main', version: 8 });
  });

  it('passes repeated tag params when listing by tags', () => {
    api.list(['ops', 'review']).subscribe();

    const req = httpMock.expectOne('/api/agents?tag=ops&tag=review');
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('keeps fork and publish requests aligned with backend contract shape', () => {
    api.fork({
      sourceKey: 'reviewer',
      sourceVersion: 2,
      workflowKey: 'triage-flow',
      config: { name: 'Forked reviewer' },
    }).subscribe();
    let req = httpMock.expectOne('/api/agents/fork');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      sourceKey: 'reviewer',
      sourceVersion: 2,
      workflowKey: 'triage-flow',
      config: { name: 'Forked reviewer' },
    });
    req.flush({});

    api.publish('reviewer/fork', { mode: 'new-agent', newKey: 'reviewer-2', acknowledgeDrift: true }).subscribe();
    req = httpMock.expectOne('/api/agents/reviewer%2Ffork/publish');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      mode: 'new-agent',
      newKey: 'reviewer-2',
      acknowledgeDrift: true,
    });
    req.flush({});
  });
});
