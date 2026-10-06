import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { verifyPurchase, verifyFiles, sha256 } from './fixture-oracle.mjs';

const catalogPath = new URL('../tests/fixtures/playtest/cases.json', import.meta.url);
const catalogBytes = readFileSync(catalogPath);
const catalog = JSON.parse(catalogBytes);
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
    const report = verifyPurchase(catalog.cases.find(c => c.id === bundle.identity.caseId), bundle);
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
  ['other owner release', b => b.facts.otherOwnerInputReleased = true],
  ['attach process killed', b => b.facts.attachedProcessTerminated = true],
  ['invalid counter', b => b.facts.requests = '1'],
  ['unknown evidence tier', b => b.identity.variant = 'E2E'],
];
for (const [name, mutate] of mutations) test(`rejects ${name}`, () => {
  const bundle = sample(); mutate(bundle);
  assert.equal(verifyPurchase(catalog.cases[1], bundle).verified, false);
});
test('missing normal-path evidence is also rejected', () => {
  const bundle = sample(false); delete bundle.facts.responses;
  assert.equal(verifyPurchase(catalog.cases[0], bundle).verified, false);
});
test('pins expected bytes before validation and reports evidence hash', () => {
  const scope = mkdtempSync(join(tmpdir(), 'gua-oracle-'));
  try {
    const evidence = join(scope, 'bundle.json');
    const bytes = JSON.stringify(sample()); writeFileSync(evidence, bytes);
    const report = verifyFiles(catalogPath, sha256(catalogBytes), evidence);
    assert.equal(report.verified, true);
    assert.equal(report.evidenceSha256, sha256(bytes));
    assert.throws(() => verifyFiles(catalogPath, '0'.repeat(64), evidence));
  } finally { rmSync(scope, { recursive: true }); }
});
