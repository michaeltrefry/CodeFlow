import { ChangeDetectionStrategy, Component, Input, computed, signal } from '@angular/core';
import { ChipComponent, ChipVariant } from './chip.component';

@Component({
  selector: 'cf-state-chip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChipComponent],
  template: `
    <cf-chip [variant]="variant()" [dot]="true">{{ label() }}</cf-chip>
  `,
})
export class StateChipComponent {
  private readonly stateValue = signal<string | null | undefined>(null);

  @Input()
  set state(value: string | null | undefined) {
    this.stateValue.set(value);
  }

  readonly variant = computed<ChipVariant>(() => {
    const s = this.stateValue();
    if (s === 'Completed') return 'ok';
    if (s === 'Running') return 'running';
    if (s === 'Failed') return 'err';
    if (s === 'Escalated') return 'warn';
    return 'default';
  });

  readonly label = computed<string>(() => this.stateValue() ?? 'Unknown');
}
