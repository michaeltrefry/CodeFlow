import type { AssistantScope } from './assistant.api';

/**
 * What the current page is showing the user. The assistant sidebar (HAA-7) reads this so the
 * conversation it opens is scoped to whatever entity the user is looking at — switching from
 * trace A to trace B in the sidebar reveals a different thread; returning to A resumes the
 * original.
 *
 * Pages that surface a single entity (trace detail, workflow editor, agent editor) call
 * `PageContextService.set(...)` in their `ngOnInit`. Pages that don't surface a specific entity
 * (lists, settings) leave the service alone and the app shell falls back to
 * `{ kind: 'other', route }` from the router URL.
 *
 * `selectedNodeId` and `selectedScriptSlot` are part of the type today so HAA-8 can inject them
 * into the prompt without another protocol change. HAA-7 itself only uses the entity ids.
 */
export type PageContext =
  | { kind: 'home' }
  | { kind: 'trace'; traceId: string; selectedNodeId?: string }
  | { kind: 'workflow-editor'; workflowId: string; selectedNodeId?: string; selectedScriptSlot?: 'input' | 'output' }
  | { kind: 'agent-editor'; agentId: string }
  | { kind: 'library' }
  | { kind: 'traces-list'; filter?: Record<string, unknown> }
  | { kind: 'other'; route: string };

/**
 * Maps a {@link PageContext} to the {@link AssistantScope} the chat panel should mount with.
 *
 * - Single-entity pages produce an `entity` scope keyed by `(entityType, entityId)` — the
 *   backend's `(userId, scopeKey)` unique constraint means the same conversation resumes
 *   across reloads and across navigations away-and-back.
 * - Lists, settings, ops pages, and any unrecognized route fall back to the homepage scope so
 *   the sidebar still gives the user a working assistant while they browse.
 * - The `home` kind returns `null` — the home page already mounts the chat in its main pane,
 *   so the sidebar suppresses itself there to avoid two parallel views of the same conversation.
 */
export function pageContextToScope(ctx: PageContext): AssistantScope | null {
  switch (ctx.kind) {
    case 'home':
      return null;
    case 'trace':
      return { kind: 'entity', entityType: 'trace', entityId: ctx.traceId };
    case 'workflow-editor':
      return { kind: 'entity', entityType: 'workflow', entityId: ctx.workflowId };
    case 'agent-editor':
      return { kind: 'entity', entityType: 'agent', entityId: ctx.agentId };
    case 'library':
    case 'traces-list':
    case 'other':
      return { kind: 'homepage' };
  }
}

/**
 * Wire shape for the per-turn page-context system-message injection (HAA-8). The server reads
 * this and renders a <c>&lt;current-page-context&gt;</c> block alongside the system prompt so
 * the model can resolve "this trace", "this node", etc. without explicit IDs.
 */
export interface PageContextDto {
  kind: PageContext['kind'];
  route?: string;
  entityType?: string;
  entityId?: string;
  selectedNodeId?: string;
  selectedScriptSlot?: 'input' | 'output';
}

/**
 * Flattens a {@link PageContext} into the wire DTO. The route is supplied by the caller
 * (typically `window.location.pathname`) so callers don't need to introduce a Router dep.
 */
export function pageContextToDto(ctx: PageContext, route: string): PageContextDto {
  switch (ctx.kind) {
    case 'home':
      return { kind: 'home', route };
    case 'trace':
      return {
        kind: 'trace',
        route,
        entityType: 'trace',
        entityId: ctx.traceId,
        selectedNodeId: ctx.selectedNodeId,
      };
    case 'workflow-editor':
      return {
        kind: 'workflow-editor',
        route,
        entityType: 'workflow',
        entityId: ctx.workflowId,
        selectedNodeId: ctx.selectedNodeId,
        selectedScriptSlot: ctx.selectedScriptSlot,
      };
    case 'agent-editor':
      return {
        kind: 'agent-editor',
        route,
        entityType: 'agent',
        entityId: ctx.agentId,
      };
    case 'library':
      return { kind: 'library', route };
    case 'traces-list':
      return { kind: 'traces-list', route };
    case 'other':
      return { kind: 'other', route: ctx.route };
  }
}
