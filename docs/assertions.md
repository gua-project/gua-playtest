# Pure assertions and ordinary three-valued logic

## 一つの値を比べるときの考え方

Assertion は「取得した購入数が1か」のような一つの比較を担当します。対象の検索、時間の履歴、Runの終了は別の層です。同じ数値の `integer` と `number` を暗黙変換すると、ゲームが宣言した型との不一致を見逃します。元の Gua Value JSON を保ったまま比較します。

結果は True／False／Unknown と違反の有無を分けます。「所持金が10ではない」という比較でも、読み取りがgapなら Unknown で、否定したから True にはなりません。型の食い違いや不正なValueも単なる比較不成立と区別します。観測障害をゲームの成功・失敗として採点することを防ぐためです。

[PreparedAssertion.CreateJson / EvaluateJson](../src/Gua.Playtest.Core/Assertions/PreparedAssertion.cs)で期待値の準備と実値の比較を追い、[EvaluationResult / TruthLogic](../src/Gua.Playtest.Core/Assertions/EvaluationResult.cs)で結果の組合せを確認してください。[AssertionTests](../tests/Gua.Playtest.Contracts.Tests/AssertionTests.cs)は数値、enum、collection、regexの境界、[TruthLogicTests](../tests/Gua.Playtest.Contracts.Tests/TruthLogicTests.cs)は三値の表に対応します。複数対象と時間は [conditions.md](conditions.md)、テスト入口は [開発者ガイド](developer-guide.ja.md)にあります。

Issue #4 implements ASSERT-001/002/003/006 in native-free `Gua.Playtest.Core.Assertions`. Input remains the existing assertion/read/Value JSON. No new Selector or Value wire format is introduced. `Gua.Playtest.Runner.AssertionPreparation` prepares every leaf before execution, including nested groups and time nodes; it does not implement selector quantifiers, target counts, temporal state or Run termination.

## Stable API and failure boundaries

```csharp
var catalog = EnumCatalogSnapshot.Create(approvedHostCatalogJson);
var options = new AssertionOptions(effectiveRegexTimeoutMs, effectiveRegexMaxPatternLength);
var leaves = AssertionPreparation.Prepare(validatedCondition, options, catalog);
var result = leaves[0].Comparison.EvaluateJson(originalValueWireJson, currentApprovedHostCatalog);
// Or Evaluate(originalParsedValueJson, currentApprovedHostCatalog)
// Or EvaluateUnavailable(EvaluationCode.Gap), with no fabricated Value
```

`PreparedAssertion.Create/CreateJson` eagerly checks kind, Target/read shape, operator/type/expected compatibility, exact Value semantics, tolerance, enum membership and regex syntax/limits. Failures throw `AssertionConfigurationException` with a fixed code. Expected values and enum definitions are immutable copies. Runner preparation takes a statically validated tree and returns read-only `(ConditionPath, Comparison)` leaves; #5 owns target/time node semantics and must prepare every applicable tree before considering success.

`EvaluationResult` separates `Truth` (False/True/Unknown), `Error` (None/InvalidConfiguration/ObservationContractViolation) and fixed `Code`. Missing/getter-error/stale/gap/truncated reads and regex timeout are Unknown, including for notEquals/notContains. Invalid actual Values, declared/actual type disagreement or incompatible enum definitions are observation violations, never ordinary False. Results carry no values, names, patterns or exception messages; #7/#11 still apply profile policy when projecting feedback.

Raw JSON entry points use the bounded decoder (4 MiB, depth64, 50000 nodes) and reject duplicate keys and malformed Unicode. Parsed-node callers must preserve original numeric lexemes through the validated decoder. Binary64 deserialization before integer validation loses evidence. #7 owns source/epoch/runtime/profile/owner/registration/type-cache identity, freshness and completeness; comparison never resolves selectors or invents enum metadata.

## OPEN-02 decisions

Gua 1.1.1 `protocol/specs/value-v1.md` remains the Value semantic authority. Core enforces the same rules without native loading; tests also use real pinned native Gua APIs.

- integer and number never coerce. Integers are exact mathematical integers within ±9007199254740991: `1e0`/`1.0` are valid, `1.00000000000000001` is invalid before rounding. number uses finite binary64, including underflow/subnormals. Both equate -0 and 0; a set containing both is invalid.
- String equality/contains/startsWith/endsWith use case-sensitive ordinal comparison without Unicode normalization. `é` differs from `e + combining acute`. NUL and supplementary characters are retained; isolated surrogates are invalid.
- enumType is a qualified ASCII identifier. Same-ID identical member sets can repeat independent of order; conflicting definitions are rejected atomically. Definition changes need a new ID. Empty enum collections still require registered types. Alias names remain distinct. Numeric values and automatic flags splitting are refused; a flags-like name is valid only when explicitly registered. Current same-ID definition changes are observation violations, even if the current member still exists. Host catalog scope/identity belongs to #7.
- list equality preserves order/repetitions; set equality ignores order and rejects duplicates. equals/notEquals require identical kind/elementType/enumType. contains/notContains expects a scalar element. containsAll/Any accepts list or set with matching element/enum type without converting the actual collection. containsAll is membership, not multiplicity: `[x]` containsAll `[x,x]` is True. Empty expected collection yields True for containsAll and False for containsAny. Duplicate expected sets remain invalid.
- count operators require nonnegative integer expected Values and count repeated list entries. isEmpty/isNotEmpty has no expected Value. Sequence operators require a list with matching element/enum type. containsSequence is contiguous; an empty sequence is a valid prefix/suffix/subsequence, including of an empty list.
- approximatelyEquals is number-only, with explicit finite nonnegative absolute tolerance and inclusive `abs(actual - expected) <= tolerance`. No relative tolerance or coercion. Overflowing finite-endpoint differences exceed any finite tolerance.
- matches uses .NET regex, CultureInvariant, explicit positive finite match timeout and maximum UTF-16 pattern length. Default matching is case sensitive; explicit inline regex options follow .NET. Matching searches for a match; use anchors for whole-string matching. Syntax/length/limit errors fail eagerly. Only trusted Runner invokes execution; timeout yields Unknown/RegexTimeout. No infinite timeout or implicit defaults. .NET's maximum finite timeout is 2147483646ms; structurally representable 2147483647ms is refused at runtime preparation.

JsonSchema.Net 7 uses decimal and cannot represent every finite Gua binary64 (e.g. 1e308). Numeric Values are first checked for exact integer/finite number semantics and duplicate sets. A structural-validation copy normalizes integers and uses bounded number placeholders and empty structural collection copies after element/uniqueness validation. All original fields/kinds/types and constraints remain checked; original data/retained bytes never change. Static document loading shares this handling so valid Values survive loading, without proving runtime enum catalogs or Goal truth.

## OPEN-05 ordinary truth table

`TruthLogic.All/Any` inspects every child without caching truth. Errors override truth; InvalidConfiguration overrides ObservationContractViolation. Otherwise a decisive False for all / True for any overrides Unknown. With only Unknown and neutral truth, the first Unknown reason is retained. Empty ordinary groups are invalid; selector quantifiers have separate empty-search rules in #5.

| A | B | all | any |
| --- | --- | --- | --- |
| False | False | False | False |
| False | True | False | True |
| False | Unknown | False | Unknown |
| True | False | False | True |
| True | True | True | True |
| True | Unknown | Unknown | True |
| Unknown | False | False | Unknown |
| Unknown | True | Unknown | True |
| Unknown | Unknown | Unknown | Unknown |

Temporal Pending/Expired, arbitrary time/group nesting, target membership changes and deadlines remain #5; terminal/result precedence remains #6. This resolves #4's ordinary portion of OPEN-05, not the temporal decision.

## Acceptance and remaining integration

`AssertionTests` supplies literal golden scalar/collection/boundary expectations, an independent 26-operator × 15-type compatibility table, a real regex timeout and loader/raw-JSON checks. `TruthLogicTests` supplies nine literal truth-table rows, errors in decisive groups and no-history examples. `AssertionIntegrationTests` checks unchanged upstream valid/invalid Value and comparison fixtures against the real Gua 1.1.1 native API and Core, including subnormals, plus trusted Runner eager preparation/execution and an invalid unselected nested branch.

| Requirement | Evidence / remaining owner |
| --- | --- |
| AT-ASSERT-001/002/003/006 | Core golden tests, exhaustive type table, native fixture and eager Runner preparation |
| VALUE-001/004, FIX-007 | Independent upstream fixture/native equality; #16 owns broader fixture oracle wiring |
| OBS-005 | Explicit unavailable reasons; #7 owns actual provider gap/getter/identity/freshness evidence |
| ACTION-002 | #5/#7/#11 propagate violations before action; comparisons send no actions |
| STALL-002 | #12 consumes typed results; no progress/recovery state here |
| OPEN-04 | #2 schema/read regions reused; #7 owns real selector/cache/identity integration |

Issue #4 remains open until downstream cooperative acceptance is connected and demonstrated. Unit/native evidence does not claim a complete Explore/Replay engine or game transport.

Shared schema evaluation is serialized across the complete pinned reference graph. The [exact JsonSchema.Net 7.3.4 source](https://raw.githubusercontent.com/json-everything/json-everything/be57a968e9a136002606f09f16c53e10ef33f27d/src/JsonSchema/JsonSchema.cs) changes evaluator options and constraint caches during evaluation; separate root locks would leave referenced schemas shared. Latest-main CI showed sporadic SchemaInvalid on otherwise-valid comparisons on Linux/macOS Intel. Parallel regression calls check both fixed valid outcomes and rejection of unknown configuration/observation fields. Only schema registration/evaluation holds the gate; scalar comparisons, hash membership and bounded regex execution run after it releases. Package pins, all golden expectations and the 10s large-comparison ceiling are unchanged.

PR31's actual Codex review identified missing enum metadata dereference and quadratic large-set scans. Regression tests cover scalar/collection missing enumType as configuration/observation errors and 18000-element equality/membership plus repeated sequences under a predeclared 10s CI ceiling. Set equality and containsAll/Any use hash membership; contiguous sequence matching uses a precomputed prefix table. The collection structural copy avoids the schema library's quadratic uniqueItems scan only after every element and duplicate set check has completed.
