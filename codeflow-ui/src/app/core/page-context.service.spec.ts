import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Subject } from 'rxjs';
import { NavigationEnd } from '@angular/router';
import { PageContextService } from './page-context.service';

describe('PageContextService', () => {
  function createService(initialUrl: string): { service: PageContextService; events$: Subject<unknown>; routerStub: { url: string } } {
    const events$ = new Subject<unknown>();
    const routerStub = { url: initialUrl, events: events$ };
    TestBed.configureTestingModule({
      providers: [
        PageContextService,
        { provide: Router, useValue: routerStub },
      ],
    });
    return { service: TestBed.inject(PageContextService), events$, routerStub };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('classifies bare root path as home', () => {
    const { service } = createService('/');
    expect(service.current()).toEqual({ kind: 'home' });
  });

  // Regression: when chat-panel forks/resumes a thread it appends `?assistantConversation=…`.
  // The service used to only match the literal `/` path so the homepage suddenly classified as
  // `other`, which made the assistant sidebar mount on top of the home page's primary chat.
  it('still classifies root path as home when query params are present', () => {
    const { service } = createService('/?assistantConversation=dabb26e9-f571-416e-baaf-d4f1d9e608ba');
    expect(service.current()).toEqual({ kind: 'home' });
  });

  it('classifies /traces and /traces?... as traces-list', () => {
    const a = createService('/traces').service;
    expect(a.current()).toEqual({ kind: 'traces-list' });
    TestBed.resetTestingModule();
    const b = createService('/traces?status=running').service;
    expect(b.current()).toEqual({ kind: 'traces-list' });
  });

  it('classifies any other route as other and preserves the original route string', () => {
    const { service } = createService('/agents/my-agent?tab=runs');
    expect(service.current()).toEqual({ kind: 'other', route: '/agents/my-agent?tab=runs' });
  });

  it('updates classification on NavigationEnd events', () => {
    const { service, events$ } = createService('/');
    expect(service.current()).toEqual({ kind: 'home' });

    events$.next(new NavigationEnd(1, '/agents', '/agents'));
    expect(service.current()).toEqual({ kind: 'other', route: '/agents' });

    events$.next(new NavigationEnd(2, '/?assistantConversation=abc', '/?assistantConversation=abc'));
    expect(service.current()).toEqual({ kind: 'home' });
  });

  it('lets a registered context override the route fallback', () => {
    const { service } = createService('/agents/foo');
    service.set({ kind: 'agent-editor', agentId: 'foo' });
    expect(service.current()).toEqual({ kind: 'agent-editor', agentId: 'foo' });
    service.clear();
    expect(service.current()).toEqual({ kind: 'other', route: '/agents/foo' });
  });
});
