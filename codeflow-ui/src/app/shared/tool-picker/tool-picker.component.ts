import { Component, computed, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AgentRoleGrant,
  AgentRoleToolCategory,
  HostTool,
  McpServer,
  McpServerTool,
} from '../../core/models';

export interface McpServerToolCatalog {
  server: McpServer;
  tools: McpServerTool[];
}

interface DisplayTool {
  identifier: string;
  category: AgentRoleToolCategory;
  label: string;
  description?: string | null;
  isMutating: boolean;
  isGhost: boolean;
}

interface DisplayGroup {
  id: string;
  label: string;
  tools: DisplayTool[];
}

function grantKey(grant: AgentRoleGrant): string {
  return `${grant.category}::${grant.toolIdentifier}`;
}

@Component({
  selector: 'cf-tool-picker',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="tool-picker">
      <div class="row">
        <input
          type="search"
          placeholder="Filter tools…"
          [(ngModel)]="filterText"
          [disabled]="readOnly()"
        />
      </div>

      @if (groups().length === 0) {
        <p class="muted small">No tools available.</p>
      }

      @for (group of groups(); track group.id) {
        <section class="group">
          <header class="group-header">
            <button type="button" class="ghost" (click)="toggleExpanded(group.id)">
              <span class="caret">{{ isExpanded(group.id) ? '▾' : '▸' }}</span>
              <span>{{ group.label }}</span>
              <span class="count">{{ selectedCount(group) }}/{{ group.tools.length }}</span>
            </button>
            @if (!readOnly()) {
              <div class="group-actions">
                <button type="button" class="ghost small" (click)="selectAll(group)">Select all</button>
                <button type="button" class="ghost small" (click)="clearGroup(group)">Clear</button>
              </div>
            }
          </header>

          @if (isExpanded(group.id)) {
            <ul class="tool-list">
              @for (tool of group.tools; track tool.identifier) {
                <li class="tool-item" [class.ghost-tool]="tool.isGhost">
                  <label>
                    <input
                      type="checkbox"
                      [checked]="isSelected(tool)"
                      (change)="toggle(tool, $event)"
                      [disabled]="readOnly()"
                    />
                    <span class="tool-label">
                      <span class="tool-name">{{ tool.label }}</span>
                      @if (tool.isGhost) {
                        <span class="tag warn small">ghost</span>
                      }
                      @if (tool.isMutating) {
                        <span class="tag small">mutating</span>
                      }
                    </span>
                  </label>
                  @if (tool.description) {
                    <div class="tool-desc">{{ tool.description }}</div>
                  }
                </li>
              }
            </ul>
          }
        </section>
      }
    </div>
  `,
  styles: [`
    .tool-picker { display: flex; flex-direction: column; gap: 0.75rem; }
    .row input[type="search"] { width: 100%; }
    .group { border: 1px solid var(--color-border); border-radius: 6px; overflow: hidden; }
    .group-header {
      display: flex; align-items: center; justify-content: space-between;
      padding: 0.5rem 0.75rem; background: var(--color-surface-alt);
    }
    .group-header > button.ghost {
      display: inline-flex; align-items: center; gap: 0.5rem;
      background: transparent; border: none; cursor: pointer;
      color: inherit; font-weight: 600; padding: 0;
    }
    .count { font-size: 0.75rem; color: var(--color-muted); font-weight: 400; }
    .group-actions { display: flex; gap: 0.25rem; }
    .group-actions .small { font-size: 0.75rem; padding: 0.15rem 0.4rem; }
    .tool-list { list-style: none; padding: 0.5rem 0.75rem; margin: 0; display: flex; flex-direction: column; gap: 0.4rem; }
    .tool-item label { display: inline-flex; align-items: center; gap: 0.5rem; }
    .tool-label { display: inline-flex; align-items: center; gap: 0.5rem; }
    .tool-desc { color: var(--color-muted); font-size: 0.8rem; padding-left: 1.5rem; }
    .tag.small { font-size: 0.7rem; padding: 0 0.35rem; }
    .tag.warn { background: rgba(250,204,21,0.15); color: #eab308; }
    .ghost-tool .tool-name { color: var(--color-muted); font-style: italic; }
    .muted.small { font-size: 0.8rem; color: var(--color-muted); }
  `]
})
export class ToolPickerComponent {
  readonly hostTools = input<HostTool[]>([]);
  readonly mcpServers = input<McpServerToolCatalog[]>([]);
  readonly value = input<AgentRoleGrant[]>([]);
  readonly readOnly = input<boolean>(false);
  readonly valueChange = output<AgentRoleGrant[]>();

  readonly filterText = signal('');
  private readonly collapsed = signal<Set<string>>(new Set());

  readonly groups = computed<DisplayGroup[]>(() => {
    const filter = this.filterText().trim().toLowerCase();
    const selectedKeys = new Set(this.value().map(grantKey));
    const groups: DisplayGroup[] = [];

    const hostTools = this.hostTools();
    const hostKnown = new Set(hostTools.map(t => t.name.toLowerCase()));
    const hostSelectedGhosts = this.value()
      .filter(g => g.category === 'Host' && !hostKnown.has(g.toolIdentifier.toLowerCase()));

    const hostEntries: DisplayTool[] = [
      ...hostTools.map<DisplayTool>(t => ({
        identifier: t.name,
        category: 'Host',
        label: t.name,
        description: t.description,
        isMutating: t.isMutating,
        isGhost: false,
      })),
      ...hostSelectedGhosts.map<DisplayTool>(g => ({
        identifier: g.toolIdentifier,
        category: 'Host',
        label: g.toolIdentifier,
        description: null,
        isMutating: false,
        isGhost: true,
      })),
    ].filter(t => filter === '' || t.label.toLowerCase().includes(filter));

    if (hostEntries.length > 0 || hostTools.length > 0) {
      groups.push({ id: 'host', label: 'Host tools', tools: hostEntries });
    }

    for (const catalog of this.mcpServers()) {
      const prefix = `mcp:${catalog.server.key}:`;
      const knownTools = new Set(catalog.tools.map(t => t.toolName.toLowerCase()));
      const groupGhosts = this.value()
        .filter(g => g.category === 'Mcp' && g.toolIdentifier.toLowerCase().startsWith(prefix.toLowerCase()))
        .filter(g => !knownTools.has(g.toolIdentifier.slice(prefix.length).toLowerCase()));

      const entries: DisplayTool[] = [
        ...catalog.tools.map<DisplayTool>(t => ({
          identifier: `${prefix}${t.toolName}`,
          category: 'Mcp',
          label: t.toolName,
          description: t.description,
          isMutating: t.isMutating,
          isGhost: false,
        })),
        ...groupGhosts.map<DisplayTool>(g => ({
          identifier: g.toolIdentifier,
          category: 'Mcp',
          label: g.toolIdentifier.slice(prefix.length),
          description: null,
          isMutating: false,
          isGhost: true,
        })),
      ].filter(t => filter === '' || t.label.toLowerCase().includes(filter)
        || t.identifier.toLowerCase().includes(filter));

      groups.push({
        id: `mcp:${catalog.server.key}`,
        label: `MCP · ${catalog.server.displayName} (${catalog.server.key})`,
        tools: entries,
      });
    }

    // Ghost MCP grants referencing archived/missing servers — show in a synthetic group
    const knownServerKeys = new Set(this.mcpServers().map(s => s.server.key.toLowerCase()));
    const orphanGhosts = this.value()
      .filter(g => g.category === 'Mcp')
      .filter(g => {
        const parts = g.toolIdentifier.split(':', 3);
        const serverKey = parts[1]?.toLowerCase() ?? '';
        return parts.length === 3 && parts[0].toLowerCase() === 'mcp' && !knownServerKeys.has(serverKey);
      });

    if (orphanGhosts.length > 0) {
      const tools: DisplayTool[] = orphanGhosts
        .filter(g => filter === '' || g.toolIdentifier.toLowerCase().includes(filter))
        .map(g => ({
          identifier: g.toolIdentifier,
          category: 'Mcp',
          label: g.toolIdentifier,
          description: null,
          isMutating: false,
          isGhost: true,
        }));
      groups.push({ id: 'mcp:orphans', label: 'MCP · unknown / archived servers', tools });
    }

    return groups.filter(g => g.tools.length > 0 || this.filterText().trim() === '');
  });

  isExpanded(groupId: string): boolean {
    return !this.collapsed().has(groupId);
  }

  toggleExpanded(groupId: string): void {
    const next = new Set(this.collapsed());
    if (next.has(groupId)) {
      next.delete(groupId);
    } else {
      next.add(groupId);
    }
    this.collapsed.set(next);
  }

  isSelected(tool: DisplayTool): boolean {
    return this.value().some(g => g.category === tool.category && g.toolIdentifier === tool.identifier);
  }

  selectedCount(group: DisplayGroup): number {
    return group.tools.filter(t => this.isSelected(t)).length;
  }

  toggle(tool: DisplayTool, event: Event): void {
    if (this.readOnly()) return;
    const checked = (event.target as HTMLInputElement).checked;
    const current = this.value();
    if (checked) {
      if (!current.some(g => g.category === tool.category && g.toolIdentifier === tool.identifier)) {
        this.valueChange.emit([...current, { category: tool.category, toolIdentifier: tool.identifier }]);
      }
    } else {
      this.valueChange.emit(
        current.filter(g => !(g.category === tool.category && g.toolIdentifier === tool.identifier))
      );
    }
  }

  selectAll(group: DisplayGroup): void {
    if (this.readOnly()) return;
    const current = this.value();
    const existingKeys = new Set(current.map(grantKey));
    const additions = group.tools
      .filter(t => !t.isGhost)
      .map<AgentRoleGrant>(t => ({ category: t.category, toolIdentifier: t.identifier }))
      .filter(g => !existingKeys.has(grantKey(g)));
    if (additions.length > 0) {
      this.valueChange.emit([...current, ...additions]);
    }
  }

  clearGroup(group: DisplayGroup): void {
    if (this.readOnly()) return;
    const groupKeys = new Set(group.tools.map(t => `${t.category}::${t.identifier}`));
    this.valueChange.emit(this.value().filter(g => !groupKeys.has(grantKey(g))));
  }
}
