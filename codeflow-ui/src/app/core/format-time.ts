export function relativeTime(iso: string | null | undefined): string {
  if (!iso) {
    return '—';
  }

  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) {
    return '—';
  }

  const deltaSec = Math.max(0, Math.round((Date.now() - then) / 1000));
  if (deltaSec < 60) {
    return `${deltaSec}s ago`;
  }

  const deltaMin = Math.round(deltaSec / 60);
  if (deltaMin < 60) {
    return `${deltaMin}m ago`;
  }

  const deltaHr = Math.round(deltaMin / 60);
  if (deltaHr < 48) {
    return `${deltaHr}h ago`;
  }

  return `${Math.round(deltaHr / 24)}d ago`;
}
