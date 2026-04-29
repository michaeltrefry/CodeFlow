/**
 * What the current page is showing the user. The assistant sidebar reads this as per-turn
 * context while keeping a single global conversation alive across navigation.
 *
 * Pages that surface a single entity (trace detail, workflow editor, agent editor) call
 * `PageContextService.set(...)` in their `ngOnInit`. Pages that don't surface a specific entity
 * (lists, settings) leave the service alone and the app shell falls back to
 * `{ kind: 'other', route }` from the router URL.
 *
 * `selectedNodeId` and `selectedScriptSlot` are part of the type today so HAA-8 can inject them
 * into the prompt without another protocol change.
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
