import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { verifyPurchase, verifyFiles, sha256 } from './fixture-oracle.mjs';

const catalogPath = new URL('../tests/fixtures/playtest/cases.json', import.meta.url);
const catalogBytes = readFileSync(catalogPath);
const catalog = JSON.parse(catalogBytes);
function expectedRun(caseId = 'shop-response-lost', catalogHash = sha256(catalogBytes)) {
  return { schemaVersion: 1, buildId: 'synthetic-checker-test', variant: 'contractFake',
    runId: 'synthetic-run', caseId, catalogSha256: catalogHash };
}
function verify(definition, bundle) {
  return verifyPurchase(definition, bundle, catalog.fixtureVersion, catalog.variants, expectedRun(definition.id));
}
function pinRun(scope, catalogHash) {
  const path = join(scope, 'run-config.json');
  const bytes = JSON.stringify(expectedRun('shop-response-lost', catalogHash));
  writeFileSync(path, bytes);
  return [path, sha256(bytes)];
}
// Synthetic inputs ONLY for testing the checker. Never recorded as a bridge/engine run.
function sample(faulted = true) {
  return {
    identity: { caseId: faulted ? 'shop-response-lost' : 'shop-normal', fixtureVersion: 'playtest-fixtures-r1',
      buildId: 'synthetic-checker-test', runId: 'synthetic-run', seed: 16, clock: 'fixedTick',
      fault: faulted ? { id: 'drop-purchase-response', boundary: 'AfterPurchaseCommitBeforeResponse', occurrence: 1 } : null,
      positionTolerance: 0, latenessToleranceTicks: 0, variant: 'contractFake' },
    facts: { runId: 'synthetic-run', requests: 1, transactions: 1, responses: faulted ? 0 : 1, requestIds: ['purchase-1'],
      faultReceipts: faulted ? [{ id: 'drop-purchase-response', boundary: 'AfterPurchaseCommitBeforeResponse', occurrence: 1 }] : [],
      otherOwnerInputReleased: false, attachedProcessTerminated: false },
    result: { runId: 'synthetic-run', status: faulted ? 'TimedOut' : 'Passed', completionConfirmed: !faulted,
      planCompletionPolicy: 'afterPlan', reasonCategory: faulted ? 'unconfirmed-completion-timeout' : 'goal-verified' },
    trace: { runId: 'synthetic-run', requestIds: ['purchase-1'], resends: 0, completionConfirmed: !faulted }
  };
}
test('normal and fault paths use fixed external expectations without claiming E2E', () => {
  for (const faulted of [false, true]) {
    const bundle = sample(faulted);
    const report = verify(catalog.cases.find(c => c.id === bundle.identity.caseId), bundle);
    assert.equal(report.verified, true);
    assert.equal(report.evidenceTier, 'contractFake');
    assert.equal(report.productEndToEndAcceptance, false);
  }
});
const mutations = [
  ['unfired fault', b => b.facts.faultReceipts = []],
  ['duplicate purchase', b => b.facts.transactions = 2],
  ['resent request', b => { b.facts.requests = 2; b.trace.resends = 1; }],
  ['lost request instead of response', b => b.facts.requests = 0],
  ['lost commit instead of response', b => b.facts.transactions = 0],
  ['acknowledged missing response', b => b.result.completionConfirmed = true],
  ['wrong failure reason', b => b.result.reasonCategory = 'global-timeout'],
  ['Trace missing', b => delete b.trace],
  ['host facts missing', b => delete b.facts],
  ['cross-run result', b => b.result.runId = 'another-run'],
  ['wrong request in Trace', b => b.trace.requestIds = ['purchase-2']],
  ['relaxed tolerance', b => b.identity.latenessToleranceTicks = 1],
  ['changed seed', b => b.identity.seed = 17],
  ['substituted build', b => b.identity.buildId = 'different-game-build'],
  ['other owner release', b => b.facts.otherOwnerInputReleased = true],
  ['attach process killed', b => b.facts.attachedProcessTerminated = true],
  ['invalid counter', b => b.facts.requests = '1'],
  ['unknown evidence tier', b => b.identity.variant = 'E2E'],
];
for (const [name, mutate] of mutations) test(`rejects ${name}`, () => {
  const bundle = sample(); mutate(bundle);
  assert.equal(verify(catalog.cases[1], bundle).verified, false);
});
test('missing normal-path evidence is also rejected', () => {
  const bundle = sample(false); delete bundle.facts.responses;
  assert.equal(verify(catalog.cases[0], bundle).verified, false);
});
test('JSON property order does not change an identical frozen fault plan', () => {
  const bundle = sample();
  bundle.identity.fault = { occurrence: 1, boundary: 'AfterPurchaseCommitBeforeResponse', id: 'drop-purchase-response' };
  bundle.facts.faultReceipts = [bundle.identity.fault];
  assert.equal(verify(catalog.cases[1], bundle).verified, true);
});
test('pins expected bytes before validation and reports evidence hash', () => {
  const scope = mkdtempSync(join(tmpdir(), 'gua-oracle-'));
  try {
    const evidence = join(scope, 'bundle.json');
    const bytes = JSON.stringify(sample()); writeFileSync(evidence, bytes);
    const pinnedRun = pinRun(scope, sha256(catalogBytes));
    const report = verifyFiles(catalogPath, sha256(catalogBytes), ...pinnedRun, evidence);
    assert.equal(report.verified, true);
    assert.equal(report.evidenceSha256, sha256(bytes));
    assert.throws(() => verifyFiles(catalogPath, '0'.repeat(64), ...pinnedRun, evidence));
    assert.throws(() => verifyFiles(catalogPath, sha256(catalogBytes), pinnedRun[0], '0'.repeat(64), evidence));
    writeFileSync(pinnedRun[0], JSON.stringify({ ...expectedRun(), buildId: 'post-failure-substitution' }));
    assert.throws(() => verifyFiles(catalogPath, sha256(catalogBytes), ...pinnedRun, evidence));
  } finally { rmSync(scope, { recursive: true }); }
});
test('pinned revised catalog requires its actual fixture version', () => {
  const scope = mkdtempSync(join(tmpdir(), 'gua-catalog-revision-'));
  try {
    const revisedPath = join(scope, 'cases.json');
    const revisedBytes = JSON.stringify({ ...catalog, fixtureVersion: 'playtest-fixtures-r2' });
    writeFileSync(revisedPath, revisedBytes);
    const evidencePath = join(scope, 'bundle.json');
    const bundle = sample(); writeFileSync(evidencePath, JSON.stringify(bundle));
    const pinnedRun = pinRun(scope, sha256(revisedBytes));
    const stale = verifyFiles(revisedPath, sha256(revisedBytes), ...pinnedRun, evidencePath);
    assert.equal(stale.verified, false);
    assert.ok(stale.failures.includes('fixture-version'));
    bundle.identity.fixtureVersion = 'playtest-fixtures-r2';
    writeFileSync(evidencePath, JSON.stringify(bundle));
    assert.equal(verifyFiles(revisedPath, sha256(revisedBytes), ...pinnedRun, evidencePath).verified, true);
  } finally { rmSync(scope, { recursive: true }); }
});

test('catalog variants control permitted evidence tiers', () => {
  const bundle = sample();
  const obsolete = verifyPurchase(catalog.cases[1], bundle, catalog.fixtureVersion, ['godot'], expectedRun());
  assert.equal(obsolete.verified, false);
  assert.ok(obsolete.failures.includes('evidence-tier'));
  bundle.identity.variant = 'new-approved-fixture-variant';
  const revisedRun = { ...expectedRun(), variant: 'new-approved-fixture-variant' };
  assert.equal(verifyPurchase(catalog.cases[1], bundle, catalog.fixtureVersion,
    ['new-approved-fixture-variant'], revisedRun).verified, true);
});
test('unsupported cases and every CLI invalid-input report forbid E2E acceptance', () => {
  const unsupported = verifyPurchase(catalog.cases[2], sample(), catalog.fixtureVersion, catalog.variants, expectedRun());
  assert.equal(unsupported.verified, false);
  assert.equal(unsupported.productEndToEndAcceptance, false);
  const cli = fileURLToPath(new URL('./fixture-oracle.mjs', import.meta.url));
  const result = spawnSync(process.execPath, [cli], { encoding: 'utf8' });
  assert.equal(result.status, 2);
  assert.equal(JSON.parse(result.stdout).productEndToEndAcceptance, false);
});
test('pre-run Run identity cannot be replaced consistently across all evidence sections', () => {
  const bundle = sample();
  for (const section of [bundle.identity, bundle.facts, bundle.result, bundle.trace]) section.runId = 'substituted-run';
  const report = verify(catalog.cases[1], bundle);
  assert.equal(report.verified, false);
  assert.ok(report.failures.includes('pre-run-identity'));
});
