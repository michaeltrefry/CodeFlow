import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { AuthService } from '../../auth/auth.service';
import { ChatPanelComponent } from '../../ui/chat';

/**
 * HAA-6: Homepage shell. Replaces "land on Traces" as the default landing experience.
 *
 * Layout: chat-first, with a thin right-side context rail placeholder. The rail is filled in
 * HAA-14 (recent traces, library shortcuts, resume-conversation list, token-usage indicator);
 * for now it's a static empty-state hint pointing the user at the chat and listing capabilities.
 *
 * The chat panel mounts with <c>scope: { kind: 'homepage' }</c> so all conversations the user
 * has on the homepage land in the same persistent thread, distinct from any entity-scoped
 * sidebar conversations that ship in HAA-7.
 *
 * HAA-6-FOLLOWUP: this route is reachable WITHOUT auth. Logged-out visitors get a demo-mode
 * chat (system-prompt knowledge only — no live tool access) and the rail copy explains the
 * distinction so the empty tool surface isn't surprising.
 */
@Component({
  selector: 'cf-home-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChatPanelComponent],
  template: `
    <main class="home" data-testid="home-page" [attr.data-mode]="isDemoMode() ? 'demo' : 'authenticated'">
      <section class="home-chat">
        <cf-chat-panel [scope]="{ kind: 'homepage' }" />
      </section>
      <aside class="home-rail" aria-label="Homepage rail">
        <header class="rail-head">
          <span class="rail-eyebrow">Assistant</span>
          <h2 class="rail-title">CodeFlow copilot</h2>
        </header>
        @if (isDemoMode()) {
          <p class="rail-badge" data-testid="rail-demo-badge">Demo mode — sign in for live tools</p>
          <p class="rail-blurb">
            You're chatting with the CodeFlow assistant in read-only demo mode. It can talk
            through concepts but can't reach your library or runtime — sign in to unlock the
            full toolset.
          </p>
          <ul class="rail-list">
            <li><strong>Available now.</strong> Authoring concepts (ports, scripting, subflows, swarms, transforms, HITL) and runtime concepts (traces, replay-with-edit, token tracking).</li>
            <li><strong>After sign-in.</strong> Live workflow / agent / trace lookups, token-usage queries, drafting, and end-to-end failure diagnosis.</li>
          </ul>
          <p class="rail-foot">
            <button type="button" class="rail-signin" (click)="signIn()" data-testid="rail-signin">Sign in to enable tools</button>
          </p>
        } @else {
          <p class="rail-blurb">
            Ask about workflows, agents, traces, or runs. The assistant has live access to your
            library and can pull a trace's timeline or token usage on demand.
          </p>
          <ul class="rail-list">
            <li><strong>Knowledge.</strong> Authoring concepts (ports, scripting, subflows, swarms, transforms, HITL) and runtime concepts (traces, replay-with-edit, token tracking).</li>
            <li><strong>Live state.</strong> "Which workflows use agent X?" · "Failed traces yesterday for Y?" · "Token cost of trace Z?"</li>
            <li><strong>Coming soon.</strong> Drafting workflows, running them from chat, diagnosing failures end-to-end.</li>
          </ul>
          <p class="rail-foot">
            The trace inspector is still one click away in the side nav.
          </p>
        }
      </aside>
    </main>
  `,
  styles: [`
    .home {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 320px;
      gap: 24px;
      padding: 24px;
      max-width: 1480px;
      margin: 0 auto;
      min-height: calc(100vh - 64px);
    }
    .home-chat {
      display: flex;
      flex-direction: column;
      min-height: 0;
      min-width: 0;
    }
    .home-chat cf-chat-panel {
      flex: 1 1 auto;
      display: block;
      min-height: 560px;
    }
    .home-rail {
      display: flex;
      flex-direction: column;
      gap: 14px;
      padding: 18px;
      background: var(--surface, #131519);
      border: 1px solid var(--border, rgba(255,255,255,0.08));
      border-radius: var(--radius-md, 8px);
      align-self: start;
      position: sticky;
      top: 24px;
    }
    .rail-eyebrow {
      display: block;
      font-size: 11px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: var(--text-muted, #9aa3b2);
    }
    .rail-title {
      margin: 4px 0 0 0;
      font-size: 18px;
      color: var(--text, #E7E9EE);
    }
    .rail-blurb {
      margin: 0;
      color: var(--text-muted, #9aa3b2);
      font-size: var(--fs-md, 13px);
      line-height: 1.5;
    }
    .rail-list {
      list-style: none;
      padding: 0;
      margin: 0;
      display: flex;
      flex-direction: column;
      gap: 10px;
      font-size: var(--fs-sm, 12px);
      color: var(--text, #E7E9EE);
      line-height: 1.5;
    }
    .rail-list li {
      padding-left: 14px;
      border-left: 2px solid var(--border, rgba(255,255,255,0.08));
    }
    .rail-list strong {
      color: var(--text, #E7E9EE);
      font-weight: 600;
    }
    .rail-foot {
      margin: 0;
      padding-top: 8px;
      border-top: 1px solid var(--border, rgba(255,255,255,0.08));
      color: var(--text-muted, #9aa3b2);
      font-size: 11px;
    }
    .rail-badge {
      margin: 0;
      padding: 6px 10px;
      background: rgba(214, 158, 46, 0.12);
      color: var(--sem-amber, #d69e2e);
      border: 1px solid rgba(214, 158, 46, 0.35);
      border-radius: var(--radius-sm, 4px);
      font-size: 11px;
      letter-spacing: 0.04em;
      text-transform: uppercase;
    }
    .rail-signin {
      appearance: none;
      background: var(--accent, #4f8cff);
      color: #fff;
      border: 0;
      padding: 8px 12px;
      border-radius: var(--radius-sm, 4px);
      font-size: var(--fs-sm, 12px);
      cursor: pointer;
      width: 100%;
    }
    .rail-signin:hover {
      filter: brightness(1.1);
    }
    @media (max-width: 1080px) {
      .home {
        grid-template-columns: 1fr;
      }
      .home-rail {
        position: static;
      }
    }
  `],
})
export class HomeComponent {
  private readonly auth = inject(AuthService);

  // While the OIDC bootstrap is still in flight, suppress the demo-mode UI so an authenticated
  // visitor doesn't see a "Sign in" CTA flash before currentUser() resolves. Only flip to demo
  // once loading has settled and the user is genuinely anonymous.
  protected readonly isDemoMode = computed(() =>
    !this.auth.loading() && this.auth.currentUser() === null);

  protected signIn(): void {
    this.auth.login();
  }
}
