import { ChangeDetectionStrategy, Component, Input, booleanAttribute } from '@angular/core';

export type ChipVariant = 'default' | 'ok' | 'warn' | 'err' | 'accent' | 'running';

@Component({
  selector: 'cf-chip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (dot) { <span class="chip-dot"></span> }
    <ng-content></ng-content>
  `,
  host: {
    '[class.chip]': 'true',
    '[class.ok]': 'variant === "ok"',
    '[class.warn]': 'variant === "warn"',
    '[class.err]': 'variant === "err"',
    '[class.accent]': 'variant === "accent"',
    '[class.running]': 'variant === "running"',
    '[class.mono]': 'mono',
    '[class.square]': 'square',
  },
})
export class ChipComponent {
  @Input() variant: ChipVariant = 'default';
  @Input({ transform: booleanAttribute }) dot = false;
  @Input({ transform: booleanAttribute }) mono = false;
  @Input({ transform: booleanAttribute }) square = false;
}
