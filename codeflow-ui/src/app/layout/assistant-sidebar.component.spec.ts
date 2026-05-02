import { Component, Input, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { DefaultUrlSerializer, NavigationEnd, Router } from '@angular/router';
import { Subject } from 'rxjs';
import { AssistantScope } from '../core/assistant.api';
import { PageContext } from '../core/page-context';
import { PageContextService } from '../core/page-context.service';
import { ThemeService } from '../core/theme.service';
import { ChatPanelComponent } from '../ui/chat';
import { AssistantSidebarComponent } from './assistant-sidebar.component';

@Component({
  selector: 'cf-chat-panel',
  standalone: true,
  template: '',
})
class ChatPanelStubComponent {
  @Input() scope!: AssistantScope;
  @Input() pageContext!: PageContext;
  @Input() conversationIdOverride: string | null = null;
}

describe('AssistantSidebarComponent', () => {
  let fixture: ComponentFixture<AssistantSidebarComponent>;
  let pageContext: FakePageContextService;
  let theme: FakeThemeService;
  let routerEvents: Subject<NavigationEnd>;
  let router: {
    url: string;
    events: Subject<NavigationEnd>;
    parseUrl: (url: string) => ReturnType<DefaultUrlSerializer['parse']>;
  };

  beforeEach(() => {
    pageContext = new FakePageContextService();
    theme = new FakeThemeService();
    routerEvents = new Subject<NavigationEnd>();
    const serializer = new DefaultUrlSerializer();
    router = {
      url: '/',
      events: routerEvents,
      parseUrl: (url: string) => serializer.parse(url),
    };

    TestBed.configureTestingModule({
      imports: [AssistantSidebarComponent],
      providers: [
        { provide: PageContextService, useValue: pageContext },
        { provide: ThemeService, useValue: theme },
        { provide: Router, useValue: router },
      ],
    }).overrideComponent(AssistantSidebarComponent, {
      remove: { imports: [ChatPanelComponent] },
      add: { imports: [ChatPanelStubComponent] },
    });
  });

  it('renders the sidebar chat on the home route with the durable homepage scope', () => {
    pageContext.current.set({ kind: 'home' });

    fixture = TestBed.createComponent(AssistantSidebarComponent);
    fixture.detectChanges();

    const sidebar = fixture.nativeElement.querySelector('[data-testid="assistant-sidebar"]');
    const chatDebug = fixture.debugElement.query(By.directive(ChatPanelStubComponent));
    const chat = chatDebug.componentInstance as ChatPanelStubComponent;

    expect(sidebar).not.toBeNull();
    expect(chat.scope).toEqual({ kind: 'homepage' });
    expect(chat.pageContext).toEqual({ kind: 'home' });
  });

  it('preserves assistantConversation query-param resume on the home route', () => {
    router.url = '/?assistantConversation=conversation-123';
    pageContext.current.set({ kind: 'home' });

    fixture = TestBed.createComponent(AssistantSidebarComponent);
    fixture.detectChanges();

    const chat = fixture.debugElement.query(By.directive(ChatPanelStubComponent))
      .componentInstance as ChatPanelStubComponent;

    expect(chat.conversationIdOverride).toBe('conversation-123');
  });

  it('continues to forward non-home page context through the same homepage scope', () => {
    pageContext.current.set({ kind: 'traces-list' });

    fixture = TestBed.createComponent(AssistantSidebarComponent);
    fixture.detectChanges();

    const chat = fixture.debugElement.query(By.directive(ChatPanelStubComponent))
      .componentInstance as ChatPanelStubComponent;

    expect(chat.scope).toEqual({ kind: 'homepage' });
    expect(chat.pageContext).toEqual({ kind: 'traces-list' });
  });

  it('does not mount chat while collapsed', () => {
    theme.setAssistantSidebarMode('collapsed');

    fixture = TestBed.createComponent(AssistantSidebarComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="assistant-sidebar-expand"]')).not.toBeNull();
    expect(fixture.debugElement.query(By.directive(ChatPanelStubComponent))).toBeNull();
  });

  it('switches between docked and expanded without remounting chat', () => {
    fixture = TestBed.createComponent(AssistantSidebarComponent);
    fixture.detectChanges();

    const initialChat = fixture.debugElement.query(By.directive(ChatPanelStubComponent))
      .componentInstance as ChatPanelStubComponent;
    const sidebar = fixture.nativeElement.querySelector('[data-testid="assistant-sidebar"]') as HTMLElement;
    const toggle = fixture.nativeElement.querySelector('[data-testid="assistant-sidebar-mode-toggle"]') as HTMLButtonElement;

    expect(sidebar.getAttribute('data-mode')).toBe('docked');

    toggle.click();
    fixture.detectChanges();

    const expandedChat = fixture.debugElement.query(By.directive(ChatPanelStubComponent))
      .componentInstance as ChatPanelStubComponent;
    expect(sidebar.getAttribute('data-mode')).toBe('expanded');
    expect(expandedChat).toBe(initialChat);
    expect(toggle.getAttribute('aria-pressed')).toBe('true');

    toggle.click();
    fixture.detectChanges();

    const dockedChat = fixture.debugElement.query(By.directive(ChatPanelStubComponent))
      .componentInstance as ChatPanelStubComponent;
    expect(sidebar.getAttribute('data-mode')).toBe('docked');
    expect(dockedChat).toBe(initialChat);
  });
});

class FakePageContextService {
  readonly current = signal<PageContext>({ kind: 'home' });
}

class FakeThemeService {
  readonly assistantSidebarMode = signal<'collapsed' | 'docked' | 'expanded'>('docked');
  readonly assistantSidebarCollapsed = signal(false);
  readonly setAssistantSidebarCollapsed = vi.fn((collapsed: boolean) => {
    this.setAssistantSidebarMode(collapsed ? 'collapsed' : 'docked');
  });
  readonly setAssistantSidebarMode = vi.fn((mode: 'collapsed' | 'docked' | 'expanded') => {
    this.assistantSidebarMode.set(mode);
    this.assistantSidebarCollapsed.set(mode === 'collapsed');
  });
}
