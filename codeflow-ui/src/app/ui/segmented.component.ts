import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';

export interface SegmentedOption {
  value: string;
  label: string;
}

@Component({
  selector: 'cf-segmented',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (o of options; track o.value) {
      <button type="button"
        [attr.data-active]="value === o.value ? 'true' : null"
        (click)="select(o.value)">{{ o.label }}</button>
    }
  `,
  host: { '[class.seg]': 'true' },
})
export class SegmentedComponent {
  @Input({ required: true }) options: SegmentedOption[] = [];
  @Input() value: string | null = null;
  @Output() valueChange = new EventEmitter<string>();

  select(next: string): void {
    if (next === this.value) return;
    this.value = next;
    this.valueChange.emit(next);
  }
}
