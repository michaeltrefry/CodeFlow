import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import ts from 'typescript';

const thresholds = {
  statements: 9,
  branches: 5,
  functions: 5,
  lines: 9,
};

const summaryPath = findCoverageSummary('coverage');
if (!summaryPath) {
  console.error('Coverage summary not found. Run `npm run test:coverage` first.');
  process.exit(1);
}

const summary = JSON.parse(readFileSync(summaryPath, 'utf8'));
const total = computeFullSourceCoverage(summary);
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
  `(${total.files.covered}/${total.files.total} production TS files reported)`,
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

function computeFullSourceCoverage(summary) {
  if (!summary.total) {
    return null;
  }

  const totals = cloneCoverageTotals(summary.total);
  const coveredSourceFiles = new Set(Object.keys(summary)
    .filter(key => key !== 'total')
    .map(toSourceRelativePath)
    .filter(Boolean));
  const productionFiles = listProductionTsFiles('src');

  let missingFiles = 0;
  for (const file of productionFiles) {
    if (coveredSourceFiles.has(file)) {
      continue;
    }

    missingFiles += 1;
    const uncovered = estimateUncoveredMetrics(file);
    for (const metric of ['lines', 'statements', 'functions', 'branches']) {
      totals[metric].total += uncovered[metric].total;
    }
  }

  for (const metric of ['lines', 'statements', 'functions', 'branches']) {
    totals[metric].pct = pct(totals[metric].covered, totals[metric].total);
  }

  totals.files = {
    total: productionFiles.length,
    covered: productionFiles.length - missingFiles,
    missing: missingFiles,
  };
  return totals;
}

function cloneCoverageTotals(total) {
  return {
    lines: cloneMetric(total.lines),
    statements: cloneMetric(total.statements),
    functions: cloneMetric(total.functions),
    branches: cloneMetric(total.branches),
  };
}

function cloneMetric(metric) {
  return {
    total: metric?.total ?? 0,
    covered: metric?.covered ?? 0,
    skipped: metric?.skipped ?? 0,
    pct: metric?.pct ?? 100,
  };
}

function listProductionTsFiles(root) {
  if (!existsSync(root)) {
    return [];
  }

  return walk(root)
    .filter(file => file.endsWith('.ts'))
    .filter(file => !file.endsWith('.spec.ts'))
    .filter(file => !file.endsWith('.d.ts'))
    .filter(file => !file.endsWith(`${sep}test-providers.ts`))
    .map(file => normalizePath(file))
    .sort();
}

function walk(root) {
  const files = [];
  for (const entry of readdirSync(root)) {
    const candidate = join(root, entry);
    const stats = statSync(candidate);
    if (stats.isDirectory()) {
      files.push(...walk(candidate));
    } else if (stats.isFile()) {
      files.push(candidate);
    }
  }
  return files;
}

function toSourceRelativePath(summaryKey) {
  const normalized = normalizePath(summaryKey);
  const marker = '/src/';
  const sourceIndex = normalized.lastIndexOf(marker);
  if (sourceIndex === -1) {
    return null;
  }
  return `src/${normalized.slice(sourceIndex + marker.length)}`;
}

function estimateUncoveredMetrics(file) {
  const text = readFileSync(file, 'utf8');
  const source = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true);
  const executableLines = new Set();
  let functions = 0;
  let branches = 0;

  visit(source);

  const lineCount = Math.max(executableLines.size, countNonCommentLines(text));
  return {
    lines: { total: lineCount },
    statements: { total: lineCount },
    functions: { total: functions },
    branches: { total: branches },
  };

  function visit(node) {
    if (isExecutableNode(node)) {
      executableLines.add(source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1);
    }
    if (isFunctionLike(node)) {
      functions += 1;
    }
    if (isBranchLike(node)) {
      branches += 1;
    }
    ts.forEachChild(node, visit);
  }
}

function isExecutableNode(node) {
  return ts.isStatement(node) ||
    ts.isPropertyDeclaration(node) ||
    ts.isVariableDeclaration(node) ||
    ts.isImportDeclaration(node) ||
    ts.isExportDeclaration(node);
}

function isFunctionLike(node) {
  return ts.isFunctionDeclaration(node) ||
    ts.isMethodDeclaration(node) ||
    ts.isConstructorDeclaration(node) ||
    ts.isGetAccessorDeclaration(node) ||
    ts.isSetAccessorDeclaration(node) ||
    ts.isFunctionExpression(node) ||
    ts.isArrowFunction(node);
}

function isBranchLike(node) {
  return ts.isIfStatement(node) ||
    ts.isConditionalExpression(node) ||
    ts.isCaseClause(node) ||
    ts.isCatchClause(node) ||
    ts.isForStatement(node) ||
    ts.isForInStatement(node) ||
    ts.isForOfStatement(node) ||
    ts.isWhileStatement(node) ||
    ts.isDoStatement(node) ||
    ts.isBinaryExpression(node) && ['&&', '||', '??'].includes(node.operatorToken.getText());
}

function countNonCommentLines(text) {
  const withoutBlocks = text.replace(/\/\*[\s\S]*?\*\//g, '');
  return withoutBlocks
    .split(/\r?\n/)
    .map(line => line.replace(/\/\/.*$/, '').trim())
    .filter(Boolean)
    .length;
}

function pct(covered, total) {
  if (total === 0) {
    return 100;
  }
  return Math.floor((covered / total) * 10_000) / 100;
}

function normalizePath(path) {
  return relative(process.cwd(), path).split(sep).join('/');
}
