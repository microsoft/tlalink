---
name: tlalink-maintain-links
description: 'Create, update, check, remove or explicitly migrate TlaLink model-owned source links using Roslyn selectors and links list/check/set/remove/migrate. Use when an agent must maintain binding/capture metadata or repair attachment drift. Keeps edits separate from inspection, independent requirements, frozen evidence and human approvals.'
---

# Maintain model-owned connections deliberately

Use this workflow for requested connection changes, not merely to answer an
inspection question. Start with [source navigation](../tlalink-source-navigation/SKILL.md)
and [correspondence inspection](../tlalink-inspect-correspondence/SKILL.md).
The [maintenance reference](../../../REFERENCE.md#maintaining-model-owned-links)
owns syntax/options; [agent safeguards](../../../AGENTS.md) own repository policy.

## Establish ownership and intended change

1. Read package instructions and inspect the existing scope, native endpoint,
   C# attachment and captures. Preserve unrelated worktree changes.
2. Establish explicit compiler context and selected source files. Prefer an
   applicable native `--regression-result` over reconstructing references or
   defines. Supply `--annotation-version 4` and the selected pinned frontend.
   Never provision or substitute tools during analysis.
3. Obtain the actual Roslyn declaration ID through `symbols`. Select a native
   endpoint from its authoritative inventory; do not guess declaration-name pairs.
4. Decide whether this is metadata maintenance or a behavioral/abstraction
   change. Surface changed assumptions for an explicit decision; do not
   silently strengthen a model to fit code or weaken a requirement to pass.

Maintain one convention: formal TLA+/PlusCal, Dafny or P comments own typed
`bind.source` and associated metadata; maintained C# stays unmodified. Use
stable unique `scope::name` IDs and the shared source-preparation selector
contract, not line numbers, external JSON binding stores or another parser.

Check the package's selected wrapper/distribution supports the requested action.
A usage error for a documented command is a capability/setup problem, not
attachment drift: do not edit links to work around it. Pause for authorized
documented distribution selection, then repeat preflight and inspection.
Changed tool pins do not authorize refreshing requirements or review decisions.

## Make the smallest authorized edit

| Operation | Procedure |
|---|---|
| Inventory | `links list` for the selected scope/target/native tool |
| Validate | `links check` with explicit C# compilation inputs |
| Create/update | `links set --binding <id> --symbol <Roslyn-ID>`; new bindings also need `--endpoint <native-name>` |
| Remove | `links remove --binding <id>` only when removing that connection is intended |
| Bulk conversion | `links migrate` plan first; see the separate procedure below |

This independently synthetic recipe runs from the repository root. Keep the
common arguments and invocation in the same shell call when shell state is
not retained by the agent host.

```powershell
$common = @(
    '--annotation-version', '4',
    '--scope', '.\examples\Components\src\Counter\verification\scope.json',
    '--target', 'counter',
    '--source', '.\examples\Components\src\Counter\Counter.cs',
    '--jar', '.\.tools\tla\v1.8.0\tla2tools.jar'
)
.\TlaLink.ps1 links set @common --binding 'counter::step' --endpoint Next --symbol 'M:ComponentsExample.Counter.Step' --syntax ExpressionStatement --tokens 'Value = (Value + 1) % 3;'
if ($LASTEXITCODE -ne 0) { throw 'Link update failed; inspect the emitted diagnostics.' }
.\TlaLink.ps1 links check @common --format summary
if ($LASTEXITCODE -ne 0) { throw 'Link check failed; do not report the edit as validated.' }
```

This example intentionally updates maintained metadata; do not run it during a
read-only question. Use the actual user's selected scope and authorized change.

For practice or skill validation, copy the public example's `Counter.cs`,
`Counter.tla` and `scope.json` into a new disposable directory. This scope's
model input is document-relative, so keep `scope.json` beside `Counter.tla`.
Point `--scope` and `--source` at those copies, keeping the pinned frontend
path absolute. Exercise update, inspect, remove, expected failing check,
recreate and passing check there; assert unchanged original hashes and clean
up only the files/directory created by the exercise. Treat the expected failed
check as a test control, not a reason to modify required binding declarations.
Migration on this already-converted fixture is only a no-op planning test;
use the focused link suite for a real source-owned migration control.

## Selector and capture rules

- Omit syntax/tokens for a whole declaration. For a statement/expression use an
  exact Roslyn syntax kind and token filter when appropriate. Token matching
  ignores trivia, not semantic changes.
- Repeated syntax requires an explicit one-based `--match` and
  `--expected-matches`. Positional selectors can survive equal-count reordering
  while changing meaning: inspect the resolved statement after editing.
- Do not replace every control's positional selector with exact tokens.
  Independently authored behavioral mutation controls must still reach their
  intended attachment; an attachment/parser failure is not a successful control.
- Preserve existing captures unless deliberately changing them. Repeated
  `--capture '<typed JSON>'` replaces that binding's capture entries;
  `--clear-capture true` clears them. Capture objects must name this binding.
  Only the TLA+/TLC adapter supports captures, not Dafny/P.
- Preserve binding reasons and exclusions. Exclusions scope correspondence,
  not runtime code, input pins or relevant argument/callee effects.
- Moving a binding to another native endpoint requires explicit remove then
  set. Removal does not remove independent required-binding declarations or
  acceptance policy; a resulting check failure must remain visible.
- Put PlusCal annotations on authored labels, never generated translation
  bodies. Regenerate translations explicitly through the documented frontend
  procedure when required; do not hand-edit generated code.

## Bulk migration is a separate, reviewed operation

1. Run `links migrate` with explicit sources/scope and selected pinned tools.
   It defaults to a plan; `--target` narrows it, otherwise all scope targets
   participate and each selected language needs its frontend.
2. Save the plan to a new `--output` file. Review IDs, exclusions, capture
   preservation, proposed source stripping and token/byte checks.
3. Differing reasons block by default. Only after reviewing both meanings use
   `--reason-policy preserve-both`; retaining both does not prove compatibility.
4. Use `--strip-source false` when importing from immutable copies that must
   remain unchanged.
5. Apply only the authorized conversion with `--apply true`. Keep before/after
   receipts and investigate interrupted application; edits are not a multi-file
   crash transaction. Never overwrite historical evidence or prepared outputs.

Migration does not authorize renewing source allowlists, configuration pins,
independent requirements or reviews. Handle any intentional successor inputs
explicitly under their own package policy, not as automatic drift repair.

## Validate and report separately

Run `links check`, inspect affected pairs with `links inspect`, and review the
relevant authored diff. For changed behavior/attachment points, run affected
regressions and defect-relevant controls with the same compiler/native context.
Do not overwrite immutable baselines or change checker requirements to clear
failures.

Report edited IDs/files, selector/capture changes, checks and native controls,
current applicability and remaining correspondence questions. CLI edits and
successful checks do not approve atomicity or semantic equivalence, discharge
human gates, or compose backend results into C# correctness. Do not commit or
publish private package material or record agent self-review as human approval.
