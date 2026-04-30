import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TraceBundlePanelComponent } from './trace-bundle-panel.component';
import {
  TraceBundleArtifactRef,
  TraceBundleAuthoritySnapshot,
  TraceBundleManifest,
  TraceBundleRefusal,
  TraceBundleReplayAttempt,
} from '../../core/models';

/**
 * sc-271 PR2: smoke coverage for the trace inspector's bundle composition panel.
 * Each test stages a manifest with the shape needed and asserts the rendered output
 * surfaces it — refusal stage breakdown, missing-artifact chip, Export Bundle download
 * trigger.
 */
describe('TraceBundlePanelComponent', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [TraceBundlePanelComponent],
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

  it('renders refusal stage breakdown and missing-artifact warn chip from the manifest', async () => {
    const fixture = TestBed.createComponent(TraceBundlePanelComponent);
    fixture.componentRef.setInput('traceId', 'trace-a');
    fixture.detectChanges();

    const req = httpMock.expectOne('/api/traces/trace-a/bundle/manifest');
    expect(req.request.method).toBe('GET');

    req.flush(buildManifest({
      refusals: [
        refusal('refusal-1', 'workspace', 'workspace-mutation-blocked'),
        refusal('refusal-2', 'tool', 'envelope-execute-grants'),
        refusal('refusal-3', 'tool', 'envelope-execute-grants'),
      ],
      authoritySnapshots: [
        authoritySnapshot('snap-1', 'agent-a', '["execute","fs.write"]'),
      ],
      artifacts: [
        artifactRef('artifacts/abc.bin', 'abc'),
        artifactRef('artifacts/missing-xyz.bin', ''), // dangling pointer
      ],
    }));

    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    expect(text).toContain('3 refusals');
    expect(text).toContain('1 authority snapshots');
    expect(text).toContain('2 artifacts');
    expect(text).toContain('1 missing');
    // Stage breakdown: 'tool' should win the order with count 2, 'workspace' with 1.
    expect(text).toMatch(/tool × 2[\s\S]*workspace × 1/);
    expect(text).toContain('2 blocked axes');
  });

  it('triggers the bundle download when Export Bundle is clicked', () => {
    const fixture = TestBed.createComponent(TraceBundlePanelComponent);
    fixture.componentRef.setInput('traceId', '00000000-0000-0000-0000-000000000abc');
    fixture.detectChanges();

    httpMock.expectOne('/api/traces/00000000-0000-0000-0000-000000000abc/bundle/manifest')
      .flush(buildManifest({}));
    fixture.detectChanges();

    const button = (fixture.nativeElement as HTMLElement)
      .querySelector('button[cf-button]') as HTMLButtonElement | null;
    expect(button).not.toBeNull();
    button!.click();

    const downloadReq = httpMock.expectOne('/api/traces/00000000-0000-0000-0000-000000000abc/bundle');
    expect(downloadReq.request.method).toBe('GET');
    expect(downloadReq.request.responseType).toBe('blob');
  });

  it('renders sc-275 replay attempts stat chip + collapsible list', () => {
    const fixture = TestBed.createComponent(TraceBundlePanelComponent);
    fixture.componentRef.setInput('traceId', 'trace-r');
    fixture.detectChanges();

    httpMock.expectOne('/api/traces/trace-r/bundle/manifest').flush(buildManifest({
      replayAttempts: [
        {
          id: 'r1', parentTraceId: 'trace-r',
          lineageId: '11111111-2222-3333-4444-555555555555',
          contentHash: 'a'.repeat(64),
          generation: 1, replayState: 'Completed', terminalPort: 'Completed',
          driftLevel: 'None', reason: 'ui:replay-panel',
          createdAtUtc: '2026-04-30T17:30:00Z',
        },
        {
          id: 'r2', parentTraceId: 'trace-r',
          lineageId: '11111111-2222-3333-4444-555555555555',
          contentHash: 'a'.repeat(64),
          generation: 1, replayState: 'Failed', terminalPort: null,
          driftLevel: 'Soft', reason: 'assistant:propose_replay_with_edit',
          createdAtUtc: '2026-04-30T18:00:00Z',
        },
      ],
    }));
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('2 replay attempts');
    const section = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="bundle-replay-attempts"]');
    expect(section).not.toBeNull();
    const sectionText = section!.textContent ?? '';
    expect(sectionText).toContain('1 lineage');
    expect(sectionText).toContain('11111111');
    expect(sectionText).toContain('Soft drift');
    expect(sectionText).toContain('ui:replay-panel');
  });

  it('omits the replay attempts surface when the bundle has none', () => {
    const fixture = TestBed.createComponent(TraceBundlePanelComponent);
    fixture.componentRef.setInput('traceId', 'trace-no-replays');
    fixture.detectChanges();

    httpMock.expectOne('/api/traces/trace-no-replays/bundle/manifest')
      .flush(buildManifest({}));
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="bundle-replay-attempts"]'))
      .toBeNull();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).not.toContain('replay attempts');
  });

  it('shows a friendly empty state when the manifest endpoint returns 404', () => {
    const fixture = TestBed.createComponent(TraceBundlePanelComponent);
    fixture.componentRef.setInput('traceId', 'unknown');
    fixture.detectChanges();

    httpMock.expectOne('/api/traces/unknown/bundle/manifest')
      .flush({ message: 'not found' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('No bundle available yet');
  });
});

function buildManifest(overrides: {
  refusals?: TraceBundleRefusal[];
  authoritySnapshots?: TraceBundleAuthoritySnapshot[];
  artifacts?: TraceBundleArtifactRef[];
  replayAttempts?: TraceBundleReplayAttempt[];
}): TraceBundleManifest {
  return {
    schemaVersion: 'codeflow.trace-bundle.v1',
    generatedAtUtc: '2026-04-30T17:00:00Z',
    trace: {
      traceId: 'trace-a',
      rootSaga: {
        correlationId: 'cor-1',
        traceId: 'trace-a',
        parentTraceId: null,
        subflowDepth: 0,
        workflowKey: 'demo',
        workflowVersion: 1,
        currentState: 'Completed',
        failureReason: null,
        createdAtUtc: '2026-04-30T16:00:00Z',
        updatedAtUtc: '2026-04-30T17:00:00Z',
        pinnedAgentVersions: { 'agent-a': 1 },
      },
      subflowSagas: [],
      decisions: [],
      refusals: overrides.refusals ?? [],
      authoritySnapshots: overrides.authoritySnapshots ?? [],
      tokenUsage: { recordCount: 0, records: [] },
      replayAttempts: overrides.replayAttempts,
    },
    artifacts: overrides.artifacts ?? [],
  };
}

function refusal(id: string, stage: string, code: string): TraceBundleRefusal {
  return {
    id,
    traceId: 'trace-a',
    assistantConversationId: null,
    stage,
    code,
    reason: `${code} reason`,
    axis: stage === 'tool' ? 'execute' : null,
    path: null,
    detailJson: null,
    occurredAtUtc: '2026-04-30T17:00:00Z',
  };
}

function authoritySnapshot(id: string, agentKey: string, blockedAxesJson: string): TraceBundleAuthoritySnapshot {
  return {
    id,
    traceId: 'trace-a',
    roundId: 'round-1',
    agentKey,
    agentVersion: 1,
    workflowKey: 'demo',
    workflowVersion: 1,
    envelopeJson: '{}',
    blockedAxesJson,
    tiersJson: '[]',
    resolvedAtUtc: '2026-04-30T17:00:00Z',
  };
}

function artifactRef(bundlePath: string, sha256: string): TraceBundleArtifactRef {
  return {
    bundlePath,
    sha256,
    sizeBytes: sha256 ? 16 : 0,
    contentType: 'text/plain',
    originalRef: bundlePath,
  };
}
