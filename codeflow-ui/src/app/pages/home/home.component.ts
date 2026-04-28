import { ChangeDetectionStrategy, Component } from '@angular/core';
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
 */
@Component({
  selector: 'cf-home-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChatPanelComponent],
  template: `
    <main class="home" data-testid="home-page">
      <section class="home-chat">
        <cf-chat-panel [scope]="{ kind: 'homepage' }" />
      </section>
      <aside class="home-rail" aria-label="Homepage rail">
        <header class="rail-head">
          <span class="rail-eyebrow">Assistant</span>
          <h2 class="rail-title">CodeFlow copilot</h2>
        </header>
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
export class HomeComponent {}
