import DOMPurify from 'dompurify';
import { marked } from 'marked';

// Streaming-friendly defaults: GFM tables / fenced code, but keep it sync so we can re-render
// on every text-delta without juggling promises in the template path.
marked.setOptions({
  gfm: true,
  breaks: true,
  async: false,
});

/**
 * Render markdown to sanitized HTML. Source can be untrusted LLM output — every render goes
 * through DOMPurify with the default profile (strips script/iframe/style + on-event attrs)
 * before the chat panel binds the result with `[innerHTML]`.
 */
export function renderMarkdown(source: string): string {
  if (!source) {
    return '';
  }
  const html = marked.parse(source) as string;
  return DOMPurify.sanitize(html, { USE_PROFILES: { html: true } });
}
