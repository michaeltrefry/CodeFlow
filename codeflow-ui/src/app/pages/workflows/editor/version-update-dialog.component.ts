import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ButtonComponent } from '../../../ui/button.component';
import { DialogComponent } from '../../../ui/dialog.component';

/**
 * Per-node "Update to latest version" affordance shared by Agent/Hitl/Start (where the diff is
 * over the agent's declared outputs) and Subflow/ReviewLoop (where the diff is over the child
 * workflow's terminal ports). The author sees added / removed ports and the list of edges that
 * would break before confirming the version bump.
 */
export interface VersionUpdateTarget {
  nodeId: string;
  /** What's being rebound — used in dialog copy ("agent 'reviewer'" vs "workflow 'review-loop'"). */
  kind: 'agent' | 'workflow';
  refKey: string;
  fromVersion: number;
  toVersion: number;
  /** Currently declared port names on the node (excluding the implicit Failed). */
  currentPorts: string[];
  /** Port names available at the latest version (also excluding the implicit Failed). */
  latestPorts: string[];
  /** Edges currently leaving this node — used to show which ones would break. */
  outgoing: { sourcePort: string; targetLabel: string }[];
}

export interface VersionUpdateResult {
  nodeId: string;
  toVersion: number;
  newPorts: string[];
  /** sourcePort values whose edges should be removed (because the port no longer exists). */
  edgePortsToRemove: string[];
}

@Component({
  selector: 'cf-version-update-dialog',
  standalone: true,
  imports: [CommonModule, DialogComponent, ButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cf-dialog
      [open]="!!target"
      [title]="dialogTitle()"
      maxWidth="540px"
      (close)="onCancel()">

      @if (target; as t) {
        <p class="muted small">
          Rebinding <strong>{{ t.kind === 'agent' ? 'agent' : 'workflow' }} <code class="mono">{{ t.refKey }}</code></strong>
          from v{{ t.fromVersion }} to v{{ t.toVersion }}.
        </p>

        <div class="diff-section">
          <span class="diff-label">Added ports</span>
          @if (added().length === 0) {
            <p class="muted xsmall">(none)</p>
          } @else {
            <ul class="port-list mono">
              @for (p of added(); track p) { <li class="added"><code>{{ p }}</code></li> }
            </ul>
          }
        </div>

        <div class="diff-section">
          <span class="diff-label">Removed ports</span>
          @if (removed().length === 0) {
            <p class="muted xsmall">(none)</p>
          } @else {
            <ul class="port-list mono">
              @for (p of removed(); track p) { <li class="removed"><code>{{ p }}</code></li> }
            </ul>
          }
        </div>

        @if (brokenEdges().length > 0) {
          <div class="diff-section">
            <span class="diff-label warning">Edges that will be removed</span>
            <ul class="port-list mono">
              @for (e of brokenEdges(); track e.sourcePort + e.targetLabel) {
                <li class="removed">
                  <code>{{ e.sourcePort }}</code> → <span class="muted">{{ e.targetLabel }}</span>
                </li>
              }
            </ul>
            <p class="muted xsmall">
              Wires from removed ports are dropped on confirm. The implicit <code>Failed</code> handle is preserved on every node.
            </p>
          </div>
        }

        <div dialog-footer>
          <button type="button" cf-button variant="ghost" (click)="onCancel()">Cancel</button>
          <button type="button" cf-button variant="primary" (click)="onConfirm()">
            Update to v{{ t.toVersion }}
          </button>
        </div>
      }
    </cf-dialog>
  `,
  styles: [`
    .diff-section {
      margin-top: 0.75rem;
    }
    .diff-label {
      font-weight: 600;
      font-size: 0.85rem;
      display: block;
      margin-bottom: 0.25rem;
    }
    .diff-label.warning {
      color: var(--color-danger, #d04848);
    }
    .port-list {
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .port-list li {
      padding: 2px 0;
    }
    .port-list li.added::before {
      content: '+ ';
      color: var(--color-success, #2eaa56);
      font-weight: 700;
    }
    .port-list li.removed::before {
      content: '− ';
      color: var(--color-danger, #d04848);
      font-weight: 700;
    }
  `]
})
export class VersionUpdateDialogComponent {
  @Input() target: VersionUpdateTarget | null = null;
  @Output() readonly confirmed = new EventEmitter<VersionUpdateResult>();
  @Output() readonly cancelled = new EventEmitter<void>();

  readonly dialogTitle = computed(() => {
    const t = this.target;
    if (!t) return 'Update to latest version';
    return `Update ${t.kind} '${t.refKey}' to v${t.toVersion}`;
  });

  readonly added = computed(() => {
    const t = this.target;
    if (!t) return [];
    const current = new Set(t.currentPorts);
    return t.latestPorts.filter(p => !current.has(p));
  });

  readonly removed = computed(() => {
    const t = this.target;
    if (!t) return [];
    const latest = new Set(t.latestPorts);
    return t.currentPorts.filter(p => !latest.has(p));
  });

  readonly brokenEdges = computed(() => {
    const t = this.target;
    if (!t) return [];
    const removedSet = new Set(this.removed());
    return t.outgoing.filter(e => removedSet.has(e.sourcePort));
  });

  onConfirm(): void {
    const t = this.target;
    if (!t) return;
    this.confirmed.emit({
      nodeId: t.nodeId,
      toVersion: t.toVersion,
      newPorts: t.latestPorts.slice(),
      edgePortsToRemove: this.removed(),
    });
  }

  onCancel(): void {
    this.cancelled.emit();
  }
}
