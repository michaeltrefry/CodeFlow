import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

@Component({
  selector: 'cf-page-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div>
      @if (title) { <h1>{{ title }}</h1> }
      @if (subtitle) { <p>{{ subtitle }}</p> }
      <ng-content select="[page-header-body]"></ng-content>
    </div>
    <div class="page-header-actions">
      <ng-content></ng-content>
    </div>
  `,
  host: { '[class.page-header]': 'true' },
})
export class PageHeaderComponent {
  @Input() title?: string;
  @Input() subtitle?: string;
}
