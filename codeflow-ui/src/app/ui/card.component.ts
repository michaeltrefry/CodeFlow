import { ChangeDetectionStrategy, Component, Input, booleanAttribute, contentChild, TemplateRef } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';

@Component({
  selector: 'cf-card',
  standalone: true,
  imports: [NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (title || rightTpl()) {
      <div class="card-header">
        @if (title) { <h3>{{ title }}</h3> }
        <span>
          @if (rightTpl()) {
            <ng-container [ngTemplateOutlet]="rightTpl()!"></ng-container>
          }
        </span>
      </div>
    }
    <div class="card-body" [class.card-body-flush]="flush">
      <ng-content></ng-content>
    </div>
  `,
  host: { '[class.card]': 'true' },
})
export class CardComponent {
  @Input() title?: string;
  @Input({ transform: booleanAttribute }) flush = false;
  rightTpl = contentChild<TemplateRef<unknown>>('cardRight');
}
