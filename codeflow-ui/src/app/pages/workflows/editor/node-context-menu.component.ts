import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  EventEmitter,
  HostListener,
  Input,
  Output,
  ViewChild
} from '@angular/core';
import { CommonModule } from '@angular/common';

export interface NodeContextMenuItem {
  id: string;
  label: string;
  danger?: boolean;
  disabled?: boolean;
  disabledReason?: string;
}

@Component({
  selector: 'cf-node-context-menu',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (open) {
      <div class="ctx-menu"
           #menu
           role="menu"
           [style.top.px]="y"
           [style.left.px]="x">
        @for (item of items; track item.id) {
          <button type="button"
                  role="menuitem"
                  class="ctx-item"
                  [class.danger]="item.danger"
                  [disabled]="item.disabled"
                  [attr.title]="item.disabled ? item.disabledReason : null"
                  (click)="pick(item)">
            {{ item.label }}
          </button>
        }
      </div>
    }
  `,
  styles: [`
    .ctx-menu {
      position: fixed;
      z-index: 900;
      min-width: 200px;
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius, 6px);
      box-shadow: 0 12px 28px rgba(0, 0, 0, 0.35);
      padding: 0.25rem 0;
      display: flex;
      flex-direction: column;
    }
    .ctx-item {
      background: transparent;
      border: 0;
      color: inherit;
      padding: 0.45rem 0.9rem;
      text-align: left;
      cursor: pointer;
      font-size: 0.85rem;
      font-family: inherit;
    }
    .ctx-item:hover:not([disabled]) { background: var(--surface-2); }
    .ctx-item[disabled] { opacity: 0.5; cursor: not-allowed; }
    .ctx-item.danger { color: #f85149; }
    .ctx-item.danger:hover:not([disabled]) { background: rgba(248, 81, 73, 0.12); }
  `]
})
export class NodeContextMenuComponent {
  @Input() open = false;
  @Input() x = 0;
  @Input() y = 0;
  @Input() items: readonly NodeContextMenuItem[] = [];

  @Output() pickItem = new EventEmitter<NodeContextMenuItem>();
  @Output() close = new EventEmitter<void>();

  @ViewChild('menu', { static: false }) menu?: ElementRef<HTMLElement>;

  pick(item: NodeContextMenuItem): void {
    if (item.disabled) return;
    this.pickItem.emit(item);
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.open) this.close.emit();
  }

  @HostListener('document:mousedown', ['$event'])
  onOutsideClick(event: MouseEvent): void {
    if (!this.open) return;
    const target = event.target as Node | null;
    if (!target) return;
    if (this.menu?.nativeElement.contains(target)) return;
    this.close.emit();
  }

  @HostListener('window:scroll')
  @HostListener('window:resize')
  onDismissEvent(): void {
    if (this.open) this.close.emit();
  }
}
