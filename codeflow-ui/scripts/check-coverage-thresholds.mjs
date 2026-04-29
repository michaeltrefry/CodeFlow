import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';

const thresholds = {
  statements: 10,
  branches: 5,
  functions: 10,
  lines: 10,
};

const summaryPath = findCoverageSummary('coverage');
if (!summaryPath) {
  console.error('Coverage summary not found. Run `npm run test:coverage` first.');
  process.exit(1);
}

const summary = JSON.parse(readFileSync(summaryPath, 'utf8'));
const total = summary.total;
if (!total) {
  console.error(`Coverage summary at ${summaryPath} does not contain a total block.`);
  process.exit(1);
}

const failures = Object.entries(thresholds)
  .map(([metric, minimum]) => ({
    metric,
    minimum,
    actual: total[metric]?.pct,
  }))
  .filter(result => typeof result.actual !== 'number' || result.actual < result.minimum);

if (failures.length > 0) {
  console.error('Coverage guardrail failed:');
  for (const failure of failures) {
    const actual = typeof failure.actual === 'number' ? `${failure.actual}%` : 'missing';
    console.error(`- ${failure.metric}: ${actual} < ${failure.minimum}%`);
  }
  process.exit(1);
}

console.log([
  'Coverage guardrail passed:',
  ...Object.entries(thresholds).map(([metric, minimum]) =>
    `${metric} ${total[metric].pct}% >= ${minimum}%`
  ),
].join(' '));

function findCoverageSummary(root) {
  if (!existsSync(root)) {
    return null;
  }

  const direct = join(root, 'coverage-summary.json');
  if (existsSync(direct)) {
    return direct;
  }

  for (const entry of readdirSync(root)) {
    const candidate = join(root, entry);
    if (statSync(candidate).isDirectory()) {
      const found = findCoverageSummary(candidate);
      if (found) {
        return found;
      }
    }
  }

  return null;
}
