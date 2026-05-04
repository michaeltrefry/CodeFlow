/**
 * sc-397: shared sessionStorage contract between the chat panel's "Resolve in imports page"
 * chip and the workflows imports page that hydrates from it. The chip writes the handoff
 * just before navigating to /workflows; the imports page reads + clears the handoff on init
 * so a reload doesn't re-trigger the redirect.
 *
 * Two source variants:
 *   - 'inline' — the LLM passed the package as a tool-call argument; the chat panel has the
 *     bytes cached in `pendingSaves` and rides them through the stash.
 *   - 'draft'  — the LLM invoked save_workflow_package with no arguments; the package lives
 *     in the conversation workspace. Only the conversationId rides through the stash; the
 *     imports page calls `GET /api/workflows/package-draft?conversationId=…` to fetch the
 *     bytes when it hydrates.
 */
export const IMPORT_HANDOFF_STORAGE_KEY = 'cf.import.handoff';

/** sc-397: handoff staleness ceiling. The imports page treats stashes older than this as
 *  expired (the user navigated away from the chip and came back hours later); on a stale
 *  read it discards the handoff and renders the page in its normal "no preview yet" state. */
export const IMPORT_HANDOFF_MAX_AGE_MS = 5 * 60 * 1000;

export interface ImportHandoff {
  /** Schema version for forward compatibility — bump if the shape ever changes. */
  v: 1;
  /** Wall-clock at stash time so the imports page can ignore stale stashes. */
  stashedAtMs: number;
  packageSource: 'inline' | 'draft';
  /** Required when packageSource === 'draft'; null otherwise. */
  conversationId: string | null;
  /** Required when packageSource === 'inline'; null otherwise. The bytes are the parsed
   *  package object (the chip's cached `pendingSaves` value). */
  package: unknown;
}
