import { Injectable, signal } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class LayoutService {
  readonly subcrumb = signal<string | null>(null);

  setSubcrumb(value: string | null): void { this.subcrumb.set(value); }
  clearSubcrumb(): void { this.subcrumb.set(null); }
}
