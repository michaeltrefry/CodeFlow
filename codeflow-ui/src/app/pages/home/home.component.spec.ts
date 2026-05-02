import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService, CurrentUser } from '../../auth/auth.service';
import { WorkflowSummary } from '../../core/models';
import { WorkflowsApi } from '../../core/workflows.api';
import { HomeComponent } from './home.component';

describe('HomeComponent', () => {
  let fixture: ComponentFixture<HomeComponent>;
  let auth: FakeAuthService;
  let workflowsApi: { list: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    auth = new FakeAuthService();
    workflowsApi = { list: vi.fn(() => of([])) };
    TestBed.configureTestingModule({
      imports: [HomeComponent],
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        { provide: WorkflowsApi, useValue: workflowsApi },
      ],
    });
  });

  it('renders a public landing page without mounting chat for anonymous users', () => {
    fixture = TestBed.createComponent(HomeComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="public-landing"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('cf-chat-panel')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Sign in');
    expect(workflowsApi.list).not.toHaveBeenCalled();
  });

  it('starts login from the public landing page CTA', () => {
    fixture = TestBed.createComponent(HomeComponent);
    fixture.detectChanges();

    const button = fixture.nativeElement.querySelector('button[cf-button]') as HTMLButtonElement;
    button.click();

    expect(auth.login).toHaveBeenCalled();
  });

  it('shows a session check state while auth is loading', () => {
    auth.loading.set(true);
    fixture = TestBed.createComponent(HomeComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="home-auth-loading"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('cf-chat-panel')).toBeNull();
  });

  it('renders the authenticated getting-started page without a homepage-owned chat panel', () => {
    auth.currentUser.set(user());
    workflowsApi.list.mockReturnValue(of([workflow('workflow-a', 'Review loop')]));

    fixture = TestBed.createComponent(HomeComponent);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(fixture.nativeElement.querySelector('[data-testid="home-page"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('cf-chat-panel')).toBeNull();
    expect(text).toContain('Create an Agent');
    expect(text).toContain('Create a Workflow');
    expect(text).toContain('Run a Workflow');
    expect(text).toContain('Review loop');
    expect((fixture.nativeElement.querySelector('a[href="/workflows"]') as HTMLAnchorElement | null)?.textContent)
      .toContain('Run a Workflow');
  });

  it('lists only runnable workflows and links them to trace submit with the workflow key', () => {
    auth.currentUser.set(user());
    workflowsApi.list.mockReturnValue(of([
      workflow('workflow-b', 'Runnable B'),
      workflow('subflow-a', 'Subflow A', { category: 'Subflow' }),
      workflow('loop-a', 'Loop A', { category: 'Loop' }),
      workflow('retired-a', 'Retired A', { isRetired: true }),
      workflow('workflow-a', 'Runnable A'),
    ]));

    fixture = TestBed.createComponent(HomeComponent);
    fixture.detectChanges();

    const rows = Array.from(fixture.nativeElement.querySelectorAll('.workflow-row')) as HTMLAnchorElement[];
    expect(rows.map(row => row.textContent?.trim())).toEqual([
      expect.stringContaining('Runnable A'),
      expect.stringContaining('Runnable B'),
    ]);
    expect(fixture.nativeElement.textContent).not.toContain('Subflow A');
    expect(fixture.nativeElement.textContent).not.toContain('Loop A');
    expect(fixture.nativeElement.textContent).not.toContain('Retired A');
    expect(rows[0].getAttribute('href')).toContain('/traces/new?workflow=workflow-a');
  });

  it('shows a compact empty state when no runnable workflows exist', () => {
    auth.currentUser.set(user());
    workflowsApi.list.mockReturnValue(of([workflow('subflow-a', 'Subflow A', { category: 'Subflow' })]));

    fixture = TestBed.createComponent(HomeComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="home-workflows-empty"]')).not.toBeNull();
    expect(fixture.nativeElement.textContent).toContain('No runnable workflows yet.');
  });

  it('shows a compact workflow loading error', () => {
    auth.currentUser.set(user());
    workflowsApi.list.mockReturnValue(throwError(() => new Error('workflow service unavailable')));

    fixture = TestBed.createComponent(HomeComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="home-workflows-error"]')).not.toBeNull();
    expect(fixture.nativeElement.textContent).toContain('workflow service unavailable');
  });
});

class FakeAuthService {
  readonly currentUser = signal<CurrentUser | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly login = vi.fn();
}

function user(): CurrentUser {
  return {
    id: 'user-1',
    email: 'user@example.com',
    name: 'User One',
    roles: [],
  };
}

function workflow(
  key: string,
  name: string,
  overrides: Partial<WorkflowSummary> = {},
): WorkflowSummary {
  return {
    key,
    name,
    latestVersion: 1,
    category: 'Workflow',
    tags: [],
    nodeCount: 1,
    edgeCount: 0,
    inputCount: 0,
    createdAtUtc: '2026-05-02T15:00:00Z',
    isRetired: false,
    ...overrides,
  };
}
