import { renderMarkdown } from './markdown';

describe('renderMarkdown', () => {
  it('renders common markdown with streaming-friendly line breaks', () => {
    const html = renderMarkdown('Hello **workflow**\nnext line');

    expect(html).toContain('<strong>workflow</strong>');
    expect(html).toContain('<br>');
    expect(html).toContain('next line');
  });

  it('sanitizes untrusted HTML from assistant output', () => {
    const html = renderMarkdown('<img src="x" onerror="alert(1)"><script>alert(2)</script>');

    expect(html).not.toContain('onerror');
    expect(html).not.toContain('<script');
    expect(html).not.toContain('alert(2)');
  });

  it('renders valid workflow packages as summarized details blocks', () => {
    const html = renderMarkdown([
      '```cf-workflow-package',
      JSON.stringify({
        schemaVersion: 'codeflow.workflow-package.v1',
        entryPoint: { key: 'triage-flow', version: 2 },
        workflows: [
          {
            key: 'triage-flow',
            name: 'Triage Flow',
            nodes: [{ id: 'start' }, { id: 'review' }],
            edges: [{ fromNodeId: 'start', toNodeId: 'review' }],
          },
          { key: 'child-flow' },
        ],
        agents: [{ key: 'triage-agent' }, { key: 'review-agent' }],
      }),
      '```',
    ].join('\n'));

    expect(html).toContain('class="cf-workflow-package"');
    expect(html).toContain('<strong>Triage Flow</strong>');
    expect(html).toContain('2 nodes');
    expect(html).toContain('1 edge');
    expect(html).toContain('<code>triage-agent</code>');
    expect(html).toContain('<code>child-flow</code>');
  });

  it('falls back to regular code rendering for incomplete workflow package JSON', () => {
    const html = renderMarkdown('```cf-workflow-package\n{"schemaVersion":\n```');

    expect(html).not.toContain('class="cf-workflow-package"');
    expect(html).toContain('language-json');
    expect(html).toContain('{"schemaVersion":');
  });
});
