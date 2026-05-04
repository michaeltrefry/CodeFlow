import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { WorkflowsApi, type WorkflowPayload } from './workflows.api';

describe('WorkflowsApi', () => {
  let api: WorkflowsApi;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    api = TestBed.inject(WorkflowsApi);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('builds workflow list and version URLs with encoded workflow keys', () => {
    api.listRecent(12).subscribe();
    let req = httpMock.expectOne('/api/workflows/recent?take=12');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('take')).toBe('12');
    req.flush([]);

    api.getVersion('triage/flow', 4).subscribe();
    req = httpMock.expectOne('/api/workflows/triage%2Fflow/4');
    expect(req.request.method).toBe('GET');
    req.flush(workflowDetail());
  });

  it('selects latest terminal-port endpoint when no subflow version is pinned', () => {
    api.getTerminalPorts('child/flow', null).subscribe();

    const req = httpMock.expectOne('/api/workflows/child%2Fflow/latest/terminal-ports');
    expect(req.request.method).toBe('GET');
    req.flush(['Completed']);
  });

  it('posts create, add-version, import-preview, and dry-run contract bodies', () => {
    const payload = workflowPayload();

    api.create(payload).subscribe();
    let req = httpMock.expectOne('/api/workflows');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBe(payload);
    req.flush({ key: 'triage-flow', version: 1 });

    api.addVersion('triage/flow', payload).subscribe();
    req = httpMock.expectOne('/api/workflows/triage%2Fflow');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toBe(payload);
    req.flush({ key: 'triage/flow', version: 2 });

    const workflowPackage = { schemaVersion: 'codeflow.workflow-package.v1' };
    api.previewPackageImport(workflowPackage).subscribe();
    req = httpMock.expectOne('/api/workflows/package/preview');
    expect(req.request.method).toBe('POST');
    // sc-395: the wire body is now `{ package, resolutions? }` so the API service can also
    // surface user-chosen resolutions; bare-package callers continue to work because the
    // service wraps internally and `resolutions` defaults to undefined.
    expect(req.request.body).toEqual({ package: workflowPackage, resolutions: undefined });
    req.flush({ items: [], warnings: [], createCount: 0, reuseCount: 0, conflictCount: 0, refusedCount: 0, warningCount: 0, canApply: true });

    const dryRun = { fixtureId: 5, workflowVersion: 2 };
    api.dryRun('triage/flow', dryRun).subscribe();
    req = httpMock.expectOne('/api/workflows/triage%2Fflow/dry-run');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBe(dryRun);
    req.flush({ state: 'Completed', events: [] });
  });

  it('requests package downloads and artifact-like responses with the correct observe/responseType options', () => {
    api.downloadPackage('triage/flow', 3).subscribe();
    let req = httpMock.expectOne('/api/workflows/triage%2Fflow/3/package');
    expect(req.request.method).toBe('GET');
    expect(req.request.responseType).toBe('blob');
    req.flush(new Blob(['{}']), { status: 200, statusText: 'OK' });

    api.getPackage('triage/flow', 3).subscribe();
    req = httpMock.expectOne('/api/workflows/triage%2Fflow/3/package');
    expect(req.request.method).toBe('GET');
    expect(req.request.responseType).toBe('json');
    req.flush({ schemaVersion: 'codeflow.workflow-package.v1' });
  });
});

function workflowPayload(): WorkflowPayload {
  return {
    key: 'triage-flow',
    name: 'Triage Flow',
    maxRoundsPerRound: 3,
    category: 'Workflow',
    tags: ['ops'],
    nodes: [],
    edges: [],
    inputs: [],
  };
}

function workflowDetail() {
  return {
    ...workflowPayload(),
    key: 'triage/flow',
    version: 4,
    createdAtUtc: '2026-04-29T00:00:00Z',
  };
}
