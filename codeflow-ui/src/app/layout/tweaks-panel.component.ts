import { ChangeDetectionStrategy, Component, EventEmitter, HostListener, Output, inject } from '@angular/core';
import { ThemeService, AccentName, FontName, ThemeMode } from '../core/theme.service';
import { IconComponent } from '../ui/icon.component';

interface SwatchDef<T extends string> { value: T; label: string; color: string; }

const ACCENT_SWATCHES: SwatchDef<AccentName>[] = [
  { value: 'indigo', label: 'Indigo', color: 'oklch(0.66 0.16 265)' },
  { value: 'cyan',   label: 'Cyan',   color: 'oklch(0.72 0.14 210)' },
  { value: 'green',  label: 'Green',  color: 'oklch(0.72 0.15 150)' },
  { value: 'amber',  label: 'Amber',  color: 'oklch(0.76 0.15 75)' },
];

const FONT_OPTIONS: Array<{ value: FontName; label: string }> = [
  { value: 'plex',  label: 'IBM Plex' },
  { value: 'inter', label: 'Inter' },
  { value: 'geist', label: 'Geist' },
];

const THEME_OPTIONS: Array<{ value: ThemeMode; label: string }> = [
  { value: 'dark',  label: 'Dark' },
  { value: 'light', label: 'Light' },
];

@Component({
  selector: 'cf-tweaks-panel',
  standalone: true,
  imports: [IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="twk-overlay" (click)="closeRequest.emit()"></div>
    <aside class="twk-panel" (click)="$event.stopPropagation()">
      <div class="twk-head">
        <h4>Tweaks</h4>
        <button type="button" class="twk-x" (click)="closeRequest.emit()" aria-label="Close tweaks">
          <cf-icon name="close"></cf-icon>
        </button>
      </div>

      <div class="twk-row">
        <span class="twk-sect">Theme</span>
        <div class="seg">
          @for (t of THEMES; track t.value) {
            <button type="button" [attr.data-active]="theme.theme() === t.value ? 'true' : null" (click)="theme.setTheme(t.value)">{{ t.label }}</button>
          }
        </div>
      </div>

      <div class="twk-row">
        <span class="twk-sect">Accent</span>
        <div class="twk-swatches">
          @for (s of ACCENTS; track s.value) {
            <button type="button" class="twk-swatch"
              [style.background]="s.color"
              [attr.data-active]="theme.accent() === s.value ? 'true' : null"
              (click)="theme.setAccent(s.value)"
              [attr.aria-label]="'Accent ' + s.label"></button>
          }
        </div>
      </div>

      <div class="twk-row">
        <span class="twk-sect">Font</span>
        <div class="seg">
          @for (f of FONTS; track f.value) {
            <button type="button" [attr.data-active]="theme.font() === f.value ? 'true' : null" (click)="theme.setFont(f.value)">{{ f.label }}</button>
          }
        </div>
      </div>

      <div class="twk-row">
        <span class="twk-sect">Navigation</span>
        <label class="checkbox">
          <input type="checkbox" [checked]="theme.navCollapsed()" (change)="theme.setNavCollapsed($any($event.target).checked)">
          <span>Collapse sidebar</span>
        </label>
      </div>
    </aside>
  `,
})
export class TweaksPanelComponent {
  readonly theme = inject(ThemeService);
  @Output() closeRequest = new EventEmitter<void>();

  readonly ACCENTS = ACCENT_SWATCHES;
  readonly FONTS = FONT_OPTIONS;
  readonly THEMES = THEME_OPTIONS;

  @HostListener('document:keydown.escape')
  onEscape(): void { this.closeRequest.emit(); }
}
