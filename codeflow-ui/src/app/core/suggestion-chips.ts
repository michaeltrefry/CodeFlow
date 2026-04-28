import type { PageContext } from './page-context';

/**
 * One clickable suggestion above the composer. <c>label</c> is the short button text the user
 * sees; <c>prompt</c> is the full message that gets sent to the assistant when clicked.
 *
 * Splitting label and prompt keeps the chips terse while still giving the model a complete,
 * unambiguous question (with implicit "this trace" / "this node" references the page-context
 * system message will resolve via HAA-8 backend injection).
 */
export interface SuggestionChip {
  readonly label: string;
  readonly prompt: string;
}

/**
 * Config-driven chip resolver. Adding a new <c>PageContext</c> kind means adding one branch
 * here — no per-page wiring required. The function returns chips appropriate for the current
 * context, including conditional specializations (e.g., trace + selectedNodeId reveals
 * node-specific chips).
 *
 * Trace-page selectedNodeId chips and workflow-editor selectedScriptSlot chips are defined here
 * but only render once the corresponding pages start pushing those fields into PageContext —
 * tracked separately. The structure is forward-compatible.
 */
export function suggestionChipsFor(context: PageContext): readonly SuggestionChip[] {
  switch (context.kind) {
    case 'home':
      // Home page mounts the chat in its main pane and suppresses the sidebar; no chips.
      return [];
    case 'trace': {
      const base: SuggestionChip[] = [
        { label: 'Why did this fail?',         prompt: 'Why did this trace fail?' },
        { label: 'Show the timeline',          prompt: 'Show me the timeline for this trace.' },
        { label: 'Token-heavy nodes',          prompt: 'Which nodes in this trace used the most tokens?' },
      ];
      if (context.selectedNodeId) {
        return [
          { label: 'Why did this node fail?',  prompt: 'Why did this node fail?' },
          { label: 'Inputs to this node',      prompt: 'What did this node receive as input?' },
          ...base,
        ];
      }
      return base;
    }
    case 'workflow-editor': {
      const base: SuggestionChip[] = [
        { label: 'Help me design a node',      prompt: 'Help me design a node for this workflow.' },
        { label: 'Suggest output ports',       prompt: 'Suggest output ports for the selected node.' },
      ];
      if (context.selectedNodeId && context.selectedScriptSlot) {
        const slot = context.selectedScriptSlot;
        const scribanChip: SuggestionChip = slot === 'input'
          ? { label: 'Help me write this Scriban template', prompt: 'Help me write this Scriban input template.' }
          : { label: 'Help me write this output script',    prompt: 'Help me write this output script.' };
        return [scribanChip, ...base];
      }
      return base;
    }
    case 'agent-editor':
      return [
        { label: 'Refine this prompt',         prompt: "Refine this agent's prompt." },
        { label: 'Suggest tool definitions',   prompt: 'Suggest tool definitions for this agent.' },
      ];
    case 'library':
      return [
        { label: 'Find a starter',             prompt: 'Recommend a starter workflow I can adapt.' },
      ];
    case 'traces-list':
      return [
        { label: 'Show recent failures',       prompt: 'Show me recent failed traces.' },
      ];
    case 'other':
      return [];
  }
}
