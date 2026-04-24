import { ChangeDetectionStrategy, Component, Input, booleanAttribute } from '@angular/core';
import { IconComponent, IconName } from './icon.component';

export type ButtonVariant = 'default' | 'primary' | 'ghost' | 'danger';
export type ButtonSize = 'sm' | 'md' | 'lg';

@Component({
  selector: 'cf-button, button[cf-button]',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `
    @if (icon) { <cf-icon [name]="icon"></cf-icon> }
    <ng-content></ng-content>
  `,
  host: {
    '[attr.type]': 'type',
    '[class.btn]': 'true',
    '[class.btn-primary]': 'variant === "primary"',
    '[class.btn-ghost]': 'variant === "ghost"',
    '[class.btn-danger]': 'variant === "danger"',
    '[class.btn-sm]': 'size === "sm"',
    '[class.btn-lg]': 'size === "lg"',
    '[class.btn-icon]': 'iconOnly',
    '[attr.data-active]': 'active ? "true" : null',
    '[attr.disabled]': 'disabled ? "" : null',
  },
})
export class ButtonComponent {
  @Input() variant: ButtonVariant = 'default';
  @Input() size: ButtonSize = 'md';
  @Input() icon?: IconName;
  @Input() type: 'button' | 'submit' | 'reset' = 'button';
  @Input({ transform: booleanAttribute }) iconOnly = false;
  @Input({ transform: booleanAttribute }) active = false;
  @Input({ transform: booleanAttribute }) disabled = false;
}
