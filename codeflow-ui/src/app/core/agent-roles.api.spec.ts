import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AgentRolesApi } from './agent-roles.api';

describe('AgentRolesApi', () => {
  let api: AgentRolesApi;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    api = TestBed.inject(AgentRolesApi);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('passes repeated tag params when listing by tags', () => {
    api.list(false, ['ops', 'review']).subscribe();

    const req = httpMock.expectOne('/api/agent-roles?includeArchived=false&tag=ops&tag=review');
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('sends tags when creating and updating roles', () => {
    api.create({
      key: 'reviewer',
      displayName: 'Reviewer',
      description: 'Reviews changes',
      tags: ['review', 'ops'],
    }).subscribe();
    let req = httpMock.expectOne('/api/agent-roles');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      key: 'reviewer',
      displayName: 'Reviewer',
      description: 'Reviews changes',
      tags: ['review', 'ops'],
    });
    req.flush({});

    api.update(7, {
      displayName: 'Operator',
      description: null,
      tags: ['ops'],
    }).subscribe();
    req = httpMock.expectOne('/api/agent-roles/7');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({
      displayName: 'Operator',
      description: null,
      tags: ['ops'],
    });
    req.flush({});
  });
});
