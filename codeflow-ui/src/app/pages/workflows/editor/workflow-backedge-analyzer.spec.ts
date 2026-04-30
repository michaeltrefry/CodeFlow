import { describe, expect, it } from 'vitest';
import { WorkflowBackedgeAnalyzer, WorkflowBackedgeConnection } from './workflow-backedge-analyzer';

describe('WorkflowBackedgeAnalyzer', () => {
  it('flags the closing edge of a cycle and reports the active cycle path', () => {
    const analysis = WorkflowBackedgeAnalyzer.recompute(
      ['a', 'b', 'c'],
      [
        connection('ab', 'a', 'b'),
        connection('bc', 'b', 'c'),
        connection('ca', 'c', 'a'),
      ],
      id => id.toUpperCase()
    );

    expect([...analysis.backedgeIds]).toEqual(['ca']);
    expect(analysis.cycleByConnectionId.get('ca')).toEqual(['A', 'B', 'C']);
  });

  it('does not flag acyclic forward edges', () => {
    const analysis = WorkflowBackedgeAnalyzer.recompute(
      ['a', 'b', 'c'],
      [
        connection('ab', 'a', 'b'),
        connection('ac', 'a', 'c'),
      ],
      id => id
    );

    expect(analysis.backedgeIds.size).toBe(0);
    expect(analysis.cycleByConnectionId.size).toBe(0);
  });

  it('styles live, intentional, and selected connections distinctly', () => {
    const analysis = {
      backedgeIds: new Set(['cycle']),
      cycleByConnectionId: new Map([['cycle', ['A', 'B']]])
    };

    const live = renderConnectionPath();
    WorkflowBackedgeAnalyzer.applyConnectionStyles(
      { element: live.element, connection: connection('cycle', 'b', 'a') },
      analysis
    );
    expect(live.path.style.stroke).toBe('#f5b84c');
    expect(live.path.style.strokeDasharray).toBe('10 6');
    expect(live.element.title).toContain('A -> B -> A');

    const intentional = renderConnectionPath();
    WorkflowBackedgeAnalyzer.applyConnectionStyles(
      { element: intentional.element, connection: { ...connection('cycle', 'b', 'a'), intentionalBackedge: true } },
      analysis
    );
    expect(intentional.path.style.stroke).toBe('#4682b4');
    expect(intentional.path.style.strokeDasharray).toBe('');

    const selected = renderConnectionPath();
    WorkflowBackedgeAnalyzer.applyConnectionStyles(
      { element: selected.element, connection: { ...connection('other', 'a', 'b'), isSelected: true } },
      analysis
    );
    expect(selected.path.style.stroke).toBe('#ffd166');
    expect(selected.path.style.strokeWidth).toBe('7px');
  });
});

function connection(id: string, source: string, target: string): WorkflowBackedgeConnection {
  return { id, source, target };
}

function renderConnectionPath(): { element: HTMLElement; path: SVGPathElement } {
  const element = document.createElement('div');
  element.innerHTML = '<svg><path /></svg>';
  const path = element.querySelector('path') as SVGPathElement;
  return { element, path };
}
