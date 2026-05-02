import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { catchError, of } from 'rxjs';
import { AuthService } from '../../auth/auth.service';
import { WorkflowSummary } from '../../core/models';
import { WorkflowsApi } from '../../core/workflows.api';
import { formatHttpError } from '../../core/format-error';
import { ButtonComponent } from '../../ui/button.component';
import { IconComponent } from '../../ui/icon.component';

/**
 * HAA-6: Homepage shell. Replaces "land on Traces" as the default landing experience.
 *
 * Authenticated users get a compact getting-started surface. The assistant chat is owned by
 * AssistantSidebarComponent so the homepage never mounts a second chat panel.
 *
 * Anonymous visitors land on a public page with no chat panel mounted. The assistant is an
 * authenticated feature only, so guests cannot spend LLM tokens.
 */
@Component({
  selector: 'cf-home-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ButtonComponent, IconComponent],
  template: `
    @if (auth.loading()) {
      <main class="auth-wait" data-testid="home-auth-loading">
        <cf-icon name="codeflowApp" [sizeOverride]="34"></cf-icon>
        <span>Checking session...</span>
      </main>
    } @else if (isAuthenticated()) {
      <main class="home" data-testid="home-page">
        <section class="home-intro" aria-labelledby="home-title">
          <div class="home-kicker">
            <cf-icon name="codeflowApp" [sizeOverride]="26"></cf-icon>
            <span>CodeFlow</span>
          </div>
          <h1 id="home-title">Build agents into reliable workflows.</h1>
          <p>
            Create agents, connect them into versioned workflows, then run and inspect traces
            without leaving the workspace.
          </p>
          <div class="home-actions">
            <a routerLink="/agents/new">
              <button type="button" cf-button variant="primary" icon="plus">Create an Agent</button>
            </a>
            <a routerLink="/workflows/new">
              <button type="button" cf-button icon="workflows">Create a Workflow</button>
            </a>
            <a routerLink="/workflows">
              <button type="button" cf-button variant="ghost" icon="traces">Run a Workflow</button>
            </a>
          </div>
        </section>

        <section class="run-panel" id="run-workflow" aria-labelledby="run-workflow-title">
          <header class="run-head">
            <div>
              <span class="section-eyebrow">Run</span>
              <h2 id="run-workflow-title">Runnable workflows</h2>
            </div>
            <button type="button" cf-button size="sm" variant="ghost" icon="refresh" (click)="loadWorkflows()" [disabled]="workflowsLoading()">
              {{ workflowsLoading() ? 'Loading...' : 'Refresh' }}
            </button>
          </header>

          @if (workflowsError()) {
            <p class="state state-error" data-testid="home-workflows-error">{{ workflowsError() }}</p>
          } @else if (workflowsLoading()) {
            <p class="state" data-testid="home-workflows-loading">Loading workflows...</p>
          } @else if (runnableWorkflows().length === 0) {
            <div class="empty-state" data-testid="home-workflows-empty">
              <p>No runnable workflows yet.</p>
              <a routerLink="/workflows/new">
                <button type="button" cf-button size="sm" variant="primary" icon="plus">Create a Workflow</button>
              </a>
            </div>
          } @else {
            <div class="workflow-list" data-testid="home-runnable-workflows">
              @for (workflow of runnableWorkflows(); track workflow.key) {
                <a
                  class="workflow-row"
                  routerLink="/traces/new"
                  [queryParams]="{ workflow: workflow.key }"
                  [attr.data-testid]="'home-runnable-workflow-' + workflow.key"
                >
                  <span class="workflow-icon"><cf-icon name="traces"></cf-icon></span>
                  <span class="workflow-main">
                    <strong>{{ workflow.name }}</strong>
                    <span class="mono">{{ workflow.key }}</span>
                  </span>
                  <span class="workflow-meta">v{{ workflow.latestVersion }}</span>
                </a>
              }
            </div>
          }
        </section>
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
      display: flex;
      flex-direction: column;
      gap: 24px;
      padding: 24px;
      width: 100%;
      max-width: 1040px;
      margin: 0 auto;
      min-height: 0;
      overflow-y: auto;
    }
    .home-intro,
    .run-panel {
      border: 1px solid var(--border, rgba(255,255,255,0.08));
      background: var(--surface, #131519);
      border-radius: var(--radius-md, 8px);
      padding: 22px;
    }
    .home-intro {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      gap: 16px;
    }
    .home-kicker {
      display: inline-flex;
      align-items: center;
      gap: 10px;
      color: var(--text, #E7E9EE);
      font-weight: 700;
    }
    .home h1 {
      margin: 0;
      max-width: 720px;
      color: var(--text, #E7E9EE);
      font-size: 34px;
      line-height: 1.08;
      letter-spacing: 0;
    }
    .home-intro p {
      margin: 0;
      max-width: 700px;
      color: var(--text-muted, #9aa3b2);
      font-size: 15px;
      line-height: 1.55;
    }
    .home-actions {
      display: flex;
      flex-wrap: wrap;
      gap: 10px;
    }
    .home-actions a,
    .empty-state a {
      text-decoration: none;
    }
    .run-panel {
      display: flex;
      flex-direction: column;
      gap: 14px;
    }
    .run-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
    }
    .section-eyebrow {
      display: block;
      font-size: 11px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: var(--text-muted, #9aa3b2);
    }
    .run-head h2 {
      margin: 4px 0 0 0;
      font-size: 18px;
      color: var(--text, #E7E9EE);
    }
    .state,
    .empty-state p {
      margin: 0;
      color: var(--text-muted, #9aa3b2);
      font-size: 13px;
    }
    .state-error {
      color: #ffc9c5;
    }
    .empty-state {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      padding: 14px;
      border: 1px dashed var(--border, rgba(255,255,255,0.08));
      border-radius: 8px;
    }
    .workflow-list {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .workflow-row {
      display: grid;
      grid-template-columns: 32px minmax(0, 1fr) auto;
      align-items: center;
      gap: 12px;
      min-height: 58px;
      padding: 10px 12px;
      color: inherit;
      text-decoration: none;
      border: 1px solid var(--border, rgba(255,255,255,0.08));
      border-radius: 8px;
      background: color-mix(in oklab, var(--surface) 92%, var(--text) 8%);
    }
    .workflow-row:hover {
      border-color: color-mix(in oklab, var(--accent) 45%, var(--border));
      background: var(--surface-hover, rgba(255,255,255,0.04));
    }
    .workflow-icon {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 32px;
      height: 32px;
      border-radius: 6px;
      color: var(--accent, #7c9cff);
      background: color-mix(in oklab, var(--accent) 14%, transparent);
    }
    .workflow-main {
      display: flex;
      flex-direction: column;
      gap: 3px;
      min-width: 0;
    }
    .workflow-main strong,
    .workflow-main span {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .workflow-main strong {
      color: var(--text, #E7E9EE);
      font-weight: 600;
    }
    .workflow-main span,
    .workflow-meta {
      color: var(--text-muted, #9aa3b2);
      font-size: 12px;
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
        padding: 18px;
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
      .home h1 {
        font-size: 28px;
      }
      .run-head,
      .empty-state {
        align-items: flex-start;
        flex-direction: column;
      }
      .workflow-row {
        grid-template-columns: 32px minmax(0, 1fr);
      }
      .workflow-meta {
        grid-column: 2;
      }
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
  private readonly workflowsApi = inject(WorkflowsApi);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly isAuthenticated = computed(() => this.auth.currentUser() !== null);
  protected readonly workflows = signal<WorkflowSummary[]>([]);
  protected readonly workflowsLoading = signal(false);
  protected readonly workflowsError = signal<string | null>(null);
  private readonly workflowsLoaded = signal(false);
  protected readonly runnableWorkflows = computed(() =>
    this.workflows()
      .filter(workflow => workflow.category === 'Workflow' && !workflow.isRetired)
      .slice()
      .sort((a, b) => a.name.localeCompare(b.name)),
  );

  constructor() {
    effect(() => {
      if (this.isAuthenticated() && !this.workflowsLoaded() && !this.workflowsLoading()) {
        this.loadWorkflows();
      }
    });
  }

  protected signIn(): void {
    this.auth.login();
  }

  protected loadWorkflows(): void {
    this.workflowsLoading.set(true);
    this.workflowsError.set(null);
    this.workflowsApi.list().pipe(
      takeUntilDestroyed(this.destroyRef),
      catchError(err => {
        this.workflowsError.set(formatHttpError(err, 'Failed to load workflows.'));
        return of([] as WorkflowSummary[]);
      }),
    ).subscribe(workflows => {
      this.workflows.set(workflows);
      this.workflowsLoaded.set(true);
      this.workflowsLoading.set(false);
    });
  }
}
