import DOMPurify from 'dompurify';
import { marked, Renderer } from 'marked';
import { summarizeWorkflowPackage } from '../../core/workflow-package.utils';

// Streaming-friendly defaults: GFM tables / fenced code, but keep it sync so we can re-render
// on every text-delta without juggling promises in the template path.
marked.setOptions({
  gfm: true,
  breaks: true,
  async: false,
});

DOMPurify.addHook('afterSanitizeAttributes', node => {
  if (!(node instanceof HTMLAnchorElement)) return;
  if (node.getAttribute('target') !== '_blank') return;

  const rel = new Set((node.getAttribute('rel') ?? '').split(/\s+/).filter(Boolean));
  rel.add('noopener');
  node.setAttribute('rel', [...rel].join(' '));
});

const WORKFLOW_PACKAGE_LANG = 'cf-workflow-package';

/**
 * HAA-9: when the assistant emits a workflow package as a fenced code block with language
 * `cf-workflow-package`, render it as a `<details>` element with a one-line summary
 * (workflow name, node count, agent count) and a collapsed JSON pretty-print. Falls back to
 * the default code-block render if the JSON is malformed or doesn't look like a package, so a
 * still-streaming partial block doesn't break the UI mid-turn.
 */
const renderer = new Renderer();
const defaultCodeRenderer = renderer.code.bind(renderer);
renderer.code = function (this: Renderer, token) {
  const code = token.text ?? '';
  const lang = (token.lang ?? '').trim().split(/\s+/)[0];
  if (lang !== WORKFLOW_PACKAGE_LANG) {
    return defaultCodeRenderer(token);
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(code);
  } catch {
    // Streaming partial: not yet valid JSON. Render as plain code so the user still sees
    // progress; once the assistant finishes the turn the full block re-renders cleanly.
    return defaultCodeRenderer({ ...token, lang: 'json' });
  }

  const summary = summarizeWorkflowPackage(parsed);
  if (!summary) {
    return defaultCodeRenderer({ ...token, lang: 'json' });
  }

  const pretty = escapeHtml(JSON.stringify(parsed, null, 2));
  const agentChips = summary.agentKeys.length
    ? summary.agentKeys.map(k => `<code>${escapeHtml(k)}</code>`).join(', ')
    : '<em>none</em>';
  const subflowChips = summary.subflowKeys.length
    ? ` · subflows: ${summary.subflowKeys.map(k => `<code>${escapeHtml(k)}</code>`).join(', ')}`
    : '';
  const filename = workflowJsonFilename(summary.workflowName);
  return [
    '<div class="cf-workflow-package">',
    '<div class="cf-workflow-package-summary">',
    `<strong>${escapeHtml(summary.workflowName)}</strong> — `,
    `${summary.nodeCount} node${summary.nodeCount === 1 ? '' : 's'}, `,
    `${summary.edgeCount} edge${summary.edgeCount === 1 ? '' : 's'}, `,
    `agents: ${agentChips}${subflowChips}`,
    '</div>',
    '<div class="cf-workflow-package-actions">',
    '<details class="cf-workflow-package-detail">',
    '<summary>Show package JSON</summary>',
    `<pre><code class="language-json">${pretty}</code></pre>`,
    '</details>',
    `<button type="button" class="cf-workflow-package-download" data-cf-filename="${escapeHtml(filename)}" title="Download workflow JSON">Download JSON</button>`,
    '</div>',
    '</div>',
  ].join('');
};

function workflowJsonFilename(workflowName: string): string {
  const safe = (workflowName || 'workflow')
    .toLowerCase()
    .replace(/[^a-z0-9._-]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 80) || 'workflow';
  return `${safe}.json`;
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

/**
 * Render markdown to sanitized HTML. Source can be untrusted LLM output — every render goes
 * through DOMPurify with the default profile (strips script/iframe/style + on-event attrs)
 * before the chat panel binds the result with `[innerHTML]`.
 */
export function renderMarkdown(source: string): string {
  if (!source) {
    return '';
  }
  const html = marked.parse(source, { renderer }) as string;
  return DOMPurify.sanitize(html, {
    USE_PROFILES: { html: true },
    ADD_ATTR: ['rel', 'target'],
  });
}

/**
 * Extract complete workflow packages the assistant emitted as `cf-workflow-package` fenced
 * blocks. This is intentionally separate from rendering so the chat panel can offer a Save
 * confirmation even when the model forgot to call `save_workflow_package` after drafting JSON.
 */
export function extractWorkflowPackagesFromMarkdown(source: string): unknown[] {
  if (!source) {
    return [];
  }

  const packages: unknown[] = [];
  const seen = new Set<string>();
  const visit = (tokens: Array<{ type?: string; lang?: string; text?: string; tokens?: unknown[] }>): void => {
    for (const token of tokens) {
      if (token.type === 'code') {
        const lang = (token.lang ?? '').trim().split(/\s+/)[0];
        if (lang !== WORKFLOW_PACKAGE_LANG) {
          continue;
        }

        try {
          const parsed = JSON.parse(token.text ?? '');
          if (summarizeWorkflowPackage(parsed)) {
            const key = JSON.stringify(parsed);
            if (!seen.has(key)) {
              seen.add(key);
              packages.push(parsed);
            }
          }
        } catch {
          // Ignore partial or malformed blocks; markdown rendering already shows them as code.
        }
      }

      if (Array.isArray(token.tokens)) {
        visit(token.tokens as Array<{ type?: string; lang?: string; text?: string; tokens?: unknown[] }>);
      }
    }
  };

  visit(marked.lexer(source) as Array<{ type?: string; lang?: string; text?: string; tokens?: unknown[] }>);
  return packages;
}
