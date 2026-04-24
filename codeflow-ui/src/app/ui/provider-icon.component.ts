import { ChangeDetectionStrategy, Component, Input, computed, signal } from '@angular/core';

@Component({
  selector: 'cf-provider-icon',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `{{ letter() }}`,
  host: {
    '[class.provider-ico]': 'true',
    '[class.openai]': 'providerValue() === "openai"',
    '[class.anthropic]': 'providerValue() === "anthropic"',
    '[class.lmstudio]': 'providerValue() === "lmstudio"',
  },
})
export class ProviderIconComponent {
  private readonly providerValue = signal<string | null | undefined>(null);

  @Input()
  set provider(value: string | null | undefined) {
    this.providerValue.set(value?.toLowerCase() ?? null);
  }

  readonly letter = computed(() => {
    const p = this.providerValue();
    if (p === 'openai') return 'O';
    if (p === 'anthropic') return 'A';
    if (p === 'lmstudio') return 'L';
    return '?';
  });
}
