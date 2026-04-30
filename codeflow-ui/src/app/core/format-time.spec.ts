import { relativeTime } from './format-time';

describe('relativeTime', () => {
  beforeEach(() => {
    vi.setSystemTime(new Date('2026-04-30T12:00:00Z'));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('formats relative seconds, minutes, hours, and days', () => {
    expect(relativeTime('2026-04-30T11:59:47Z')).toBe('13s ago');
    expect(relativeTime('2026-04-30T11:42:00Z')).toBe('18m ago');
    expect(relativeTime('2026-04-29T00:00:00Z')).toBe('36h ago');
    expect(relativeTime('2026-04-27T12:00:00Z')).toBe('3d ago');
  });

  it('uses a placeholder for missing or invalid dates', () => {
    expect(relativeTime(null)).toBe('—');
    expect(relativeTime(undefined)).toBe('—');
    expect(relativeTime('not-a-date')).toBe('—');
  });
});
