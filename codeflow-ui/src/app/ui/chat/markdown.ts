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
  return [
    '<div class="cf-workflow-package">',
    '<div class="cf-workflow-package-summary">',
    `<strong>${escapeHtml(summary.workflowName)}</strong> — `,
    `${summary.nodeCount} node${summary.nodeCount === 1 ? '' : 's'}, `,
    `${summary.edgeCount} edge${summary.edgeCount === 1 ? '' : 's'}, `,
    `agents: ${agentChips}${subflowChips}`,
    '</div>',
    '<details class="cf-workflow-package-detail">',
    '<summary>Show package JSON</summary>',
    `<pre><code class="language-json">${pretty}</code></pre>`,
    '</details>',
    '</div>',
  ].join('');
};

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
  return DOMPurify.sanitize(html, { USE_PROFILES: { html: true } });
}
