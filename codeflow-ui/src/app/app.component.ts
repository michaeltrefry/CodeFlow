import { Component, inject } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive } from '@angular/router';
import { AuthService } from './auth/auth.service';

@Component({
  selector: 'cf-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="shell">
      <aside class="nav">
        <div class="nav-brand">
          <span class="brand-mark">◆</span>
          <span>CodeFlow</span>
        </div>
        <nav class="nav-links">
          <a routerLink="/agents" routerLinkActive="active">Agents</a>
          <a routerLink="/workflows" routerLinkActive="active">Workflows</a>
          <a routerLink="/traces" routerLinkActive="active">Traces</a>
          <a routerLink="/hitl" routerLinkActive="active">HITL Queue</a>
        </nav>
        <footer class="nav-user">
          @if (auth.currentUser(); as user) {
            <div class="nav-user-name">{{ user.name ?? user.email ?? user.id }}</div>
            <div class="nav-user-roles">
              @for (role of user.roles; track role) {
                <span class="tag accent">{{ role }}</span>
              }
            </div>
          } @else {
            <div class="nav-user-name muted">Not signed in</div>
          }
        </footer>
      </aside>
      <main class="workspace">
        <router-outlet />
      </main>
    </div>
  `,
  styles: [`
    .shell {
      display: grid;
      grid-template-columns: 220px 1fr;
      min-height: 100vh;
    }
    .nav {
      background: var(--color-surface);
      border-right: 1px solid var(--color-border);
      display: flex;
      flex-direction: column;
      padding: 1rem;
    }
    .nav-brand {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      font-size: 1.15rem;
      font-weight: 700;
      margin-bottom: 1.5rem;
    }
    .brand-mark {
      color: var(--color-accent);
    }
    .nav-links {
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
      flex: 1;
    }
    .nav-links a {
      display: block;
      padding: 0.5rem 0.75rem;
      border-radius: 4px;
      color: var(--color-text);
    }
    .nav-links a:hover {
      background: var(--color-surface-alt);
    }
    .nav-links a.active {
      background: rgba(56,189,248,0.12);
      color: var(--color-accent);
    }
    .nav-user {
      border-top: 1px solid var(--color-border);
      padding-top: 1rem;
      font-size: 0.85rem;
    }
    .nav-user-name {
      font-weight: 600;
      margin-bottom: 0.35rem;
    }
    .nav-user-name.muted {
      color: var(--color-muted);
    }
    .nav-user-roles {
      display: flex;
      flex-wrap: wrap;
      gap: 0.25rem;
    }
    .workspace {
      padding: 2rem;
      overflow-y: auto;
    }
  `]
})
export class AppComponent {
  readonly auth = inject(AuthService);

  constructor() {
    this.auth.load();
  }
}
