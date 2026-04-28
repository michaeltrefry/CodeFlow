import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ChatPanelComponent } from '../../ui/chat';
import { ChatToolCallComponent, ChatToolCallView } from '../../ui/chat/chat-tool-call.component';

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
  imports: [ChatPanelComponent, ChatToolCallComponent],
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

      <section class="mock-section" data-testid="haa10-mock">
        <h2>HAA-10 — save_workflow_package confirmation chip (mock)</h2>
        <p>
          Static <code>cf-chat-tool-call</code> instance with a confirmation chip wired in
          via the new <code>confirmation</code> field on <code>ChatToolCallView</code>. Use the
          buttons to walk through the chip's local lifecycle. The real chat-panel performs the
          API call when the user confirms; this mock just flips state.
        </p>
        <div class="mock-controls">
          <button type="button" (click)="setMockState('idle')">Reset to idle</button>
          <button type="button" (click)="setMockState('applying')">applying…</button>
          <button type="button" (click)="setMockState('success')">success</button>
          <button type="button" (click)="setMockState('error')">error</button>
          <button type="button" (click)="setMockState('cancelled')">cancelled</button>
        </div>
        <cf-chat-tool-call
          [view]="mockView()"
          (confirmConfirmation)="onMockConfirm()"
          (cancelConfirmation)="onMockCancel()"
        />
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
    .mock-section {
      display: flex;
      flex-direction: column;
      gap: 10px;
      padding: 16px;
      border: 1px dashed var(--border, rgba(255,255,255,0.16));
      border-radius: var(--radius-md, 8px);
    }
    .mock-section h2 {
      margin: 0;
      font-size: 14px;
      color: var(--text-muted, #9aa3b2);
      text-transform: uppercase;
      letter-spacing: 0.06em;
    }
    .mock-section p {
      margin: 0;
      color: var(--text-muted, #9aa3b2);
      font-size: var(--fs-md, 13px);
    }
    .mock-controls {
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
    }
    .mock-controls button {
      appearance: none;
      background: var(--surface, #131519);
      border: 1px solid var(--border, rgba(255,255,255,0.12));
      color: var(--text, #E7E9EE);
      padding: 4px 10px;
      border-radius: 4px;
      font-size: 11px;
      cursor: pointer;
    }
  `],
})
export class AssistantPreviewComponent {
  /**
   * HAA-10 visual smoke: a hand-rolled tool-call view with a save_workflow_package confirmation
   * chip so the chip rendering can be verified end-to-end without the API. The real chat-panel
   * builds this view from a `tool-result` SSE event whose JSON has `status: "preview_ok"`.
   */
  protected readonly mockView = signal<ChatToolCallView>({
    id: 'mock-haa10',
    name: 'save_workflow_package',
    status: 'success',
    argsPreview: '{ "package": { "schemaVersion": "codeflow.workflow-package.v1", "entryPoint": { "key": "draft-flow", "version": 1 }, ... } }',
    resultPreview: '{ "status": "preview_ok", "createCount": 1, "reuseCount": 0, "canApply": true }',
    confirmation: {
      kind: 'save_workflow_package',
      prompt: 'Save Draft flow (draft-flow v1) to the library?',
      confirmLabel: 'Save',
      cancelLabel: 'Cancel',
      state: 'idle',
    },
  });

  protected setMockState(state: 'idle' | 'applying' | 'success' | 'error' | 'cancelled'): void {
    this.mockView.update(v => ({
      ...v,
      confirmation: {
        ...v.confirmation!,
        state,
        applied: state === 'success' ? { key: 'draft-flow', version: 1 } : undefined,
        errorMessage: state === 'error' ? '500 Internal Server Error — workflow draft-flow already exists.' : undefined,
      },
    }));
  }

  protected onMockConfirm(): void {
    // Demo flow: simulate the apply round-trip with a brief 'applying' state then success.
    this.setMockState('applying');
    setTimeout(() => this.setMockState('success'), 400);
  }

  protected onMockCancel(): void {
    this.setMockState('cancelled');
  }
}
