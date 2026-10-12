# Optional walking profile

`EnvironmentDocument.Navigation` / `environment.navigation` is optional. Its
`schemaVersion: 1` names the walk3d-r1 configuration contract. A Scenario still
contains one Goal and no navigation procedure. Replay retains the original
Recording and does not repair routes or introduce a navigation Planner.

The profile declares one grounded actor selected through the existing Gua World
selector and stable `actorDomainId`. `positionField: position` refers to World
Object.position in `world3d`; `spaceId` and `spaceEpoch` identify the spatial-r1
space. `plane.up`, `axisX` and `axisY` are finite orthonormal vectors. They define
the walking plane; neither Input vector2 nor its description determines them.

`capsule.height` is the total upright capsule height including both hemispheres.
Radius is positive and height is at least twice radius. `footOffset` and
`centerOffset` are world-space displacements from Object.position, and center
must equal foot plus up times half the height. `representation` explicitly says
exact or approximation. Queries use the existing Gua capsule endpoints/radius,
policy ID and consistency; the profile creates no provider or registry.

`movement` declares planarTranslation, the existing move Action ID, each axis's
finite range spanning zero, polarity ±1, world/camera/body basis, and neutral
release at the Timed Segment boundary. Camera/body require `basis` containing
two vectors of three existing numeric Observe/Property/standard scalar reads.
World-relative profiles omit that dynamic basis. `motion.velocity` also groups
three numeric reads; `motion.grounded` is an actor boolean read. No object-valued
Observe, source inspection, test movement or automatic calibration is implied.

`motion` bounds speed, stopping distance and stopping time. `arrival` declares
distance, speed and height tolerance plus a slope limit below 90 degrees. A
neutral input receipt does not prove physical stopping. Arrival requires current
distance, height, grounded and velocity facts; pickup and Goal success still use
the existing Assertion/Scenario checks.

`survey` declares a finite world-coordinate box, signed height band along up,
positive cell size and support spacing. Only singleGroundedSurface topology is
supported. Jumping, steps, moving platforms, falling shortcuts, flight, swimming,
tank/vehicle controls, multiple actors, automatic multilayer connections and
loading unknown regions are outside this profile. An unsupported declaration is
rejected rather than flattened into a plane.

Each target declares a public semantic selector, stable `domainId`, one or more
author-approved world-space `approachPoints`, a positive `approachRadius` around
those points, and a boolean `uncollected` read on that target. Points must lie in
the declared box and height band. Object pivots do not generate approaches;
candidate generation and current spatial clearance belong to the map/follower.
Runtime ID replacement does not create a new domain target or pickup fact.

All observation age, input interval, query/deadline, node/edge, replan, LLM and
approach-candidate ceilings are explicit. Zero LLM calls is allowed. Input
duration stays within the existing Gua 60000ms hard ceiling and the reduced
Environment segment limit. `provenance` identifies the fixture author and the
source of all parameters; values are author declarations, not universal defaults.

## Trusted preparation composition

Static validation checks offline structure, finite values, internal consistency
and that `preparation.fixtureId` equals Environment.fixture.fixtureId. It neither
connects to a host nor grants read/action permissions.

Trusted composition passes `NavigationPreparationPolicy` in `PreparationPolicy`
when that Environment has a profile. It supplies the effective allowed actions,
reads and segment ceiling after all reductions. It selects the existing approved
`IApprovedSetup` whose FixtureId matches the profile. Setup remains available
only to Preparing; its existing allowlist, count, deadline and receipt checks
apply. Successful Setup does not establish navigation readiness.

The preparation adapter returns `InitialBoundary.Navigation` as read-only public
evidence from the same subscribe/capture request as the initial Run observation.
Samples carry source, session epoch, space identity, physics sample ID and
run-owned monotonic real capture time (`ClockId: run-real`). They cannot subtract
game timestamps from real timestamps. All scalar components and resolved objects
must have the same sample ID/time, be current within the explicit age ceiling,
and match that request and identity. Providers must return immutable evidence
and certify unique selector resolution through MatchCount, actor controllability,
the declared loaded survey and topology, the author-approved movement mapping,
and strict Timed Segment/neutral-release support. Missing attestations fail closed.

The check reuses pinned Gua Action-map and spatial-r1 provider schemas, verifies
an active public holdable vector2 action with explicit compatible range, and
requires raycast/overlap/sweep, capsule, policy, consistency, deadline and space
support. Confirmation-required/private actions are refused for automatic walking.
Position, grounded, velocity, public dynamic basis and target existence/uncollected
facts are separately validated. A valid preparation check grants no dispatch or
Goal authority. Every later query and Timed Segment still goes through the
existing Runner permission, budget, cancellation and fresh-observation gates.
