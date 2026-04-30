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
import { ButtonComponent } from '../../../ui/button.component';
import { ChipComponent } from '../../../ui/chip.component';
import { DialogComponent } from '../../../ui/dialog.component';

export interface PublishForkTarget {
  nodeId: string;
  forkKey: string;
}

export interface PublishForkResult {
  nodeId: string;
  publishedKey: string;
  publishedVersion: number;
}

interface PublishStatus {
  forkedFromKey: string;
  forkedFromVersion: number;
  originalLatestVersion: number | null;
  isDrift: boolean;
}

@Component({
  selector: 'cf-publish-fork-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, DialogComponent, ButtonComponent, ChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cf-dialog
      [open]="!!target"
      title="Publish fork"
      maxWidth="520px"
      (close)="onClose()">

      @if (target) {
        @if (loading()) {
          <p class="muted">Checking drift against the original agent…</p>
        } @else if (loadError()) {
          <div class="banner error">{{ loadError() }}</div>
          <div dialog-footer>
            <button type="button" cf-button variant="ghost" (click)="onClose()">Close</button>
          </div>
        } @else if (status(); as s) {
          <div class="status-block">
            <div class="row">
              <span class="muted">Forked from</span>
              <cf-chip mono>{{ s.forkedFromKey }} v{{ s.forkedFromVersion }}</cf-chip>
            </div>
            <div class="row">
              <span class="muted">Original latest</span>
              @if (s.originalLatestVersion !== null) {
                <cf-chip mono>{{ s.forkedFromKey }} v{{ s.originalLatestVersion }}</cf-chip>
              } @else {
                <cf-chip variant="err" mono>missing</cf-chip>
              }
            </div>
            @if (s.isDrift) {
              <div class="banner warn">
                <strong>Drift detected.</strong>
                The original <code class="mono">{{ s.forkedFromKey }}</code> has moved from
                v{{ s.forkedFromVersion }} to v{{ s.originalLatestVersion }} since this fork was taken.
                Publishing to the original overwrites that newer work. Consider publishing as a
                new agent instead.
              </div>
            }
          </div>

          <div class="action-block">
            <h4>Publish to original</h4>
            <p class="muted small">
              Creates v{{ (s.originalLatestVersion ?? s.forkedFromVersion) + 1 }} of
              <code class="mono">{{ s.forkedFromKey }}</code> with this fork's config. The node
              re-links to the new version.
            </p>
            @if (s.isDrift) {
              <label class="ack">
                <input type="checkbox" [(ngModel)]="acknowledgeDrift" />
                <span>I understand this overwrites newer edits to <code class="mono">{{ s.forkedFromKey }}</code>.</span>
              </label>
            }
            <button type="button" cf-button variant="primary"
                    [disabled]="saving() || (s.isDrift && !acknowledgeDrift)"
                    (click)="publishToOriginal()">
              {{ saving() === 'original' ? 'Publishing…' : 'Publish to ' + s.forkedFromKey }}
            </button>
          </div>

          <hr />

          <div class="action-block">
            <h4>Publish as new agent</h4>
            <p class="muted small">
              Creates a brand-new library agent with this fork's config. The node re-links to
              the new key at v1.
            </p>
            <label class="field">
              <span class="field-label">New agent key</span>
              <input class="input mono" type="text" [(ngModel)]="newKey"
                     placeholder="reviewer-v2" />
            </label>
            <button type="button" cf-button variant="primary"
                    [disabled]="saving() || !newKey.trim()"
                    (click)="publishAsNew()">
              {{ saving() === 'new-agent' ? 'Publishing…' : 'Publish as new agent' }}
            </button>
          </div>

          @if (saveError()) {
            <div class="banner error">{{ saveError() }}</div>
          }
        }
      }

      <div dialog-footer>
        <button type="button" cf-button variant="ghost" (click)="onClose()">Cancel</button>
      </div>
    </cf-dialog>
  `,
  styles: [`
    .status-block { display: flex; flex-direction: column; gap: 0.5rem; margin-bottom: 1rem; }
    .status-block .row { display: flex; gap: 0.6rem; align-items: center; font-size: 0.85rem; }
    .action-block { display: flex; flex-direction: column; gap: 0.5rem; margin-bottom: 1rem; }
    .action-block h4 { margin: 0; font-size: 0.9rem; font-weight: 600; }
    .action-block .muted.small { font-size: 0.78rem; color: var(--muted); margin: 0; line-height: 1.4; }
    .field { display: flex; flex-direction: column; gap: 0.25rem; }
    .field-label { font-size: 0.75rem; color: var(--muted); }
    .input.mono {
      width: 100%;
      padding: 0.4rem 0.55rem;
      border-radius: 4px;
      border: 1px solid var(--border);
      background: var(--surface-2);
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      color: inherit;
    }
    .ack {
      display: flex;
      align-items: flex-start;
      gap: 0.5rem;
      font-size: 0.82rem;
      color: var(--muted);
    }
    .banner {
      padding: 0.5rem 0.75rem;
      border-radius: var(--radius, 4px);
      font-size: 0.82rem;
      line-height: 1.4;
    }
    .banner.warn {
      background: rgba(245, 166, 35, 0.15);
      color: var(--sem-amber, #f5a623);
      border: 1px solid color-mix(in oklab, var(--sem-amber, #f5a623) 30%, transparent);
    }
    .banner.error {
      background: rgba(248, 81, 73, 0.15);
      color: #f85149;
    }
    hr { border: 0; border-top: 1px solid var(--border); margin: 0.5rem 0; }
    code.mono { padding: 0.05rem 0.25rem; background: var(--surface-2); border-radius: 3px; }
  `]
})
export class PublishForkDialogComponent implements OnChanges {
  private readonly agentsApi = inject(AgentsApi);

  @Input() target: PublishForkTarget | null = null;

  @Output() close = new EventEmitter<void>();
  @Output() published = new EventEmitter<PublishForkResult>();

  readonly loading = signal(false);
  readonly loadError = signal<string | null>(null);
  readonly status = signal<PublishStatus | null>(null);
  readonly saving = signal<'original' | 'new-agent' | null>(null);
  readonly saveError = signal<string | null>(null);

  acknowledgeDrift = false;
  newKey = '';

  ngOnChanges(changes: SimpleChanges): void {
    if (!changes['target']) return;
    this.status.set(null);
    this.loadError.set(null);
    this.saveError.set(null);
    this.acknowledgeDrift = false;
    this.newKey = '';
    const target = this.target;
    if (!target) return;

    this.loading.set(true);
    this.agentsApi.getPublishStatus(target.forkKey).subscribe({
      next: s => {
        this.status.set(s);
        this.loading.set(false);
      },
      error: err => {
        this.loadError.set(formatHttpError(err, 'Publish failed'));
        this.loading.set(false);
      }
    });
  }

  onClose(): void {
    this.close.emit();
  }

  publishToOriginal(): void {
    const target = this.target;
    const status = this.status();
    if (!target || !status) return;

    this.saving.set('original');
    this.saveError.set(null);
    this.agentsApi.publish(target.forkKey, {
      mode: 'original',
      acknowledgeDrift: status.isDrift ? this.acknowledgeDrift : undefined
    }).subscribe({
      next: result => {
        this.saving.set(null);
        this.published.emit({
          nodeId: target.nodeId,
          publishedKey: result.publishedKey,
          publishedVersion: result.publishedVersion
        });
      },
      error: err => {
        this.saving.set(null);
        this.saveError.set(formatHttpError(err, 'Publish failed'));
      }
    });
  }

  publishAsNew(): void {
    const target = this.target;
    const key = this.newKey.trim();
    if (!target || !key) return;

    this.saving.set('new-agent');
    this.saveError.set(null);
    this.agentsApi.publish(target.forkKey, {
      mode: 'new-agent',
      newKey: key
    }).subscribe({
      next: result => {
        this.saving.set(null);
        this.published.emit({
          nodeId: target.nodeId,
          publishedKey: result.publishedKey,
          publishedVersion: result.publishedVersion
        });
      },
      error: err => {
        this.saving.set(null);
        this.saveError.set(formatHttpError(err, 'Publish failed'));
      }
    });
  }

}
