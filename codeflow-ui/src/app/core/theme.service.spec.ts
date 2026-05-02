import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { ThemeService } from './theme.service';

describe('ThemeService', () => {
  let store: Record<string, string>;
  let doc: Document;

  beforeEach(() => {
    store = {};
    doc = document.implementation.createHTMLDocument('theme');
    Object.defineProperty(doc, 'defaultView', {
      value: {
        localStorage: {
          getItem: vi.fn((key: string) => store[key] ?? null),
          setItem: vi.fn((key: string, value: string) => { store[key] = value; }),
        },
      },
    });
  });

  afterEach(() => TestBed.resetTestingModule());

  it('defaults the assistant sidebar to docked mode', () => {
    const service = createService();

    expect(service.assistantSidebarMode()).toBe('docked');
    expect(service.assistantSidebarCollapsed()).toBe(false);
  });

  it('persists expanded assistant sidebar mode', () => {
    const service = createService();

    service.setAssistantSidebarMode('expanded');
    TestBed.flushEffects();

    expect(service.assistantSidebarMode()).toBe('expanded');
    expect(JSON.parse(store['cf.ui.tweaks']).assistantSidebarMode).toBe('expanded');
  });

  it('hydrates legacy collapsed boolean into the new mode', () => {
    store['cf.ui.tweaks'] = JSON.stringify({ assistantSidebarCollapsed: true });

    const service = createService();

    expect(service.assistantSidebarMode()).toBe('collapsed');
    expect(service.assistantSidebarCollapsed()).toBe(true);
  });

  function createService(): ThemeService {
    TestBed.configureTestingModule({
      providers: [
        ThemeService,
        { provide: DOCUMENT, useValue: doc },
      ],
    });
    return TestBed.inject(ThemeService);
  }
});
