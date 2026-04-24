import { ChangeDetectionStrategy, Component, Input } from '@angular/core';
import { IconComponent, IconName } from './icon.component';

@Component({
  selector: 'cf-empty',
  standalone: true,
  imports: [IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="empty-ico">
      @if (icon) { <cf-icon [name]="icon"></cf-icon> }
      <ng-content select="[empty-icon]"></ng-content>
    </div>
    @if (title) { <h4>{{ title }}</h4> }
    @if (desc) { <div>{{ desc }}</div> }
    <ng-content></ng-content>
  `,
  host: { '[class.empty]': 'true' },
})
export class EmptyComponent {
  @Input() icon?: IconName;
  @Input() title?: string;
  @Input() desc?: string;
}
