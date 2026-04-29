import {
  STOCK_PARTIALS,
  buildTemplateSuggestions,
  isInsideScribanTag,
} from './template-completion';

describe('isInsideScribanTag', () => {
  it('returns true only when the cursor is after the closest open Scriban delimiter', () => {
    const template = 'Hello {{ input }} and {{ workflow.customer';

    expect(isInsideScribanTag(template, template.indexOf('Hello'))).toBe(false);
    expect(isInsideScribanTag(template, template.indexOf('input'))).toBe(true);
    expect(isInsideScribanTag(template, template.indexOf(' and'))).toBe(false);
    expect(isInsideScribanTag(template, template.length)).toBe(true);
  });

  it('treats triple-brace raw output as Scriban tag context', () => {
    const template = 'Raw {{{ input';

    expect(isInsideScribanTag(template, template.length)).toBe(true);
  });
});

describe('buildTemplateSuggestions', () => {
  it('offers every stock partial as an include suggestion with searchable filter text', () => {
    const suggestions = buildTemplateSuggestions();

    for (const partial of STOCK_PARTIALS) {
      const suggestion = suggestions.find(item => item.insertText === `include "${partial}"`);

      expect(suggestion).toMatchObject({
        label: `include "${partial}"`,
        detail: 'partial',
      });
      expect(suggestion?.filterText).toContain('codeflow');
      expect(suggestion?.filterText).toContain(partial.replace(/^@codeflow\//, ''));
    }
  });

  it('includes workflow and context snippets without omitting the raw input binding', () => {
    const suggestions = buildTemplateSuggestions();

    expect(suggestions).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ label: 'input', insertText: 'input' }),
        expect.objectContaining({ label: 'workflow.', insertText: 'workflow.${1:variableName}' }),
        expect.objectContaining({ label: 'context.', insertText: 'context.${1:keyName}' }),
      ]),
    );
  });
});
