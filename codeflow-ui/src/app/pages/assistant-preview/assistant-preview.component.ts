import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ChatPanelComponent } from '../../ui/chat';

/**
 * HAA-2 acceptance harness. Mounts two <c>ChatPanelComponent</c> instances side-by-side with
 * different scopes — one homepage-scoped, one entity-scoped — to demonstrate that the embed
 * works in multiple parents and that conversations remain isolated.
 *
 * Lives behind <c>/assistant-preview</c>. The real homepage and sidebar mounts ship in HAA-6
 * and HAA-7; this page is intentionally minimal and exists for development + manual smoke.
 */
@Component({
  selector: 'cf-assistant-preview',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChatPanelComponent],
  template: `
    <main class="preview">
      <header class="preview-head">
        <h1>Assistant chat primitive — preview</h1>
        <p>
          Two embedded <code>cf-chat-panel</code> instances against the HAA-1 backend.
          Conversations are scoped per <code>(currentUser, scope)</code>; the homepage scope
          and the entity-scoped panel below should remain independent.
        </p>
      </header>
      <section class="preview-grid">
        <div class="preview-cell">
          <h2>Homepage scope</h2>
          <cf-chat-panel [scope]="{ kind: 'homepage' }" />
        </div>
        <div class="preview-cell">
          <h2>Entity scope (trace · preview-entity)</h2>
          <cf-chat-panel
            [scope]="{ kind: 'entity', entityType: 'trace', entityId: 'preview-entity' }"
          />
        </div>
      </section>
    </main>
  `,
  styles: [`
    .preview {
      max-width: 1280px;
      margin: 0 auto;
      padding: 24px;
      display: flex;
      flex-direction: column;
      gap: 18px;
    }
    .preview-head h1 {
      margin: 0 0 4px 0;
      font-size: 22px;
      color: var(--text, #E7E9EE);
    }
    .preview-head p {
      margin: 0;
      color: var(--text-muted, #9aa3b2);
      font-size: var(--fs-md, 13px);
    }
    .preview-grid {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 18px;
      align-items: stretch;
    }
    @media (max-width: 960px) {
      .preview-grid {
        grid-template-columns: 1fr;
      }
    }
    .preview-cell {
      display: flex;
      flex-direction: column;
      gap: 8px;
      min-height: 540px;
    }
    .preview-cell h2 {
      margin: 0;
      font-size: 14px;
      color: var(--text-muted, #9aa3b2);
      text-transform: uppercase;
      letter-spacing: 0.06em;
    }
    .preview-cell cf-chat-panel {
      flex: 1 1 auto;
      display: block;
    }
  `],
})
export class AssistantPreviewComponent {}
