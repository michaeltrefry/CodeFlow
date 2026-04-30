import { describe, expect, it } from 'vitest';
import { declaredOutputPorts, derivePortRows, hasPortDrift } from './workflow-port-rows';

describe('workflow port rows', () => {
  it('marks stale node ports and missing declared outputs', () => {
    const rows = derivePortRows(
      { outputPortNames: ['Approved', 'Legacy'] },
      {
        outputs: [
          { kind: 'Approved' },
          { kind: 'Rejected' },
          { kind: 'Failed' },
        ],
      }
    );

    expect(rows).toEqual([
      { name: 'Approved', status: 'ok' },
      { name: 'Legacy', status: 'stale' },
      { name: 'Rejected', status: 'missing' },
    ]);
    expect(hasPortDrift(rows)).toBe(true);
  });

  it('treats absent declarations as current node ports without drift', () => {
    const rows = derivePortRows({ outputPortNames: ['Done'] }, null);

    expect(rows).toEqual([{ name: 'Done', status: 'ok' }]);
    expect(hasPortDrift(rows)).toBe(false);
  });

  it('filters non-author-facing declared outputs', () => {
    expect(declaredOutputPorts({
      outputs: [
        { kind: '' },
        { kind: 'Completed' },
        { kind: 'Failed' },
      ],
    })).toEqual(['Completed']);
  });
});
