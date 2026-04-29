import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { ChatPanelComponent } from '../../ui/chat';
import { AuthService } from '../../auth/auth.service';
import { HomeRailComponent } from './home-rail.component';

/**
 * HAA-6: Homepage shell. Replaces "land on Traces" as the default landing experience.
 *
 * Layout: chat-first, with a thin right-side context rail. HAA-14 fills the rail with live
 * sections (assistant token chip, resume conversations, recent traces, recently used
 * workflows). Demo mode keeps a stripped-down version: just the resume-conversation slot —
 * the others require live tool access — plus the existing capability blurb so anonymous
 * users still understand what they can ask.
 *
 * The chat panel mounts with <c>scope: { kind: 'homepage' }</c> so all conversations the user
 * has on the homepage land in the same persistent thread, distinct from any entity-scoped
 * sidebar conversations that ship in HAA-7.
 *
 * Anonymous visitors land here in demo mode: the backend mints a cookie-backed synthetic id
 * and runs the chat with no tool access (system-prompt knowledge only). The rail copy branches
 * to surface that and to nudge sign-in for live data access.
 */
@Component({
  selector: 'cf-home-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChatPanelComponent, HomeRailComponent],
  template: `
    <main class="home" data-testid="home-page">
      <section class="home-chat">
        <cf-chat-panel
          [scope]="{ kind: 'homepage' }"
          [conversationIdOverride]="selectedConversationId()"
        />
      </section>
      <aside class="home-rail" aria-label="Homepage rail">
        <header class="rail-head">
          <span class="rail-eyebrow">Assistant</span>
          <h2 class="rail-title">CodeFlow copilot</h2>
        </header>
        @if (isDemo()) {
          <p class="rail-blurb">
            You're previewing CodeFlow. The assistant can answer general questions about
            authoring concepts, ports, traces, and runtime behavior, but live tools are off
            until you sign in.
          </p>
          <p class="rail-foot rail-foot-cta" data-testid="rail-demo-banner">
            Demo mode — sign in for live data access.
          </p>
          <cf-home-rail [demoMode]="true" />
        } @else {
          <cf-home-rail />
        }
      </aside>
    </main>
  `,
  styles: [`
    :host {
      display: flex;
      flex: 1 1 auto;
      min-height: 0;
      overflow: hidden;
    }
    .home {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 320px;
      grid-template-rows: minmax(0, 1fr);
      gap: 24px;
      padding: 24px;
      width: 100%;
      height: 100%;
      max-width: 1480px;
      margin: 0 auto;
      min-height: 0;
      overflow: hidden;
    }
    .home-chat {
      display: flex;
      flex-direction: column;
      min-height: 0;
      min-width: 0;
    }
    .home-chat cf-chat-panel {
      flex: 1 1 auto;
      display: flex;
      flex-direction: column;
      min-height: 0;
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
    .rail-foot-cta {
      color: var(--text, #E7E9EE);
      font-weight: 600;
    }
    @media (max-width: 1080px) {
      .home {
        grid-template-columns: 1fr;
        grid-template-rows: minmax(0, 1fr) auto;
        overflow-y: auto;
      }
      .home-chat {
        min-height: min(640px, calc(100vh - 112px));
      }
      .home-rail {
        position: static;
      }
    }
  `],
})
export class HomeComponent {
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly queryParamMap = toSignal(this.route.queryParamMap, {
    initialValue: this.route.snapshot.queryParamMap,
  });

  // True iff bootstrap completed and we resolved no current user. We deliberately key off
  // currentUser() rather than tokenAcceptedByApi(): the chat backend's cookie-driven anon
  // identity makes "no real user resolved" the right gate for demo-mode rail copy.
  protected readonly isDemo = computed(() => !this.auth.loading() && this.auth.currentUser() === null);
  protected readonly selectedConversationId = computed(() => this.queryParamMap().get('assistantConversation'));
}
