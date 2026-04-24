import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';

export interface TabItem {
  value: string;
  label: string;
  count?: number;
}

@Component({
  selector: 'cf-tabs',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (it of items; track it.value) {
      <button type="button" class="tab"
        [attr.data-active]="value === it.value ? 'true' : null"
        (click)="select(it.value)">
        {{ it.label }}
        @if (it.count !== undefined && it.count !== null) {
          <span class="tab-count">{{ it.count }}</span>
        }
      </button>
    }
  `,
  host: { '[class.tabs]': 'true' },
})
export class TabsComponent {
  @Input({ required: true }) items: TabItem[] = [];
  @Input() value: string | null = null;
  @Output() valueChange = new EventEmitter<string>();

  select(next: string): void {
    if (next === this.value) return;
    this.value = next;
    this.valueChange.emit(next);
  }
}
