// External test tool: no import of product assemblies, Goal evaluator, or fixture verdict code.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';

export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const equal = (a, b) => JSON.stringify(a) === JSON.stringify(b);

// Inputs are independent host counters, saved Runner result, and Gua Trace extraction.
// This verifies their agreement with a pre-run pinned case, not their authenticity.
export function verifyPurchase(caseDefinition, bundle) {
  const failures = [];
  const check = (condition, code) => { if (!condition) failures.push(code); };
  const expected = caseDefinition.expected;
  check(caseDefinition.scene === 'shop' && expected && typeof expected === 'object', 'unsupported-case');
  if (failures.length) return { verified: false, failures };
  const { identity = {}, facts = {}, result = {}, trace = {} } = bundle;
  check(identity.caseId === caseDefinition.id, 'case-identity');
  check(identity.fixtureVersion === 'playtest-fixtures-r1', 'fixture-version');
  check(typeof identity.buildId === 'string' && identity.buildId.length > 0, 'build-identity');
  check(identity.seed === caseDefinition.seed && identity.clock === caseDefinition.clock, 'seed-clock');
  check(equal(identity.fault, caseDefinition.fault), 'fault-plan-changed');
  check(identity.positionTolerance === 0 && identity.latenessToleranceTicks === 0, 'tolerance-changed');
  check(['contractFake','realBridge','godot','unity'].includes(identity.variant), 'evidence-tier');
  check(typeof identity.runId === 'string' && identity.runId.length > 0 &&
    [facts,result,trace].every(x => x.runId === identity.runId), 'run-correlation');
  for (const counter of ['requests','transactions','responses']) {
    check(Number.isSafeInteger(facts[counter]) && facts[counter] === expected[counter], `host-${counter}`);
  }
  check(Array.isArray(facts.requestIds) && facts.requestIds.length === expected.requests &&
    facts.requestIds.every(x => typeof x === 'string' && x.length > 0) &&
    new Set(facts.requestIds).size === expected.requests, 'host-request-ids');
  check(Array.isArray(trace.requestIds) && equal(trace.requestIds, facts.requestIds), 'trace-request-correlation');
  check(trace.resends === expected.resends, 'trace-resends');
  check(trace.completionConfirmed === expected.completionConfirmed, 'trace-confirmation');
  check(result.status === expected.status, 'runner-status');
  check(result.completionConfirmed === expected.completionConfirmed, 'runner-confirmation');
  check(result.planCompletionPolicy === 'afterPlan', 'completion-policy');
  check(result.reasonCategory === (caseDefinition.fault ? 'unconfirmed-completion-timeout' : 'goal-verified'), 'reason-category');
  const receipts = facts.faultReceipts;
  check(Array.isArray(receipts) && equal(receipts, caseDefinition.fault ? [caseDefinition.fault] : []), 'fault-not-fired-or-unexpected');
  check(facts.otherOwnerInputReleased === false, 'other-owner-release');
  check(facts.attachedProcessTerminated === false, 'attached-process-terminated');
  return { verified: failures.length === 0, failures, evidenceTier: identity.variant,
    productEndToEndAcceptance: false };
}

export function verifyFiles(catalogPath, expectedCatalogHash, bundlePath) {
  const catalogBytes = readFileSync(catalogPath);
  if (!/^[a-f0-9]{64}$/.test(expectedCatalogHash) || sha256(catalogBytes) !== expectedCatalogHash)
    throw new Error('expected-catalog-hash-mismatch');
  const catalog = JSON.parse(catalogBytes);
  const bundleBytes = readFileSync(bundlePath);
  const bundle = JSON.parse(bundleBytes);
  const cases = catalog.cases.filter(c => c.id === bundle.identity?.caseId);
  if (cases.length !== 1) throw new Error('case-not-unique');
  return { ...verifyPurchase(cases[0], bundle), expectedCatalogSha256: expectedCatalogHash,
    evidenceSha256: sha256(bundleBytes) };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    if (process.argv.length !== 5) throw new Error('usage: fixture-oracle.mjs catalog expected-sha256 bundle');
    const report = verifyFiles(...process.argv.slice(2));
    console.log(JSON.stringify(report));
    process.exitCode = report.verified ? 0 : 1;
  } catch {
    // No parser excerpts or supplied fixture secrets in diagnostics.
    console.log(JSON.stringify({ verified: false, failures: ['invalid-or-unpinned-evidence'] }));
    process.exitCode = 2;
  }
}
