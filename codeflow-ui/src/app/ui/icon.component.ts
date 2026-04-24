import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

export type IconName =
  | 'traces' | 'agents' | 'workflows' | 'hitl' | 'dlq'
  | 'settings' | 'mcp' | 'roles' | 'skills' | 'git' | 'inventory'
  | 'search' | 'bell' | 'plus' | 'chevL' | 'chevR' | 'chevD' | 'close'
  | 'panelL' | 'trash' | 'play' | 'check' | 'x' | 'alert'
  | 'logic' | 'bot' | 'copy' | 'refresh' | 'back';

@Component({
  selector: 'cf-icon',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (name) {
      @case ('traces') {
        <svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
          <path d="M2 12h2l2-6 2 8 2-5 2 3h2"/>
        </svg>
      }
      @case ('agents') {
        <svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
          <rect x="3" y="3" width="5" height="5" rx="1"/>
          <rect x="8" y="8" width="5" height="5" rx="1"/>
          <rect x="8" y="3" width="5" height="5" rx="1"/>
          <rect x="3" y="8" width="5" height="5" rx="1"/>
        </svg>
      }
      @case ('workflows') {
        <svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
          <circle cx="3.5" cy="8" r="1.5"/><circle cx="8" cy="3.5" r="1.5"/><circle cx="8" cy="12.5" r="1.5"/><circle cx="12.5" cy="8" r="1.5"/>
          <path d="M4.8 7 6.7 4.8M9.3 4.8l1.9 2.2M11.2 9l-1.9 2.2M6.7 11.2 4.8 9"/>
        </svg>
      }
      @case ('hitl') {
        <svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
          <circle cx="8" cy="5.5" r="2.5"/><path d="M3 13.5c.9-2.5 2.8-4 5-4s4.1 1.5 5 4"/>
        </svg>
      }
      @case ('dlq') {
        <svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
          <path d="M2.5 4.5h11M2.5 8h11M2.5 11.5h7"/><path d="M12 11.5 13.5 13l2-2.5"/>
        </svg>
      }
      @case ('settings') {
        <svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
          <circle cx="8" cy="8" r="2"/>
          <path d="M8 1.5v2M8 12.5v2M14.5 8h-2M3.5 8h-2M12.6 3.4l-1.4 1.4M4.8 11.2l-1.4 1.4M12.6 12.6l-1.4-1.4M4.8 4.8 3.4 3.4"/>
        </svg>
      }
      @case ('mcp') {
        <svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
          <rect x="2" y="3" width="12" height="4" rx="1"/><rect x="2" y="9" width="12" height="4" rx="1"/>
          <circle cx="4.5" cy="5" r=".6" fill="currentColor"/><circle cx="4.5" cy="11" r=".6" fill="currentColor"/>
        </svg>
      }
      @case ('roles') {
        <svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
          <path d="M8 1.5 2.5 4v4.5c0 3 2.3 5.4 5.5 6 3.2-.6 5.5-3 5.5-6V4L8 1.5z"/>
        </svg>
      }
      @case ('skills') {
        <svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
          <path d="m8 2 2 4 4.4.5-3.2 3.1.9 4.4L8 11.8 3.9 14l.9-4.4L1.6 6.5 6 6z"/>
        </svg>
      }
      @case ('git') {
        <svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
          <circle cx="3.5" cy="8" r="1.5"/><circle cx="12.5" cy="4" r="1.5"/><circle cx="12.5" cy="12" r="1.5"/>
          <path d="M3.5 6.5V4c0-1 1-1.5 2-1.5h3.5M3.5 9.5v2c0 1 1 1.5 2 1.5h5"/>
        </svg>
      }
      @case ('inventory') {
        <svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
          <rect x="2" y="2" width="5" height="5" rx="1"/><rect x="9" y="2" width="5" height="5" rx="1"/>
          <rect x="2" y="9" width="5" height="5" rx="1"/><rect x="9" y="9" width="5" height="5" rx="1"/>
        </svg>
      }
      @case ('search') {
        <svg [attr.width]="size(14)" [attr.height]="size(14)" viewBox="0 0 14 14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"><circle cx="6" cy="6" r="4"/><path d="m9 9 3 3"/></svg>
      }
      @case ('bell') {
        <svg [attr.width]="size(14)" [attr.height]="size(14)" viewBox="0 0 14 14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
          <path d="M3 10V6.5C3 4.6 4.6 3 6.5 3h1C9.4 3 11 4.6 11 6.5V10"/><path d="M2 10h10M6 12h2"/>
        </svg>
      }
      @case ('plus') {
        <svg [attr.width]="size(14)" [attr.height]="size(14)" viewBox="0 0 14 14" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round"><path d="M7 2.5v9M2.5 7h9"/></svg>
      }
      @case ('chevL') {
        <svg [attr.width]="size(14)" [attr.height]="size(14)" viewBox="0 0 14 14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m8.5 3.5-3.5 3.5 3.5 3.5"/></svg>
      }
      @case ('chevR') {
        <svg [attr.width]="size(14)" [attr.height]="size(14)" viewBox="0 0 14 14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m5.5 3.5 3.5 3.5-3.5 3.5"/></svg>
      }
      @case ('chevD') {
        <svg [attr.width]="size(12)" [attr.height]="size(12)" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m3 4.5 3 3 3-3"/></svg>
      }
      @case ('close') {
        <svg [attr.width]="size(14)" [attr.height]="size(14)" viewBox="0 0 14 14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"><path d="m3 3 8 8M11 3l-8 8"/></svg>
      }
      @case ('panelL') {
        <svg [attr.width]="size(14)" [attr.height]="size(14)" viewBox="0 0 14 14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"><rect x="2" y="2.5" width="10" height="9" rx="1"/><path d="M6 2.5v9"/></svg>
      }
      @case ('trash') {
        <svg [attr.width]="size(14)" [attr.height]="size(14)" viewBox="0 0 14 14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
          <path d="M2.5 3.5h9M4 3.5V2.5h6V3.5M4.5 3.5v8a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-8"/>
        </svg>
      }
      @case ('play') {
        <svg [attr.width]="size(12)" [attr.height]="size(12)" viewBox="0 0 12 12" fill="currentColor"><path d="M3.5 2v8l6-4z"/></svg>
      }
      @case ('check') {
        <svg [attr.width]="size(12)" [attr.height]="size(12)" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m2.5 6.5 2.5 2.5 4.5-5"/></svg>
      }
      @case ('x') {
        <svg [attr.width]="size(10)" [attr.height]="size(10)" viewBox="0 0 10 10" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="m2 2 6 6M8 2l-6 6"/></svg>
      }
      @case ('alert') {
        <svg [attr.width]="size(12)" [attr.height]="size(12)" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M6 2v4M6 8.5v.1"/><circle cx="6" cy="6" r="5"/></svg>
      }
      @case ('logic') {
        <svg [attr.width]="size(14)" [attr.height]="size(14)" viewBox="0 0 14 14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m2 7 5-4 5 4-5 4-5-4z"/></svg>
      }
      @case ('bot') {
        <svg [attr.width]="size(14)" [attr.height]="size(14)" viewBox="0 0 14 14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect x="2.5" y="4.5" width="9" height="7" rx="1.5"/><path d="M7 2.5v2M5 7.5v.5M9 7.5v.5"/></svg>
      }
      @case ('copy') {
        <svg [attr.width]="size(12)" [attr.height]="size(12)" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect x="3.5" y="3.5" width="6.5" height="6.5" rx="1"/><path d="M3.5 8V2.5c0-.5.5-1 1-1H8"/></svg>
      }
      @case ('refresh') {
        <svg [attr.width]="size(12)" [attr.height]="size(12)" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10.5 6A4.5 4.5 0 1 1 9 2.8"/><path d="M10.5 1.5v2.5H8"/></svg>
      }
      @case ('back') {
        <svg [attr.width]="size(14)" [attr.height]="size(14)" viewBox="0 0 14 14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3 2 7l4 4M2 7h10"/></svg>
      }
    }
  `,
  styles: [`
    :host { display: inline-flex; line-height: 0; }
    svg { display: block; }
  `],
})
export class IconComponent {
  @Input({ required: true }) name!: IconName;
  @Input() sizeOverride?: number;

  size(defaultSize = 16): number {
    return this.sizeOverride ?? defaultSize;
  }
}
