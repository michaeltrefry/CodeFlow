import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  OnChanges,
  Output,
  SimpleChanges,
  inject,
  signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AgentsApi } from '../../../core/agents.api';
import { formatHttpError } from '../../../core/format-error';
import { AgentConfig } from '../../../core/models';
import { AgentFormComponent } from '../../agents/agent-form.component';
import { ButtonComponent } from '../../../ui/button.component';
import { ChipComponent } from '../../../ui/chip.component';
import { DialogComponent } from '../../../ui/dialog.component';

export interface InPlaceEditTarget {
  nodeId: string;
  agentKey: string;
  agentVersion: number;
  workflowKey: string;
  initialConfig: AgentConfig;
  initialType: 'agent' | 'hitl';
  isExistingFork: boolean;
}

export interface InPlaceEditResult {
  nodeId: string;
  agentKey: string;
  agentVersion: number;
  config: AgentConfig;
}

type ModalPhase = 'warn' | 'edit' | 'saving';

@Component({
  selector: 'cf-agent-in-place-edit-dialog',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    DialogComponent,
    AgentFormComponent,
    ButtonComponent,
    ChipComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cf-dialog
      [open]="!!target"
      [title]="dialogTitle()"
      maxWidth="880px"
      [dismissOnBackdrop]="phase() === 'warn'"
      (close)="onClose()">

      @if (target) {
        @if (phase() === 'warn' && !suppressWarning) {
          <div class="warn-body">
            <p>
              Editing <code class="mono">{{ target.agentKey }}</code> here creates a
              <strong>workflow-scoped fork</strong> when you save. The original
              <code class="mono">{{ target.agentKey }}</code> agent stays untouched, and the
              fork is invisible to other workflows. You can later publish the fork back to
              the original agent (or as a new agent).
            </p>
            <label class="warn-checkbox">
              <input type="checkbox" [(ngModel)]="dontWarnAgain" />
              <span>Don't warn me again for this workflow</span>
            </label>
          </div>
          <div dialog-footer>
            <button type="button" cf-button variant="ghost" (click)="onClose()">Cancel</button>
            <button type="button" cf-button variant="primary" (click)="acceptWarning()">
              Fork &amp; edit
            </button>
          </div>
        } @else {
          <div class="scope-chip-row">
            <cf-chip variant="accent" mono>scoped to this workflow</cf-chip>
            @if (target.isExistingFork) {
              <cf-chip mono>forked from {{ target.agentKey }} v{{ target.agentVersion }}</cf-chip>
            } @else {
              <cf-chip mono>will fork from {{ target.agentKey }} v{{ target.agentVersion }}</cf-chip>
            }
          </div>
          <cf-agent-form
            #editor
            [key]="target.agentKey"
            [initialConfig]="target.initialConfig"
            [initialType]="target.initialType"
            (saveRequested)="onSaveRequested($event)"></cf-agent-form>
          @if (error()) {
            <div class="banner error">{{ error() }}</div>
          }
          <div dialog-footer>
            <button type="button" cf-button variant="ghost" (click)="onClose()" [disabled]="phase() === 'saving'">
              Cancel
            </button>
            <button type="button" cf-button variant="primary" (click)="editor.submit($event)" [disabled]="phase() === 'saving'">
              {{ phase() === 'saving' ? 'Saving…' : 'Save changes' }}
            </button>
          </div>
        }
      }
    </cf-dialog>
  `,
  styles: [`
    .warn-body { padding: 0.25rem 0.5rem; }
    .warn-body p { margin-top: 0; line-height: 1.5; }
    .warn-body code { padding: 0.1rem 0.3rem; background: var(--surface-2); border-radius: 3px; }
    .warn-checkbox {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      margin-top: 1rem;
      font-size: 0.9rem;
      color: var(--muted);
      cursor: pointer;
    }
    .scope-chip-row {
      display: flex;
      gap: 0.4rem;
      margin-bottom: 0.75rem;
      flex-wrap: wrap;
    }
    .banner.error {
      margin-top: 0.75rem;
      padding: 0.5rem 0.75rem;
      border-radius: var(--radius, 4px);
      background: rgba(248, 81, 73, 0.15);
      color: #f85149;
      font-size: 0.85rem;
    }
  `]
})
export class AgentInPlaceEditDialogComponent implements OnChanges {
  private readonly agentsApi = inject(AgentsApi);

  @Input() target: InPlaceEditTarget | null = null;
  @Input() suppressWarning = false;

  @Output() close = new EventEmitter<void>();
  @Output() saved = new EventEmitter<InPlaceEditResult>();
  @Output() warningSuppressed = new EventEmitter<void>();

  protected readonly phase = signal<ModalPhase>('warn');
  protected readonly error = signal<string | null>(null);
  dontWarnAgain = false;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['target']) {
      const target = this.target;
      if (!target) {
        this.phase.set('warn');
        this.error.set(null);
        this.dontWarnAgain = false;
        return;
      }
      // Skip warning step entirely if the caller has already dismissed it for this session
      // or if the node already points at a fork (warning is only about the first fork).
      this.phase.set(this.suppressWarning || target.isExistingFork ? 'edit' : 'warn');
      this.error.set(null);
      this.dontWarnAgain = false;
    }
  }

  dialogTitle(): string {
    if (!this.target) return '';
    return this.target.isExistingFork
      ? `Edit ${this.target.agentKey} (workflow-scoped fork)`
      : `Edit ${this.target.agentKey} in place`;
  }

  acceptWarning(): void {
    if (this.dontWarnAgain) this.warningSuppressed.emit();
    this.phase.set('edit');
  }

  onClose(): void {
    this.close.emit();
  }

  onSaveRequested(payload: { key: string; type: 'agent' | 'hitl'; config: AgentConfig }): void {
    const target = this.target;
    if (!target) return;

    this.phase.set('saving');
    this.error.set(null);

    const save$ = target.isExistingFork
      ? this.agentsApi.addVersion(target.agentKey, payload.config)
      : this.agentsApi.fork({
          sourceKey: target.agentKey,
          sourceVersion: target.agentVersion,
          workflowKey: target.workflowKey,
          config: payload.config
        });

    save$.subscribe({
      next: result => {
        this.saved.emit({
          nodeId: target.nodeId,
          agentKey: result.key,
          agentVersion: result.version,
          config: payload.config
        });
      },
      error: err => {
        this.phase.set('edit');
        this.error.set(formatHttpError(err, 'Save failed'));
      }
    });
  }

}
