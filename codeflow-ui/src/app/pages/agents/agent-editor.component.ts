import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
import { formatHttpError } from '../../core/format-error';
import { AgentConfig, LlmProviderKey } from '../../core/models';
import { PageContextService } from '../../core/page-context.service';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { AgentFormComponent, AgentFormHeaderState, AgentFormSaveRequest } from './agent-form.component';

const DEFAULT_HEADER_STATE: AgentFormHeaderState = {
  type: 'agent',
  provider: 'openai' as LlmProviderKey,
  model: 'gpt-5.4',
};

@Component({
  selector: 'cf-agent-editor-page',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    PageHeaderComponent,
    ButtonComponent,
    ChipComponent,
    AgentFormComponent,
  ],
  template: `
    <div class="page">
      <cf-page-header
        [title]="existingKey() ? 'Edit ' + existingKey() : 'New agent'"
        subtitle="Saving always creates a new immutable version. Tool access flows through roles assigned on the agent's detail page.">
        <a routerLink="/agents">
          <button type="button" cf-button variant="ghost" icon="back">Cancel</button>
        </a>
        <button type="button" cf-button variant="primary" icon="check"
                (click)="submitForm($event)" [disabled]="saving()">
          {{ saving() ? 'Saving...' : (existingKey() ? 'Save new version' : 'Create agent') }}
        </button>
        <div page-header-body>
          <div class="trace-header-meta">
            @if (existingKey()) { <cf-chip mono>{{ existingKey() }}</cf-chip> }
            @if (headerState(); as state) {
              <cf-chip [variant]="state.type === 'hitl' ? 'accent' : 'default'" mono>{{ state.type === 'hitl' ? 'HITL' : 'LLM agent' }}</cf-chip>
              @if (state.type === 'agent') {
                <cf-chip mono>{{ state.provider }}</cf-chip>
                <cf-chip mono>{{ state.model }}</cf-chip>
              }
            }
          </div>
        </div>
      </cf-page-header>

      @if (error()) {
        <div class="trace-failure">{{ error() }}</div>
      }

      <div class="card" style="padding: 0 20px">
        <cf-agent-form
          [key]="existingKey()"
          [initialConfig]="loadedConfig()"
          [initialType]="loadedType()"
          [initialTags]="loadedTags()"
          (saveRequested)="save($event)"></cf-agent-form>
      </div>
    </div>
  `,
})
export class AgentEditorPageComponent {
  private readonly agentsApi = inject(AgentsApi);
  private readonly router = inject(Router);
  private readonly pageContext = inject(PageContextService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly form = viewChild(AgentFormComponent);

  protected readonly existingKey = input<string | undefined>(undefined, { alias: 'key' });
  protected readonly loadedConfig = signal<AgentConfig | null>(null);
  protected readonly loadedType = signal<'agent' | 'hitl' | null>(null);
  protected readonly loadedTags = signal<string[]>([]);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly headerState = computed<AgentFormHeaderState>(() =>
    this.form()?.headerState() ?? {
      ...DEFAULT_HEADER_STATE,
      type: this.loadedType() ?? DEFAULT_HEADER_STATE.type,
    });

  private loadRequestId = 0;

  constructor() {
    effect(() => {
      const key = this.existingKey();
      if (key) {
        this.pageContext.set({ kind: 'agent-editor', agentId: key });
      } else {
        this.pageContext.clear();
      }
    });

    effect(() => {
      this.loadExistingAgent(this.existingKey());
    });
  }

  protected submitForm(event: Event): void {
    this.form()?.submit(event);
  }

  protected save(payload: AgentFormSaveRequest): void {
    this.saving.set(true);
    this.error.set(null);

    const existingKey = this.existingKey();
    const save$ = existingKey
      ? this.agentsApi.addVersion(existingKey, payload.config, payload.tags)
      : this.agentsApi.create(payload.key, payload.config, payload.tags);

    save$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: result => {
        this.saving.set(false);
        this.router.navigate(['/agents', result.key]);
      },
      error: err => {
        this.saving.set(false);
        this.error.set(formatHttpError(err, 'Save failed'));
      },
    });
  }

  private loadExistingAgent(existingKey: string | undefined): void {
    const requestId = ++this.loadRequestId;
    this.loadedConfig.set(null);
    this.loadedType.set(null);
    this.loadedTags.set([]);
    this.error.set(null);

    if (!existingKey) return;

    this.agentsApi.getLatest(existingKey).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: version => {
        if (requestId !== this.loadRequestId) return;
        this.loadedConfig.set(version.config ?? {});
        this.loadedType.set(version.type === 'hitl' ? 'hitl' : 'agent');
        this.loadedTags.set(version.tags ?? []);
      },
      error: err => {
        if (requestId !== this.loadRequestId) return;
        this.error.set(formatHttpError(err, 'Load failed'));
      },
    });
  }
}
