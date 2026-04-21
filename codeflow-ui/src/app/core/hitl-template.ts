import { AgentDecisionKind } from './models';

export const ALL_DECISION_KINDS: AgentDecisionKind[] = [
  'Completed',
  'Approved',
  'ApprovedWithActions',
  'Rejected',
  'Failed'
];

export type HitlPlaceholderKind = 'text' | 'select' | 'decision';

export interface HitlPlaceholder {
  /** Name exactly as written in the template (used as the form field key). */
  name: string;
  kind: HitlPlaceholderKind;
  /** For select/decision placeholders, the allowed options. */
  options?: string[];
}

export interface HitlTemplateParseResult {
  placeholders: HitlPlaceholder[];
  /**
   * Map from placeholder name (lowercased) to the first index where it appears.
   * Used to dedupe repeated references while keeping first-seen ordering.
   */
  nameIndex: Map<string, number>;
}

const PLACEHOLDER_PATTERN =
  /\{\{\s*([A-Za-z0-9_.\-]+)\s*(?::\s*([^}]+?)\s*)?\}\}/g;

/** Parse a HITL output template, returning a deduped, ordered list of placeholders. */
export function parseHitlTemplate(template: string | null | undefined): HitlTemplateParseResult {
  const placeholders: HitlPlaceholder[] = [];
  const nameIndex = new Map<string, number>();

  if (!template) {
    return { placeholders, nameIndex };
  }

  PLACEHOLDER_PATTERN.lastIndex = 0;
  let match: RegExpExecArray | null;
  while ((match = PLACEHOLDER_PATTERN.exec(template)) !== null) {
    const name = match[1];
    const optionsRaw = match[2];
    const key = name.toLowerCase();

    const options = optionsRaw
      ? optionsRaw
          .split('|')
          .map(opt => opt.trim())
          .filter(opt => opt.length > 0)
      : undefined;

    const existingIdx = nameIndex.get(key);
    if (existingIdx !== undefined) {
      // Merge options: later references may refine the enum set, but we keep
      // the first set if present to avoid confusing UI churn.
      const existing = placeholders[existingIdx];
      if (options && !existing.options) {
        placeholders[existingIdx] = {
          ...existing,
          kind: classifyKind(existing.name, options),
          options
        };
      }
      continue;
    }

    const kind = classifyKind(name, options);
    nameIndex.set(key, placeholders.length);
    placeholders.push({ name, kind, options });
  }

  return { placeholders, nameIndex };
}

/** Substitute placeholder values into the template. Unresolved placeholders are left as-is. */
export function renderHitlTemplate(
  template: string,
  values: Record<string, string>
): string {
  const lookup = new Map<string, string>();
  for (const [key, val] of Object.entries(values)) {
    lookup.set(key.toLowerCase(), val);
  }

  PLACEHOLDER_PATTERN.lastIndex = 0;
  return template.replace(PLACEHOLDER_PATTERN, (_full, name: string) => {
    const value = lookup.get(name.toLowerCase());
    return value ?? _full;
  });
}

/** Get the decision placeholder (if any) from a parsed template. */
export function getDecisionPlaceholder(
  result: HitlTemplateParseResult
): HitlPlaceholder | undefined {
  const idx = result.nameIndex.get('decision');
  return idx === undefined ? undefined : result.placeholders[idx];
}

/** Return the allowed decision kinds for a decision placeholder, falling back to all kinds. */
export function getDecisionOptions(placeholder: HitlPlaceholder | undefined): AgentDecisionKind[] {
  if (!placeholder || !placeholder.options || placeholder.options.length === 0) {
    return ALL_DECISION_KINDS;
  }
  const allowed = new Set(placeholder.options);
  const matching = ALL_DECISION_KINDS.filter(kind => allowed.has(kind));
  return matching.length > 0 ? matching : ALL_DECISION_KINDS;
}

function classifyKind(name: string, options: string[] | undefined): HitlPlaceholderKind {
  if (name.toLowerCase() === 'decision') { return 'decision'; }
  if (options && options.length > 0) { return 'select'; }
  return 'text';
}
