# Paired source correspondence vocabulary

Annotation version 4 uses standard JSON inside native block comments.
Maintained examples put the unique `scope::name` ID and typed `source` selector
on the formal declaration. Roslyn resolves the C# declaration/syntax and the
native frontend resolves the formal attachment independently. C# remains
unmodified; declaration spelling is not a pairing heuristic.

Use [symbols and link maintenance](../../REFERENCE.md#maintaining-model-owned-links)
to discover, inspect, validate and edit metadata. Selectors reuse the existing
preparation contract rather than a new tokenizer or external binding catalogue.
Competing owners of one ID are errors. The example-first migration leaves
nonmigrated consumer inputs and historical receipts unchanged.

Bulk migration uses the same native indexes and selector resolver, defaults to
read-only planning and requires explicit apply. Typed exclusions can also be
formal-owned through `exclude.source`; their alias scope, reason, input pins and
effect checks remain intact. Migration is metadata movement, not correspondence
approval or permission to revise independent requirements.

JSON manifest schema 4 is independent of annotation grammar 4. Earlier
annotation versions and source-side `target` properties are rejected.

## One vocabulary, typed declarations

| Array | Responsibility |
|---|---|
| `bind` | Name a correspondence; formal-owned entries select C# through `source` |
| `capture` | C# observation metadata alongside the formal-owned binding, for a supported adapter |
| `exclude` | C# correspondence-scope disposition, not code removal |

`BindAnnotation`, `CaptureAnnotation`, `ExcludeAnnotation` and resolved
`FormalLinkDeclaration` / `ResolvedCorrespondence` records are the data model.
`System.Text.Json` handles syntax; enums represent finite capture choices.
Unknown properties, duplicate JSON properties, invalid IDs, null entries and
unsupported attachments fail explicitly. No XML or custom token DSL is accepted.

An ID has exactly two stable-name components separated by `::`. The first
identifies an explicitly declared formal scope; the second names the
correspondence, not a C# or native symbol. IDs are ordinal case-sensitive and
must never be normalized as filepaths.

## Independently resolved endpoints

C#:

```csharp
enabled = true;
```

TLA+:

```tla
(* @link {
  "bind": [{"id":"lamp::enable","reason":"Assignment under the instance gate.",
    "source":{"symbol":"M:Lamp.Enable","syntax":"ExpressionStatement","tokens":"enabled = true;"}}]
} *)
SwitchOn == enabled' = TRUE
```

The names intentionally differ. Roslyn resolves the statement; SANY resolves
the operator. For PlusCal, attach the formal declaration to the authored label
and explicitly regenerate its translation with the pinned native tool. Never
insert markers into generated translation bodies.

Dafny and P use their native `/* ... */` comment carrier with the same JSON
`bind` payload. Their authoritative frontends supply declaration kinds and exact
source spans. A native declaration may carry several distinct IDs, as may a C#
location; each selected ID still identifies one unambiguous pair.

The [synthetic cases](source-correspondence-cases.md) distinguish language-native
claims and controls. The case matrix is not itself an execution result.

## Matching and selection

For each selected correspondence:

1. Require exactly one C# endpoint and one authoritative formal endpoint.
2. Reject a missing counterpart in either direction and duplicate declarations.
3. Keep the resolved native kind, location, signature and arity; do not infer
   them from the shared name.
4. Validate the requested operation against backend-specific capabilities.
5. Pin original source, formal, configuration and compiler-context bytes.

Several captures of one C# endpoint at distinct points do not create multiple
implementation endpoints. Generated event IDs retain their paired binding ID.
Native renames leave a correspondence intact when its annotation remains
attached. Moving or retargeting an endpoint remains an evidence/review change.

One schema-4 `correspondence-scope` declares aliases, native languages and
explicit input files. TLA+ scopes select one root and preserve import pinning;
`required_bindings` can independently require named correspondences even when
the formal marker was accidentally removed. Native marked endpoints in a
selected TLA+ scope are checked in both directions. Unmarked helper operators
do not require invented source links.

Dafny/P configurations explicitly select binding IDs for their particular
claims/cases. Other marked correspondences remain unselected rather than
implicitly requiring every native engine. Integrated `tla_targets` explicitly
selects optional TLA+ review alongside Dafny.

No directory discovery, name-based mapping, inheritance, override chain or
fallback checker supplies missing endpoints. Missing prerequisites are
incomplete, not a reason to weaken the claim.

## Source execution metadata

```tla
(* @link {
  "bind": [{"id":"lamp::value","source":{"symbol":"F:Lamp.enabled"}}],
  "capture": [{"binding":"lamp::value","guard":"gate","type":"bool"}]
} *)
ObservedEnabled == enabled
```

```tla
(* @link {
  "bind": [{"id":"lamp::change","source":{
    "symbol":"M:Lamp.Change(System.Boolean)","syntax":"ExpressionStatement",
    "tokens":"enabled = requested;"}}],
  "capture": [{"binding":"lamp::change","point":"commit",
    "classification":"transition","actor":"actorId","operation":"$call",
    "arguments":["requested"],"observe":["lamp::value"]}]
} *)
Change == enabled' = ~enabled
```

Formal declarations with the same IDs own their native semantics. Source capture
currently supports the TLC execution adapter only. It is not an Apalache trace
adapter, a Dafny oracle or a P monitoring adapter.

Preserve separate entity/invocation identities, `$call` across awaits, restricted
scalar arguments, actually held guards, interference checks and no cross-lock
snapshots. Each JSON argument element is exactly one C# expression; it cannot
expand into extra arguments. Data values never become causal identity keys.

Keep invoke/commit/complete/fault/cleanup distinct, including once-only resource
acquisition, successful disposal versus throwing disposal, original exceptions
and reverse cleanup. Existing unsupported capture shapes remain unsupported.
Hooks remain separate and forbidden under source locks. Task/queue/caller-callee
handoffs are not inferred from matching IDs, timestamps or sequence numbers.

Dafny retains explicit adapters, oracle cases and source premises. Ghost lemma
links are review-only. Statement/expression bindings need an explicit adapter
for oracle execution. P retains native exploration/induction/monitoring inputs
and claims. Matching identities do not create a common native execution API.

## Reviewed method summaries

The same paired method/operator binding can identify a reviewed method summary;
there is no fourth inline annotation array. Optional correspondence-scope
`method_summaries` metadata declares explicit C# source files, pure/atomic
intent, assumptions, limits, inspection identity and acyclic same-scope summary
dependencies. The behavior remains a stateless parameterized TLA+ relation.

Project TLC/trace stages explicitly select summary bindings and import their
modules. Current source-pinned inspection supports substitution; it does not
prove native argument/state mapping, purity, linearizability or caller
preconditions. An agent-origin inspection is distinct from human approval.
Source/model changes invalidate support independently of caller model results.
See the [reference](../../REFERENCE.md#reviewed-method-summaries).

## Source exclusions and routine logging

```csharp
/* @link {
  "exclude": [{"id":"diagnostic-dump","from":["lamp"],
    "reason":"Diagnostic output is outside this correspondence scope."}]
} */
{
    Diagnostics.Write(0);
}
```

An exclusion attaches to the next declaration, statement or block, including its
enumerated nested regions. It does not remove code from compilation/execution,
input fingerprints, competing-access inspection or retention edges. A same-scope
binding inside it conflicts; overlapping exclusions do not introduce precedence.

Scope omission rules declare `id`, explicit `targets`, exact Roslyn declaration
IDs in `calls`, `reason` and boundary `assumptions`. The fixed treatment omits
routine synchronous void invocations from model correspondence, not execution.
Resolve method symbols uniquely in the supplied compiler context, including
metadata assemblies. Do not match name patterns, arbitrary overrides or
transitive wrappers.

Keep receiver/argument evaluation, property effects, awaits, cleanup, exceptions
and external calls visible. Inspect known callee closures conservatively;
modeled-state writes/ref escapes conflict and unresolved effects remain boundary
obligations. Reads remain candidate accesses, not an automatically proven
snapshot. Report matched sites and zero-match rules explicitly.

An exclusion or logging rule is not an implementation stutter, hidden model
action, source-coverage approval or acceptance exemption. Native hidden actions
remain explicit TLA+ configuration with reasons, disjoint from observed actions.

## Evidence and migration

Source and native inventories expose independently declared endpoints,
resolved pairs, selected/unselected scope, capture capability, exclusions,
omission matches and outstanding boundary questions.

Archive readers reconstruct pairs from archived source/formal bytes and pinned
authoritative native inventories, not live files or recorded target strings.
All authored bytes, including excluded regions and assumptions, remain
fingerprinted. An unchanged ID never makes changed code equivalent or old
evidence current.

Preserve native outcomes, archive consistency, current applicability,
correspondence review and acceptance as distinct results. Matching IDs,
compatible signatures and backend passes do not establish atomicity, behavioral
equivalence, C# refinement, general coverage, liveness or cross-model composition.

Migrate maintained endpoints together, collect new successor evidence and retain
historical receipts and independent requirement pins. Keep preparation for
copied controls and explicitly immutable/external sources, not a second registry
of source/formal relationships.
