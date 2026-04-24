import { Component, inject } from '@angular/core';
import { AuthService } from './auth/auth.service';
import { ThemeService } from './core/theme.service';
import { AppShellComponent } from './layout/app-shell.component';

@Component({
  selector: 'cf-root',
  standalone: true,
  imports: [AppShellComponent],
  template: `<cf-app-shell></cf-app-shell>`,
})
export class AppComponent {
  readonly auth = inject(AuthService);
  // ThemeService is constructed eagerly so data-* attrs are applied before first render.
  private readonly theme = inject(ThemeService);

  constructor() {
    this.auth.load();
  }
}
