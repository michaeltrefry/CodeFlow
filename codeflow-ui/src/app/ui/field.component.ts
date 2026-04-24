import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

@Component({
  selector: 'cf-field',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (label) { <span class="field-label">{{ label }}</span> }
    <ng-content></ng-content>
    @if (hint) { <span class="field-hint">{{ hint }}</span> }
    <ng-content select="[field-hint]"></ng-content>
  `,
  host: {
    '[class.field]': 'true',
    '[class.span-2]': 'span2',
  },
})
export class FieldComponent {
  @Input() label?: string;
  @Input() hint?: string;
  @Input() span2 = false;
}
