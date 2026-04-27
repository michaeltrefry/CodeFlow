/**
 * E3 — Scriban template autocomplete.
 *
 * The Monaco editor for prompt templates / Hitl outputTemplates is set to
 * `language: 'plaintext'` (Scriban has no first-class language support), so we
 * provide our own completion provider that fires only when the cursor is inside
 * `{{ ... }}`. It offers:
 *
 *  - Stock `@codeflow/*` partials via `{{ include "..." }}`.
 *  - Loop bindings (`round`, `maxRounds`, `isLastRound`).
 *  - The `input` variable.
 *  - Top-level `workflow.` / `context.` placeholders (the actual key set lives
 *    in F2's per-node dataflow, which agents don't have access to outside a
 *    workflow context — narrowing to detected keys is a follow-up slice).
 *
 * Outside `{{ ... }}` no completions fire (acceptance #3).
 */

/** Stock partials shipped via P1's SystemPromptPartials seeder. Hand-maintained
 *  alongside `CodeFlow.Persistence/SystemPromptPartials.cs`. Adding a partial there
 *  without updating this list misses it from autocomplete (it still resolves at runtime). */
export const STOCK_PARTIALS = [
  '@codeflow/reviewer-base',
  '@codeflow/producer-base',
  '@codeflow/last-round-reminder',
  '@codeflow/no-metadata-sections',
  '@codeflow/write-before-submit',
] as const;

export interface TemplateSuggestion {
  /** Suggestion label as rendered in the autocomplete popup. */
  readonly label: string;
  /** Tag rendered next to the label. */
  readonly detail: string;
  /** Markdown body shown when highlighted. */
  readonly documentation: string;
  /** Snippet body, with `${1:...}` tabstops where useful. */
  readonly insertText: string;
  /** Optional sort prefix; otherwise alphabetic by label. */
  readonly sortKey?: string;
  /** Optional Monaco filterText override; if absent, the label is used. Set this when the
   *  label contains punctuation (e.g. `include "@codeflow/..."`) and the user is more
   *  likely to type the inner token (e.g. `code` or `reviewer`) than the leading verb. */
  readonly filterText?: string;
}

/** Checks whether the given position is inside a `{{ ... }}` Scriban tag.
 *  Scans backward from the cursor for the closest `{{` and `}}`; returns true
 *  iff the closest delimiter is `{{`. Triple-brace `{{{ }}}` (raw output) is
 *  treated identically. */
export function isInsideScribanTag(text: string, offset: number): boolean {
  if (offset > text.length) return false;
  // Look only at text before the cursor.
  const head = text.slice(0, offset);
  const lastOpen = head.lastIndexOf('{{');
  const lastClose = head.lastIndexOf('}}');
  if (lastOpen === -1) return false;
  return lastOpen > lastClose;
}

/** Builds the suggestion list. Static today; will accept a context object
 *  (workflow-var keys, context keys, in-loop flag) once cross-workflow narrowing
 *  is wired in a follow-up slice. */
export function buildTemplateSuggestions(): TemplateSuggestion[] {
  const partialItems: TemplateSuggestion[] = STOCK_PARTIALS.map(p => {
    // Strip "@codeflow/" so the bare name (e.g. "reviewer-base") sits at a word boundary
    // for Monaco's fuzzy matcher; concatenate every plausible prefix the author might type.
    const bare = p.replace(/^@codeflow\//, '');
    return {
      label: `include "${p}"`,
      detail: 'partial',
      documentation: `Inlines the stock \`${p}\` partial. Pin a specific version on the agent if you need to opt out of upgrades.`,
      insertText: `include "${p}"`,
      sortKey: '0-' + p,
      // Author may type `inc`, `code`, `reviewer`, `reviewer-base`, etc.
      filterText: `include codeflow ${bare} ${p}`,
    };
  });

  const loopBindings: TemplateSuggestion[] = [
    {
      label: 'round',
      detail: 'loop',
      documentation: 'Current round number inside a ReviewLoop child (1-based).',
      insertText: 'round',
      sortKey: '1-round',
    },
    {
      label: 'maxRounds',
      detail: 'loop',
      documentation: 'Maximum rounds configured on the parent ReviewLoop.',
      insertText: 'maxRounds',
      sortKey: '1-maxRounds',
    },
    {
      label: 'isLastRound',
      detail: 'loop',
      documentation: 'True on the final round of a ReviewLoop. Pair with `{{ if isLastRound }}...{{ end }}` in reviewer prompts to soften criteria.',
      insertText: 'isLastRound',
      sortKey: '1-isLastRound',
    },
  ];

  const inputBinding: TemplateSuggestion = {
    label: 'input',
    detail: 'variable',
    documentation: 'The artifact passed into this node by the upstream edge.',
    insertText: 'input',
    sortKey: '2-input',
  };

  const placeholders: TemplateSuggestion[] = [
    {
      label: 'workflow.',
      detail: 'bag',
      documentation: 'Read a per-trace-tree workflow variable. Set upstream via `setWorkflow(...)` in a script or by an Agent node\'s "Mirror output" config.',
      insertText: 'workflow.${1:variableName}',
      sortKey: '3-workflow',
    },
    {
      label: 'context.',
      detail: 'bag',
      documentation: 'Read a context value scoped to this trace step. Set via `setContext(...)`.',
      insertText: 'context.${1:keyName}',
      sortKey: '3-context',
    },
  ];

  return [...partialItems, ...loopBindings, inputBinding, ...placeholders];
}
