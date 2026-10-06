# Independent Gua Value golden fixture

`gua-value-v1.json` is unchanged from `protocol/fixtures/value-v1.json` at Gua tag `gua-v1.1.1`, commit `88f5dca4aa97c5d5187ab66ea4416377f3affc96`, under the MIT license retained in `docs/schemas/gua-1.1.1/LICENSE`.

SHA-256: `3789dd636327c339d190dfe55030a48ec15702954f2d17536102f04b70654fed`.

Its expected values predate this comparator. Foundation tests check them against published native Gua and native-free Playtest comparison. Gua cross-type inequality corresponds to assertion configuration rejection, because Playtest requires matching declared types.

Playtest-specific operator/truth expectations are literal data in `AssertionTests.cs` / `TruthLogicTests.cs`, never outputs of the comparator.
