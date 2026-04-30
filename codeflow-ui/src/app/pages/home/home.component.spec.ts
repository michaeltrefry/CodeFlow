import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { AuthService, CurrentUser } from '../../auth/auth.service';
import { HomeComponent } from './home.component';

describe('HomeComponent', () => {
  let fixture: ComponentFixture<HomeComponent>;
  let auth: FakeAuthService;

  beforeEach(() => {
    auth = new FakeAuthService();
    TestBed.configureTestingModule({
      imports: [HomeComponent],
      providers: [
        { provide: AuthService, useValue: auth },
        {
          provide: ActivatedRoute,
          useValue: {
            queryParamMap: of(convertToParamMap({})),
            snapshot: { queryParamMap: convertToParamMap({}) },
          },
        },
      ],
    });
  });

  it('renders a public landing page without mounting chat for anonymous users', () => {
    fixture = TestBed.createComponent(HomeComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="public-landing"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('cf-chat-panel')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Sign in');
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
});

class FakeAuthService {
  readonly currentUser = signal<CurrentUser | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly login = vi.fn();
}
