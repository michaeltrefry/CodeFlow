import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { ChatPanelComponent } from '../../ui/chat';
import { AuthService } from '../../auth/auth.service';
import { HomeRailComponent } from './home-rail.component';
import { ButtonComponent } from '../../ui/button.component';
import { IconComponent } from '../../ui/icon.component';

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
 * Anonymous visitors land on a public page with no chat panel mounted. The assistant is an
 * authenticated feature only, so guests cannot spend LLM tokens.
 */
@Component({
  selector: 'cf-home-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChatPanelComponent, HomeRailComponent, ButtonComponent, IconComponent],
  template: `
    @if (auth.loading()) {
      <main class="auth-wait" data-testid="home-auth-loading">
        <cf-icon name="codeflowApp" [sizeOverride]="34"></cf-icon>
        <span>Checking session...</span>
      </main>
    } @else if (isAuthenticated()) {
      <main class="home" data-testid="home-page">
        <section class="home-chat">
          <cf-chat-panel
            [scope]="{ kind: 'homepage' }"
            [conversationIdOverride]="selectedConversationId()"
            layout="wide"
          />
        </section>
        <aside class="home-rail" aria-label="Homepage rail">
          <header class="rail-head">
            <span class="rail-eyebrow">Assistant</span>
            <h2 class="rail-title">CodeFlow copilot</h2>
          </header>
          <cf-home-rail />
        </aside>
      </main>
    } @else {
      <main class="landing" data-testid="public-landing">
        <section class="landing-copy" aria-labelledby="landing-title">
          <div class="landing-brand">
            <cf-icon name="codeflowApp" [sizeOverride]="38"></cf-icon>
            <span>CodeFlow</span>
          </div>
          <h1 id="landing-title">Design, run, and inspect agent workflows.</h1>
          <p class="landing-lede">
            CodeFlow is the private control surface for multi-agent workflow authoring,
            trace replay, HITL review, and runtime governance.
          </p>
          @if (auth.error()) {
            <p class="landing-error" data-testid="landing-auth-error">{{ auth.error() }}</p>
          }
          <button type="button" cf-button variant="primary" size="lg" icon="check" (click)="signIn()">
            Sign in
          </button>
        </section>

        <section class="product-preview" aria-label="CodeFlow workflow preview">
          <div class="preview-toolbar">
            <span></span><span></span><span></span>
            <strong>Review loop</strong>
          </div>
          <div class="preview-canvas">
            <div class="preview-node start">
              <span>Input</span>
              <strong>Ticket context</strong>
            </div>
            <div class="preview-line one"></div>
            <div class="preview-node agent">
              <span>Agent</span>
              <strong>Reviewer</strong>
            </div>
            <div class="preview-line two"></div>
            <div class="preview-node hitl">
              <span>HITL</span>
              <strong>Approval</strong>
            </div>
            <div class="preview-node trace">
              <span>Trace</span>
              <strong>Replay ready</strong>
            </div>
          </div>
        </section>
      </main>
    }
  `,
  styles: [`
    :host {
      display: flex;
      flex: 1 1 auto;
      min-height: 0;
      overflow: hidden;
    }
    .auth-wait {
      display: grid;
      place-items: center;
      gap: 12px;
      width: 100%;
      min-height: 100%;
      color: var(--text-muted, #9aa3b2);
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
    .landing {
      display: grid;
      grid-template-columns: minmax(320px, 0.92fr) minmax(420px, 1.08fr);
      gap: 48px;
      align-items: center;
      width: 100%;
      max-width: 1180px;
      margin: 0 auto;
      padding: 56px 32px;
      min-height: 100%;
    }
    .landing-copy {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      gap: 18px;
    }
    .landing-brand {
      display: inline-flex;
      align-items: center;
      gap: 12px;
      color: var(--text, #E7E9EE);
      font-weight: 700;
      font-size: 16px;
    }
    .landing h1 {
      margin: 0;
      max-width: 680px;
      color: var(--text, #E7E9EE);
      font-size: clamp(42px, 6vw, 76px);
      line-height: 0.96;
      letter-spacing: 0;
    }
    .landing-lede {
      margin: 0;
      max-width: 560px;
      color: var(--text-muted, #9aa3b2);
      font-size: 17px;
      line-height: 1.55;
    }
    .landing-error {
      margin: 0;
      padding: 12px 14px;
      border: 1px solid rgba(248, 81, 73, 0.35);
      background: rgba(248, 81, 73, 0.12);
      color: #ffc9c5;
      border-radius: 6px;
      line-height: 1.45;
    }
    .product-preview {
      min-height: 430px;
      border: 1px solid var(--border, rgba(255,255,255,0.08));
      background:
        linear-gradient(180deg, rgba(231, 233, 238, 0.07), rgba(231, 233, 238, 0.02)),
        var(--surface, #131519);
      border-radius: 8px;
      overflow: hidden;
      box-shadow: 0 24px 70px rgba(0, 0, 0, 0.32);
    }
    .preview-toolbar {
      display: flex;
      align-items: center;
      gap: 7px;
      height: 44px;
      padding: 0 16px;
      border-bottom: 1px solid var(--border, rgba(255,255,255,0.08));
      color: var(--text-muted, #9aa3b2);
      font-size: 12px;
    }
    .preview-toolbar span {
      width: 9px;
      height: 9px;
      border-radius: 50%;
      background: rgba(231, 233, 238, 0.22);
    }
    .preview-toolbar strong {
      margin-left: 10px;
      color: var(--text, #E7E9EE);
      font-weight: 600;
    }
    .preview-canvas {
      position: relative;
      min-height: 386px;
      background-image:
        linear-gradient(rgba(255,255,255,0.035) 1px, transparent 1px),
        linear-gradient(90deg, rgba(255,255,255,0.035) 1px, transparent 1px);
      background-size: 28px 28px;
    }
    .preview-node {
      position: absolute;
      width: 170px;
      padding: 16px;
      border: 1px solid rgba(231, 233, 238, 0.14);
      border-radius: 8px;
      background: rgba(16, 21, 31, 0.92);
      box-shadow: 0 16px 38px rgba(0,0,0,0.24);
    }
    .preview-node span {
      display: block;
      color: var(--text-muted, #9aa3b2);
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
    }
    .preview-node strong {
      display: block;
      margin-top: 7px;
      color: var(--text, #E7E9EE);
      font-size: 15px;
    }
    .preview-node.start { left: 42px; top: 62px; }
    .preview-node.agent { left: 245px; top: 150px; border-color: rgba(6, 214, 160, 0.34); }
    .preview-node.hitl { right: 52px; top: 76px; border-color: rgba(188, 140, 255, 0.36); }
    .preview-node.trace { right: 116px; bottom: 54px; }
    .preview-line {
      position: absolute;
      height: 2px;
      background: rgba(231, 233, 238, 0.24);
      transform-origin: left center;
    }
    .preview-line.one {
      left: 196px;
      top: 145px;
      width: 74px;
      transform: rotate(24deg);
    }
    .preview-line.two {
      left: 414px;
      top: 166px;
      width: 102px;
      transform: rotate(-26deg);
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
      .landing {
        grid-template-columns: 1fr;
        padding: 32px 20px;
      }
      .product-preview {
        min-height: 360px;
      }
      .preview-canvas {
        min-height: 316px;
      }
    }
    @media (max-width: 640px) {
      .landing h1 {
        font-size: 40px;
      }
      .product-preview {
        display: none;
      }
    }
  `],
})
export class HomeComponent {
  protected readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly queryParamMap = toSignal(this.route.queryParamMap, {
    initialValue: this.route.snapshot.queryParamMap,
  });

  protected readonly isAuthenticated = computed(() => this.auth.currentUser() !== null);
  protected readonly selectedConversationId = computed(() => this.queryParamMap().get('assistantConversation'));

  protected signIn(): void {
    this.auth.login();
  }
}
