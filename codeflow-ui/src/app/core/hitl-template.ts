export type HitlPlaceholderKind = 'text' | 'select';

export interface HitlPlaceholder {
  /** Name exactly as written in the template (used as the form field key). */
  name: string;
  kind: HitlPlaceholderKind;
  /** For select placeholders, the allowed options. */
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
  /\{\{\s*([^}]+?)\s*\}\}/g;

interface ParsedPlaceholderExpression {
  helper: 'json' | null;
  name: string;
  options?: string[];
}

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
    const parsed = parsePlaceholderExpression(match[1]);
    if (!parsed) {
      continue;
    }

    const { name, options } = parsed;
    const key = name.toLowerCase();

    const existingIdx = nameIndex.get(key);
    if (existingIdx !== undefined) {
      // Merge options: later references may refine the enum set, but we keep
      // the first set if present to avoid confusing UI churn.
      const existing = placeholders[existingIdx];
      if (options && !existing.options) {
        placeholders[existingIdx] = {
          ...existing,
          kind: classifyKind(options),
          options
        };
      }
      continue;
    }

    const kind = classifyKind(options);
    nameIndex.set(key, placeholders.length);
    placeholders.push({ name, kind, options });
  }

  return { placeholders, nameIndex };
}

/** Substitute placeholder values into the template. Unresolved placeholders are left as-is. */
export function renderHitlTemplate(
  template: string,
  values: Record<string, unknown>
): string {
  const lookup = new Map<string, unknown>();
  for (const [key, val] of Object.entries(values)) {
    lookup.set(key.toLowerCase(), val);
  }

  PLACEHOLDER_PATTERN.lastIndex = 0;
  return template.replace(PLACEHOLDER_PATTERN, (full, expression: string) => {
    const parsed = parsePlaceholderExpression(expression);
    if (!parsed) {
      return full;
    }

    const value = lookup.get(parsed.name.toLowerCase());
    if (value === undefined) {
      return full;
    }

    return renderPlaceholderValue(parsed, value);
  });
}

function classifyKind(options: string[] | undefined): HitlPlaceholderKind {
  if (options && options.length > 0) { return 'select'; }
  return 'text';
}

function parsePlaceholderExpression(raw: string): ParsedPlaceholderExpression | null {
  const trimmed = raw.trim();
  if (!trimmed) {
    return null;
  }

  let helper: 'json' | null = null;
  let inner = trimmed;

  const helperMatch = /^json\((.*)\)$/i.exec(trimmed);
  if (helperMatch) {
    helper = 'json';
    inner = helperMatch[1]?.trim() ?? '';
  }

  const placeholderMatch = /^([A-Za-z0-9_.\-]+)(?:\s*:\s*(.+))?$/.exec(inner);
  if (!placeholderMatch) {
    return null;
  }

  const name = placeholderMatch[1];
  const optionsRaw = placeholderMatch[2];
  const options = optionsRaw
    ? optionsRaw
        .split('|')
        .map(opt => opt.trim())
        .filter(opt => opt.length > 0)
    : undefined;

  return { helper, name, options };
}

function renderPlaceholderValue(
  placeholder: ParsedPlaceholderExpression,
  value: unknown
): string {
  if (placeholder.helper === 'json') {
    return JSON.stringify(value) ?? 'null';
  }

  if (typeof value === 'string') {
    return value;
  }

  if (value === null || value === undefined) {
    return '';
  }

  return typeof value === 'object'
    ? JSON.stringify(value)
    : String(value);
}
