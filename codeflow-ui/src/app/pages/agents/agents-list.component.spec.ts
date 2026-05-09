import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AgentSummary } from '../../core/models';
import { AgentsListComponent } from './agents-list.component';

/**
 * AP-6 (sc-837): per-row Export button. Verifies the click-to-download path hits
 * `GET /api/agents/{key}/{version}/package`, parses the suggested filename out of the
 * `Content-Disposition` header, and triggers a browser save with a Blob URL. The agents
 * card is wrapped in a `<a routerLink>`; the Export click handler must `preventDefault`
 * so the navigation never fires.
 */
describe('AgentsListComponent — per-row Export (AP-6)', () => {
  let httpMock: HttpTestingController;
  // Captures every save the component triggers. The component creates an anchor element
  // and `.click()`s it; we patch click + URL APIs to record what would have been saved.
  let savedBlobs: Array<{ blob: Blob; fileName: string }>;
  let capturedBlob: Blob | null = null;
  let originalCreateObjectURL: typeof URL.createObjectURL | undefined;
  let originalRevokeObjectURL: typeof URL.revokeObjectURL | undefined;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AgentsListComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    savedBlobs = [];
    capturedBlob = null;

    // JSDOM's URL doesn't define createObjectURL by default. Define + record so the
    // component's saveBlobToDisk path runs end-to-end without exploding.
    originalCreateObjectURL = (URL as { createObjectURL?: typeof URL.createObjectURL }).createObjectURL;
    originalRevokeObjectURL = (URL as { revokeObjectURL?: typeof URL.revokeObjectURL }).revokeObjectURL;
    (URL as unknown as { createObjectURL: (blob: Blob) => string }).createObjectURL = (blob: Blob) => {
      capturedBlob = blob;
      return 'blob:fake';
    };
    (URL as unknown as { revokeObjectURL: (url: string) => void }).revokeObjectURL = () => undefined;

    // Patch document.createElement to capture each anchor's `click()` call. The component
    // assigns href + download then triggers click — we don't want a real navigation.
    const realCreate = document.createElement.bind(document);
    vi.spyOn(document, 'createElement').mockImplementation((tag: string) => {
      const element = realCreate(tag) as HTMLElement;
      if (tag === 'a') {
        const anchor = element as HTMLAnchorElement;
        anchor.click = () => {
          savedBlobs.push({ blob: capturedBlob!, fileName: anchor.download });
        };
      }
      return element;
    });
  });

  afterEach(() => {
    httpMock.verify();
    vi.restoreAllMocks();
    if (originalCreateObjectURL) {
      (URL as { createObjectURL: typeof URL.createObjectURL }).createObjectURL = originalCreateObjectURL;
    } else {
      delete (URL as { createObjectURL?: typeof URL.createObjectURL }).createObjectURL;
    }
    if (originalRevokeObjectURL) {
      (URL as { revokeObjectURL: typeof URL.revokeObjectURL }).revokeObjectURL = originalRevokeObjectURL;
    } else {
      delete (URL as { revokeObjectURL?: typeof URL.revokeObjectURL }).revokeObjectURL;
    }
  });

  function makeAgent(over: Partial<AgentSummary> = {}): AgentSummary {
    return {
      key: 'demo-writer',
      latestVersion: 3,
      latestCreatedAtUtc: new Date().toISOString(),
      latestCreatedBy: 'tester',
      type: 'agent',
      provider: 'openai',
      model: 'gpt-5.4',
      tags: ['demo'],
      isRetired: false,
      ...over,
    } as AgentSummary;
  }

  it('hits the export endpoint and saves with the filename from Content-Disposition', () => {
    const fixture = TestBed.createComponent(AgentsListComponent);
    fixture.detectChanges();

    // Fulfill the initial agents list call so the component is in the "loaded" state.
    httpMock.expectOne('/api/agents').flush([]);

    const component = fixture.componentInstance;
    const agent = makeAgent();
    component.downloadPackage(new MouseEvent('click'), agent);

    const req = httpMock.expectOne('/api/agents/demo-writer/3/package');
    expect(req.request.method).toBe('GET');
    expect(req.request.responseType).toBe('blob');

    // Backend sets a Content-Disposition with the canonical name; the parser pulls it out.
    const blob = new Blob(['{}'], { type: 'application/json' });
    req.flush(blob, {
      headers: {
        'content-disposition': 'attachment; filename="demo-writer-v3-agent-package.json"',
        'content-type': 'application/json',
      },
    });

    expect(savedBlobs).toHaveLength(1);
    expect(savedBlobs[0].fileName).toBe('demo-writer-v3-agent-package.json');
    expect(component.exportingKey()).toBeNull();
    expect(component.exportError()).toBeNull();
  });

  it('falls back to a synthesized filename when Content-Disposition is absent', () => {
    const fixture = TestBed.createComponent(AgentsListComponent);
    fixture.detectChanges();
    httpMock.expectOne('/api/agents').flush([]);

    const component = fixture.componentInstance;
    component.downloadPackage(new MouseEvent('click'), makeAgent({ key: 'writer/main', latestVersion: 7 }));

    const req = httpMock.expectOne('/api/agents/writer%2Fmain/7/package');
    req.flush(new Blob(['{}'], { type: 'application/json' }));

    expect(savedBlobs).toHaveLength(1);
    expect(savedBlobs[0].fileName).toBe('writer/main-v7-agent-package.json');
  });

  it('surfaces an error message when the server returns a problem', () => {
    const fixture = TestBed.createComponent(AgentsListComponent);
    fixture.detectChanges();
    httpMock.expectOne('/api/agents').flush([]);

    const component = fixture.componentInstance;
    component.downloadPackage(new MouseEvent('click'), makeAgent());

    const req = httpMock.expectOne('/api/agents/demo-writer/3/package');
    // Blob responseType — error body must also be a Blob, not a JSON object.
    req.flush(new Blob(['{"title":"Agent package export failed"}'], { type: 'application/json' }), {
      status: 422,
      statusText: 'Unprocessable Entity',
    });

    expect(component.exportingKey()).toBeNull();
    expect(component.exportError()).toBeTruthy();
    expect(savedBlobs).toHaveLength(0);
  });

  it('preventDefaults the click so the row anchor does not navigate', () => {
    const fixture = TestBed.createComponent(AgentsListComponent);
    fixture.detectChanges();
    httpMock.expectOne('/api/agents').flush([]);

    const event = new MouseEvent('click', { cancelable: true });
    const preventSpy = vi.spyOn(event, 'preventDefault');
    const stopSpy = vi.spyOn(event, 'stopPropagation');

    fixture.componentInstance.downloadPackage(event, makeAgent());

    // The pending HTTP request still fires; verify the event was tamed BEFORE network kicks in.
    expect(preventSpy).toHaveBeenCalled();
    expect(stopSpy).toHaveBeenCalled();
    httpMock.expectOne('/api/agents/demo-writer/3/package').flush(new Blob(['{}']));
  });
});
