import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
import { useAsyncList } from '../../core/async-state';
import { formatHttpError } from '../../core/format-error';
import { relativeTime } from '../../core/format-time';
import { AgentSummary } from '../../core/models';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';
import { IconComponent } from '../../ui/icon.component';
import { SegmentedComponent, SegmentedOption } from '../../ui/segmented.component';
import { ProviderIconComponent } from '../../ui/provider-icon.component';

type AgentFilter = 'all' | 'agent' | 'hitl';

@Component({
  selector: 'cf-agents-list',
  standalone: true,
  imports: [
    RouterLink, FormsModule,
    PageHeaderComponent, ButtonComponent, ChipComponent, IconComponent,
    SegmentedComponent, ProviderIconComponent,
  ],
  template: `
    <div class="page">
      <cf-page-header
        title="Agents"
        subtitle="Named, versioned prompt + model bundles. Each version is immutable; latest is rotated in on new runs unless pinned.">
        <button type="button" cf-button variant="ghost" icon="refresh" (click)="reload()">Refresh</button>
        <a routerLink="/agents/new">
          <button type="button" cf-button variant="primary" icon="plus">New agent</button>
        </a>
      </cf-page-header>

      <div class="list-toolbar">
        <div class="list-toolbar-left">
          <cf-segmented
            [options]="filterOptions()"
            [value]="filter()"
            (valueChange)="filter.set($any($event))">
          </cf-segmented>
          <form class="tag-filter" (submit)="addTagFilter($event)">
            <label class="field">
              <span class="field-label">Tags</span>
              <input
                class="input mono tag-filter-input"
                name="agentTagFilter"
                list="agent-tag-options"
                [ngModel]="tagFilterInput()"
                (ngModelChange)="tagFilterInput.set($event)"
                placeholder="ops, review" />
            </label>
            <button type="submit" cf-button variant="ghost" size="sm" icon="plus">Add tag</button>
            <datalist id="agent-tag-options">
              @for (tag of availableTags(); track tag) {
                <option [value]="tag"></option>
              }
            </datalist>
          </form>
        </div>
        <div class="bulk-actions">
          <span class="muted small">showing {{ visibleAgents().length }}</span>
          @if (selectedCount() > 0) {
            <button type="button" cf-button variant="ghost" size="sm" icon="trash" (click)="retireSelected()" [disabled]="retiring()">
              {{ retiring() ? 'Retiring…' : 'Retire selected' }}
            </button>
          }
        </div>
      </div>

      @if (selectedTags().length > 0 || visibleTagOptions().length > 0) {
        <div class="tag-filter-row" aria-label="Agent tag filters">
          @for (tag of selectedTags(); track tag) {
            <button type="button" class="tag-filter-chip active" (click)="removeTagFilter(tag)">
              {{ tag }} <span aria-hidden="true">×</span>
            </button>
          }
          @for (tag of visibleTagOptions(); track tag) {
            <button type="button" class="tag-filter-chip" (click)="addTag(tag)">
              {{ tag }}
            </button>
          }
          @if (selectedTags().length > 0) {
            <button type="button" cf-button variant="ghost" size="sm" icon="x" (click)="clearTagFilters()">Clear</button>
          }
        </div>
      }

      @if (retireError()) {
        <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ retireError() }}</cf-chip></div></div>
      }

      @if (loading()) {
        <div class="card"><div class="card-body muted">Loading agents…</div></div>
      } @else if (error()) {
        <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ error() }}</cf-chip></div></div>
      } @else if (agents().length === 0) {
        <div class="card"><div class="card-body muted">No agents yet. Create one to get started.</div></div>
      } @else {
        <div class="agent-grid">
          @for (agent of visibleAgents(); track agent.key) {
            <a class="agent-card" [routerLink]="['/agents', agent.key]">
              <div class="agent-card-head">
                <input
                  type="checkbox"
                  class="select-box"
                  [checked]="isSelected(agent.key)"
                  (click)="$event.stopPropagation()"
                  (change)="toggleSelected(agent.key, $event)" />
                <div style="min-width: 0; flex: 1">
                  <div class="agent-key">{{ agent.key }}</div>
                  <div class="agent-name">{{ agent.name ?? '—' }}</div>
                </div>
                <div class="agent-type-ico" [class.hitl]="agent.type === 'hitl'">
                  <cf-icon [name]="agent.type === 'hitl' ? 'hitl' : 'bot'"></cf-icon>
                </div>
              </div>
              <div class="agent-tags">
                <cf-chip variant="accent" mono>v{{ agent.latestVersion }}</cf-chip>
                @for (tag of visibleAgentTags(agent); track tag) {
                  <cf-chip mono>{{ tag }}</cf-chip>
                }
                @if (remainingAgentTagCount(agent) > 0) {
                  <cf-chip mono>+{{ remainingAgentTagCount(agent) }}</cf-chip>
                }
                @if (agent.type === 'hitl') {
                  <cf-chip mono>hitl</cf-chip>
                } @else {
                  @if (agent.provider) {
                    <cf-chip mono>
                      <cf-provider-icon [provider]="agent.provider"></cf-provider-icon>
                      {{ agent.provider }}
                    </cf-chip>
                  }
                  @if (agent.model) {
                    <cf-chip mono>{{ agent.model }}</cf-chip>
                  }
                }
              </div>
              <div class="agent-stamp">
                <span>updated {{ relTime(agent.latestCreatedAtUtc) }}</span>
                @if (agent.latestCreatedBy) {
                  <span>·</span>
                  <span class="mono">&#64;{{ agent.latestCreatedBy }}</span>
                }
              </div>
            </a>
          }
        </div>
      }
    </div>
  `,
  styles: [`
    .tag-filter {
      display: flex;
      align-items: end;
      gap: 8px;
      min-width: min(360px, 100%);
    }
    .tag-filter .field {
      min-width: 180px;
    }
    .tag-filter-input {
      min-height: 34px;
    }
    .tag-filter-row {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
      padding: 2px 0 10px;
    }
    .tag-filter-chip {
      min-height: 28px;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 4px 9px;
      border-radius: var(--radius-sm);
      border: 1px solid var(--border);
      background: var(--surface);
      color: var(--text-2);
      font: inherit;
      font-family: var(--font-mono);
      font-size: var(--fs-xs);
      cursor: pointer;
    }
    .tag-filter-chip:hover,
    .tag-filter-chip:focus-visible {
      border-color: var(--accent);
      color: var(--accent-ink);
      outline: none;
    }
    .tag-filter-chip.active {
      background: var(--accent-weak);
      border-color: color-mix(in oklab, var(--accent) 35%, transparent);
      color: var(--accent-ink);
    }
    @media (max-width: 760px) {
      .list-toolbar,
      .list-toolbar-left,
      .tag-filter {
        align-items: stretch;
        flex-direction: column;
      }
      .tag-filter .field {
        min-width: 0;
      }
    }
  `],
})
export class AgentsListComponent {
  private readonly agentsApi = inject(AgentsApi);
  private readonly agentsList = useAsyncList(
    () => this.agentsApi.list(),
    { errorMessage: 'Failed to load agents' },
  );

  readonly agents = this.agentsList.items;
  readonly loading = this.agentsList.loading;
  readonly error = this.agentsList.error;
  readonly filter = signal<AgentFilter>('all');
  readonly tagFilterInput = signal('');
  readonly selectedTags = signal<string[]>([]);
  readonly selectedKeys = signal<Set<string>>(new Set());
  readonly retiring = signal(false);
  readonly retireError = signal<string | null>(null);
  readonly selectedCount = computed(() => this.selectedKeys().size);

  readonly visibleAgents = computed(() => {
    const f = this.filter();
    const tags = this.selectedTags();
    return this.agents().filter(agent =>
      (f === 'all' || agent.type === f)
      && (tags.length === 0 || tags.every(tag => hasTag(agent, tag))));
  });

  readonly availableTags = computed(() => {
    const seen = new Map<string, string>();
    for (const agent of this.agents()) {
      for (const tag of agent.tags ?? []) {
        const trimmed = tag.trim();
        if (!trimmed) continue;
        const key = trimmed.toLocaleLowerCase();
        if (!seen.has(key)) {
          seen.set(key, trimmed);
        }
      }
    }
    return [...seen.values()].sort((a, b) => a.localeCompare(b));
  });

  readonly visibleTagOptions = computed(() => {
    const selected = new Set(this.selectedTags().map(tag => tag.toLocaleLowerCase()));
    return this.availableTags()
      .filter(tag => !selected.has(tag.toLocaleLowerCase()))
      .slice(0, 12);
  });

  readonly filterOptions = computed<SegmentedOption[]>(() => {
    const all = this.agents();
    return [
      { value: 'all', label: `All (${all.length})` },
      { value: 'agent', label: `LLM (${all.filter(a => a.type === 'agent').length})` },
      { value: 'hitl', label: `HITL (${all.filter(a => a.type === 'hitl').length})` },
    ];
  });

  constructor() { this.reload(); }

  reload(): void {
    this.selectedKeys.set(new Set());
    this.agentsList.reload();
  }

  relTime = relativeTime;

  isSelected(key: string): boolean {
    return this.selectedKeys().has(key);
  }

  toggleSelected(key: string, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    const next = new Set(this.selectedKeys());
    checked ? next.add(key) : next.delete(key);
    this.selectedKeys.set(next);
  }

  addTagFilter(event?: Event): void {
    event?.preventDefault();
    const tags = parseTagInput(this.tagFilterInput());
    if (tags.length === 0) return;
    for (const tag of tags) {
      this.addTag(tag);
    }
    this.tagFilterInput.set('');
  }

  addTag(tag: string): void {
    const trimmed = tag.trim();
    if (!trimmed) return;
    const next = [...this.selectedTags()];
    if (next.some(existing => existing.localeCompare(trimmed, undefined, { sensitivity: 'accent' }) === 0)) {
      return;
    }
    next.push(trimmed);
    this.selectedTags.set(next);
  }

  removeTagFilter(tag: string): void {
    this.selectedTags.set(this.selectedTags().filter(existing => existing !== tag));
  }

  clearTagFilters(): void {
    this.selectedTags.set([]);
    this.tagFilterInput.set('');
  }

  visibleAgentTags(agent: AgentSummary): string[] {
    return (agent.tags ?? []).slice(0, 4);
  }

  remainingAgentTagCount(agent: AgentSummary): number {
    return Math.max(0, (agent.tags?.length ?? 0) - 4);
  }

  retireSelected(): void {
    const keys = [...this.selectedKeys()];
    if (keys.length === 0 || this.retiring()) return;
    this.retiring.set(true);
    this.retireError.set(null);
    this.agentsApi.retireMany(keys).subscribe({
      next: () => {
        this.retiring.set(false);
        this.reload();
      },
      error: err => {
        this.retiring.set(false);
        this.retireError.set(formatHttpError(err, 'Failed to retire selected agents.'));
      }
    });
  }
}

function parseTagInput(value: string): string[] {
  const seen = new Set<string>();
  const tags: string[] = [];
  for (const raw of value.split(',')) {
    const tag = raw.trim();
    if (!tag) continue;
    const key = tag.toLocaleLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    tags.push(tag);
  }
  return tags;
}

function hasTag(agent: AgentSummary, requestedTag: string): boolean {
  return (agent.tags ?? []).some(tag =>
    tag.localeCompare(requestedTag, undefined, { sensitivity: 'accent' }) === 0);
}
