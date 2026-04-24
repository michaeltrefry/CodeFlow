import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  HostListener,
  Input,
  Output,
  booleanAttribute
} from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'cf-dialog',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (open) {
      <div class="dialog-backdrop" (mousedown)="onBackdrop($event)">
        <div class="dialog-panel"
             role="dialog"
             aria-modal="true"
             [attr.aria-labelledby]="labelledBy || null"
             (mousedown)="$event.stopPropagation()"
             [style.max-width]="maxWidth">
          @if (title) {
            <header class="dialog-header">
              <h2 [id]="labelledBy || 'cf-dialog-title'">{{ title }}</h2>
              <button type="button" class="dialog-close" (click)="close.emit()" aria-label="Close">×</button>
            </header>
          }
          <div class="dialog-body">
            <ng-content></ng-content>
          </div>
          <footer class="dialog-footer">
            <ng-content select="[dialog-footer]"></ng-content>
          </footer>
        </div>
      </div>
    }
  `,
  styles: [`
    .dialog-backdrop {
      position: fixed;
      inset: 0;
      z-index: 1000;
      background: rgba(0, 0, 0, 0.55);
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 2rem;
      animation: dialog-fade 120ms ease;
    }
    .dialog-panel {
      background: var(--surface);
      color: var(--text);
      border: 1px solid var(--border);
      border-radius: var(--radius, 8px);
      box-shadow: 0 20px 50px rgba(0, 0, 0, 0.45);
      display: flex;
      flex-direction: column;
      max-height: calc(100vh - 4rem);
      width: 100%;
      overflow: hidden;
    }
    .dialog-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 0.9rem 1rem;
      border-bottom: 1px solid var(--border);
    }
    .dialog-header h2 {
      margin: 0;
      font-size: 1rem;
      font-weight: 600;
    }
    .dialog-close {
      background: transparent;
      border: 0;
      color: var(--muted);
      font-size: 1.4rem;
      line-height: 1;
      cursor: pointer;
      padding: 0 0.25rem;
    }
    .dialog-close:hover { color: var(--text); }
    .dialog-body {
      padding: 1rem;
      overflow: auto;
      flex: 1;
    }
    .dialog-footer {
      padding: 0.75rem 1rem;
      border-top: 1px solid var(--border);
      display: flex;
      justify-content: flex-end;
      gap: 0.5rem;
    }
    .dialog-footer:empty { display: none; }
    @keyframes dialog-fade {
      from { opacity: 0; }
      to { opacity: 1; }
    }
  `]
})
export class DialogComponent {
  @Input({ transform: booleanAttribute }) open = false;
  @Input() title?: string;
  @Input() labelledBy?: string;
  @Input() maxWidth: string = '540px';
  @Input({ transform: booleanAttribute }) dismissOnBackdrop = true;

  @Output() close = new EventEmitter<void>();

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.open) this.close.emit();
  }

  onBackdrop(event: MouseEvent): void {
    if (!this.dismissOnBackdrop) return;
    if (event.target === event.currentTarget) {
      this.close.emit();
    }
  }
}
