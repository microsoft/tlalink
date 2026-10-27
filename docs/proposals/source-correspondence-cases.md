# Source vocabulary: synthetic conformance cases

Status: conformance design matrix for the [vocabulary](source-correspondence.md).
The snippets use annotation-version-3 JSON. This document is not an executable
suite, native test result or evidence; executable core/native regressions live
in the console test project. The matrix also records controls to preserve when
extending those suites.

All examples are independently synthetic. The common concern is a small
Boolean indicator: selected assignments should match a two-state model, a pure
decision helper should correspond to a Dafny/P function, and diagnostic output
should be deliberately outside the selected correspondence scope.

## Fixture organization

The headings below name conceptual fixture inputs, not files that exist or
commands that can currently be run. A future regression suite should materialize
these snippets in isolated temporary directories and vary one input per control.
Do not copy an expected outcome into a success-shaped receipt.

| Alias | Language | Native input/context |
|---|---|---|
| `lamp-tla` | TLA+ | `Lamp`, from the TLA+ fixture below |
| `lamp-detail` | TLA+ | Same module, independently selected correspondence |
| `lamp-pcal` | TLA+/PlusCal | `LampProgram`, after explicit native translation |
| `lamp-dafny` | Dafny | `LampMath`, from the Dafny fixture |
| `lamp-p` | P | Explicit P fixture with testcase `tcLamp` |

Each case must supply its real explicit C# compiler context, complete native
input closure and pinned prerequisites. The table is not a manifest.
TLA+ targets also declare the required inline IDs: `lamp-tla` selects
`lamp-value`/`lamp-change`, `lamp-detail` selects `lamp-change-detail`, and
`lamp-pcal` selects `lamp-start`. Missing IDs are structural failures, not an
automatically reduced claim.

### Common C# input

```csharp
using System;

namespace VocabularyCases;

public sealed class Lamp
{
    private readonly object gate = new();

    /* @link {
  "bind": [
    {
      "id": "lamp-value",
      "target": "lamp-tla::Enabled"
    }
  ],
  "capture": [
    {
      "binding": "lamp-value",
      "guard": "gate",
      "type": "bool"
    }
  ]
} */
    private bool enabled;

    /* @link {
  "bind": [
    {
      "id": "lamp-command",
      "target": "lamp-p::eSet"
    }
  ]
} */
    public void Set(bool requested, int actorId, int operationId)
    {
        lock (gate)
        {
            /* @link {
  "bind": [
    {
      "id": "lamp-change",
      "target": "lamp-tla::Set"
    },
    {
      "id": "lamp-change-detail",
      "target": "lamp-detail::Set"
    }
  ],
  "capture": [
    {
      "binding": "lamp-change",
      "point": "commit",
      "classification": "transition",
      "actor": "actorId",
      "operation": "operationId",
      "arguments": [
        "requested"
      ],
      "observe": [
        "lamp-value"
      ]
    }
  ]
} */
            enabled = requested;
        }

        Diagnostics.Write(1);
    }

    /* @link {
  "bind": [
    {
      "id": "lamp-decision",
      "target": "lamp-dafny::LampMath.Desired"
    },
    {
      "id": "lamp-protocol-decision",
      "target": "lamp-p::Lamp.Desired"
    }
  ]
} */
    public static bool Desired(bool requested) => requested;

    public void DumpDiagnostics()
    {
        /* @link {
  "exclude": [
    {
      "id": "lamp-dump",
      "from": [
        "lamp-tla",
        "lamp-detail",
        "lamp-dafny",
        "lamp-p"
      ],
      "reason": "Diagnostic output is outside these correspondence scopes."
    }
  ]
} */
        {
            Diagnostics.Write(0);
        }
    }
}

public static class Diagnostics
{
    public static void Write(int marker) => Console.WriteLine(marker);
}
```

This fixture deliberately contains an instance, void `Set` method and a static,
Boolean `Desired` helper. Do not silently use one as the other's Dafny oracle
adapter. No model describes `Console.WriteLine` or proves its effects harmless.

### Shared logging rule

Conceptual rule inputs:

| Field | Value |
|---|---|
| ID | `diagnostic-output` |
| Targets | `lamp-tla`, `lamp-detail`, `lamp-dafny`, `lamp-p` |
| Calls | `M:VocabularyCases.Diagnostics.Write(System.Int32)` |
| Treatment | `omit-invocation` |
| Reason | Diagnostic output is outside the selected behavioral claims |
| Reviewed boundary | Claims omit diagnostic I/O, logging failures/blocking and logger internals; no general progress claim |

The ordinary `Diagnostics.Write(1)` call is rule-omitted. The call inside
`lamp-dump` has the explicit region disposition, not a duplicate rule
disposition. `Console.WriteLine` is not transitively matched by this rule.
Its source/effects remain available for boundary review.

Binding selection, code dispositions, boundary review and native claims are
separate assessments. A positive omission classification is not human approval.

## TLA+ / TLC cases

### Native TLA+ input

```tla
---------------------------- MODULE Lamp ----------------------------
VARIABLE
  \* @type: Bool;
  enabled

vars == <<enabled>>
Init == enabled = FALSE
Enabled == enabled

\* @type: (Int, Int, Bool) => Bool;
Set(actor, operation, requested) == enabled' = requested

Next == Set(1, 1, TRUE) \/ Set(1, 1, FALSE)
TypeOK == enabled \in BOOLEAN
AlwaysOff == enabled = FALSE
====================================================================
```

`actor` and `operation` are identity positions. `requested` is an explicit
Boolean action argument, not an entity identity. Native typing annotations are
independent of source bindings.

The finite TLC configuration for the baseline is:

```text
INIT Init
NEXT Next
INVARIANT TypeOK
```

For trace cases, select `lamp-tla`, `lamp-value` and `lamp-change`, the matching
configuration and no hidden actions. The intended workload constructs one lamp
and calls `Set(true, 1, 1)` then `Set(false, 1, 2)`. Its generated execution must
be tied to observed compiler inputs and a loaded binary before validation.

| Case | Change/operation | Required outcome |
|---|---|---|
| T01 | Resolve baseline TLA+ bindings with SANY/Roslyn | State/action endpoints and three action parameters resolve; capture arguments distinguish two identities from one data value |
| T02 | Select `lamp-detail` only for structural review | `lamp-change-detail` resolves without requiring `lamp-tla` capture; code is not regenerated with different bindings |
| T03 | Run native TLC with baseline configuration | Completed finite-model invariant result, not a C# conformance claim |
| T04 | Execute/capture the two-call workload and validate the whole trace | One consistent witness with observed values `TRUE`, then `FALSE`; no reset-per-event model state |
| T05 | In a declared copied control, replace only `enabled = requested` with `enabled = !requested` | Capture still resolves and compiles; completed whole-trace rejection, not parser failure |
| T06 | Change target to nonexistent `lamp-tla::Missing` | Structural failure before instrumentation |
| T07 | Capture observed field outside `gate` | Guard validation failure; no cross-lock or unlocked snapshot fallback |
| T08 | Change arguments to `Diagnostics.Write(1)` | Invalid capture expression; logging rule cannot legalize it |
| T09 | Add `AlwaysOff` as the selected TLC invariant | Native completed property rejection, distinct from trace rejection |

T03/T04/T05/T09 are future native integration tests requiring provisioned tools.
The other design cases must still use authoritative frontend resolution where
their expected outcome depends on native declarations.

## PlusCal cases

PlusCal is a frontend of TLA+, not a fourth native proof engine.

### Native PlusCal input

```tla
------------------------ MODULE LampProgram ------------------------
EXTENDS Naturals

(* --algorithm LampOnce
variables enabled = FALSE;
begin
SetOn:
    enabled := TRUE;
end algorithm; *)
====================================================================
```

Explicitly translate a fresh working input with the pinned native PlusCal tool.
Then use SANY and native label information to resolve `SetOn`. Do not hand-author
the generated translation or accept stale generated bytes.

The corresponding separate C# input is:

```csharp
namespace VocabularyCases;

public sealed class LampStartup
{
    private bool enabled;

    public void Start()
    {
        /* @link {
  "bind": [
    {
      "id": "lamp-start",
      "target": "lamp-pcal::SetOn"
    }
  ]
} */
        enabled = true;
    }
}
```

| Case | Change/operation | Required outcome |
|---|---|---|
| L01 | Resolve `lamp-start` against an explicitly translated, current input | Structural label correspondence, not an execution witness |
| L02 | Change the algorithm after translation without retranslating | Freshness blocks analysis; do not edit the generated body to compensate |
| L03 | Rename the target to an absent label | Native endpoint resolution fails; no label-name regex fallback |
| L04 | Run without the selected native frontend prerequisite | Incomplete, not a structural pass or alternate-engine result |

## Apalache cases

Use the same `Lamp` TLA+ module and source bindings. Do not introduce a second
binding language or pretend that Apalache is a capture adapter.

An explicit analysis configuration selects `Init`, `Next`, `TypeOK` and the
candidate invariant `TypeOK` as appropriate for its mode. There are no constants
or ASSUME declarations. Keep native typing, nonempty searches and supported
configuration checks separate from link resolution.

| Case | Change/operation | Required outcome |
|---|---|---|
| A01 | Native typecheck | A typechecking result only |
| A02 | Bounded safety with `TypeOK`, length 2 | Safety through the selected bound, with required nonempty native examples |
| A03 | Full supplied-invariant induction of `TypeOK` | Separate initiation, preservation and implication results with nonempty checks |
| A04 | Bounded safety with `AlwaysOff`, length 1 | Native property rejection after setting the indicator on |
| A05 | Ask Apalache to discharge the T04 whole-trace claim | Unsupported capability; no TLC fallback and no substitution of bounded safety |
| A06 | Add a SANY-resolved ASSUME declaration | Existing Apalache compatibility rejection remains in force |

The parameterized `Set` operator is not selected as an Apalache root predicate.
Selected predicates are named and nullary, preserving the existing adapter
boundary.

## Dafny cases

### Native Dafny input

```dafny
module LampMath {
  function Desired(requested: bool): bool
    ensures Desired(requested) == requested
  {
    requested
  }

  lemma DesiredIsRequested(requested: bool)
    ensures Desired(requested) == requested
  {
  }
}
```

Select `lamp-decision` from the common C# input. The explicit oracle cases are:

| Case ID | Input `requested` | Expected C# / Dafny output |
|---|---|---|
| `on` | `true` | `true` |
| `off` | `false` | `false` |

Use the linked static C# helper directly where the existing oracle adapter
supports it. Cases and source premises reference `lamp-decision`, without
repeating its source and target endpoints.

| Case | Change/operation | Required outcome |
|---|---|---|
| D01 | Resolve `lamp-decision` | C# method and native non-ghost function resolve |
| D02 | Verify the unchanged Dafny module | Native verification result; no automatic C# proof |
| D03 | Run both explicit oracle cases | Matching outputs for the two cases, separate from verification |
| D04 | Copy C# helper and change its result to `!requested`; leave Dafny unchanged | Completed oracle mismatch, not a native Dafny proof failure |
| D05 | Copy Dafny function and return `!requested`, preserving its postcondition | Native verification rejection; do not remove the postcondition |
| D06 | Add a review-only binding to `LampMath.DesiredIsRequested` | Ghost lemma resolves, but is not made executable |
| D07 | Ask to execute that lemma as an oracle | Explicit unsupported executable-target failure |
| D08 | Add `capture lamp-decision ...` to the helper | Unsupported capture adapter, not an automatic conversion to an oracle |
| D09 | Select instance `Set` as a direct Boolean oracle without an explicit compatible adapter | Unsupported signature/adapter failure, not inferred adaptation |
| D10 | Attach an existing source premise to `lamp-decision` by ID | Preserve its own supported-shape validation and independent result; a bind does not discharge it |

D10 is an ID-wiring case, not a claim that the trivial helper satisfies every
source premise. Premise-specific positive/negative source shapes belong in the
existing focused obligation suites.

## P cases

### Native P input

```p
event eSet: bool;

machine Lamp {
    var enabled: bool;

    start state Ready {
        entry {
            assert Desired(true), "desired-on";
            assert !Desired(false), "desired-off";
            send this, eSet, true;
        }
        on eSet do (requested: bool) {
            enabled = Desired(requested);
            assert enabled == requested, "set-result";
        }
    }

    fun Desired(requested: bool): bool {
        return requested;
    }
}

test tcLamp [main = Lamp]: { Lamp };
```

Select `lamp-command` and `lamp-protocol-decision`. An explicit PChecker stage
selects exactly `tcLamp`, 10 schedules, maximum 100 steps and seed 1. Its result
is schedule exploration under that configuration, not exhaustive exploration
or a C# execution comparison.

| Case | Change/operation | Required outcome |
|---|---|---|
| P01 | Resolve both selected bindings | Native event `eSet` and function `Lamp.Desired` retain their distinct endpoint kinds |
| P02 | Run the explicit PChecker stage | Recognized completed exploration outcome under the declared schedules/bound/seed |
| P03 | Copy the P function and return `!requested` | Native completed assertion rejection with the expected selector, not frontend failure |
| P04 | Change target to `lamp-p::Lamp.Missing` | Structural resolution failure before exploration |
| P05 | Add `capture` to `lamp-command` | Unsupported capture capability; no invented C#-to-P monitor |
| P06 | Request a PObserve claim without monitor/parser/log prerequisites | Incomplete; do not substitute PChecker |
| P07 | Request PEx/PSym exhaustion using only the PChecker result | Claim mismatch; no promoted exhaustion result |
| P08 | Request PVerifier induction using only the PChecker result | Claim mismatch; no inferred inductive invariant |

Native P frontend inventory must actually expose the selected event/function
declarations. An adapter that does not expose a kind must report the unsupported
endpoint, not reconstruct it by parsing source text.

## Omission and boundary controls

Apply these controls independently to the common C# fixture. A call omitted for
several aliases must retain a per-target disposition; a logging rule is not a
model action or an inferred stutter.

| Case | Change/operation | Required outcome |
|---|---|---|
| O01 | Apply the exact logging rule | `Write(1)` is rule-omitted; source and native input bytes are still fingerprinted |
| O02 | Inspect `DumpDiagnostics` | Its block is explicitly excluded for the named targets; no duplicate rule disposition |
| O03 | Add `Diagnostics.Write(string)` and call it without registering its declaration ID | Overload remains undisposed; no name-only match |
| O04 | Replace `Write(1)` with `Write((enabled = true) ? 1 : 0)` outside the lock | Argument write remains visible; modeled-state interference/guard violation blocks the boundary |
| O05 | Replace argument with a call that throws | Do not erase the argument call or its exceptional path; trace completion cannot be claimed after an unhandled fault |
| O06 | Replace the logging method with one that mutates observed lamp state | Known callee effects conflict with the omission; do not certify noninterference |
| O07 | Introduce unknown dispatch/reflection in the logger | Surface unresolved boundary effects; no assumed-pure fallback |
| O08 | Put a binding to `lamp-tla` inside the excluded block | Same-target binding/exclusion conflict |
| O09 | Add an overlapping logging rule for the same call/target | Ambiguity failure; no first-match precedence |
| O10 | Replace the call with an awaited logging operation | Initial `omit-invocation` rule does not match; await and Task edges remain explicit |
| O11 | Use a return-valued logging scope in `using` | No automatic omission of acquisition/disposal/fault behavior |
| O12 | Make the logger throw or block under a claim that assumes otherwise | Boundary assumption is unmet; no successful general progress or whole-execution result |
| O13 | Change omitted code after evidence collection | Current applicability must be reevaluated; omission cannot bypass fingerprints |
| O14 | Remove logging API metadata reference | Compiler context is incomplete; exclusion cannot suppress it |
| O15 | Add a lambda/local function inside an excluded block | Inventory enumerates the nested region; its relevant effects are not hidden |
| O16 | Add unknown syntax/options inside an excluded region | Annotation validation still fails |
| O17 | Add an ordinary wrapper around `Diagnostics.Write` | Wrapper is not transitively matched by the API rule |
| O18 | Exclude code required by an independent coverage obligation | Coverage/acceptance stays unmet; a disposition is not an approval |

For O06, materialize a deliberate synthetic callee/receiver mutation so Roslyn
can inspect the access; do not infer callee effects merely from a method name.
O07 tests honest incompleteness where such effects cannot be resolved.

## Capability-preservation design cases

These require additional independently synthetic regression fixtures at
implementation time. They are explicit requirements, not claims that the small
indicator fixture exercises all production instrumentation paths.

| Case | Fixture requirement | Required outcome |
|---|---|---|
| C01 | Two receiver instances, entity-indexed model observations | No shared state/guard snapshot or merged entity identity |
| C02 | Concurrent calls on one receiver using `operation="$call"` | Different call identities; each remains stable across awaits |
| C03 | Explicit local scalar action data and distinct entity identity | Values cannot become causal identity keys or bypass held guards |
| C04 | Expression-bodied callback with review-only binding | Resolves structurally; executable point on unsupported expression fails |
| C05 | Synchronous reference-type `using` across await/return/body exception | Once-only acquisition, correct reverse disposal, cleanup only after successful disposal |
| C06 | Throwing disposer with cleanup/fault captures on the same binding | Fault is separate, original exception is propagated; no successful cleanup event |
| C07 | Await-using, value-type disposer or cleanup under source lock | Existing unsupported cases remain unsupported, not approximated |
| C08 | Scheduling hook under source lock | Instrumentation failure; no hook under recorder/source locks |
| C09 | Task/queue handoff without an explicit causal edge | Missing causal coverage remains visible; no invented global order |
| C10 | Explicit handoff added to the same workload | Recorded predecessor edge is preserved; no general liveness inference |
| C11 | Source gate/storage/publication premise selected by binding ID | Existing premise outcomes stay distinct from native proof and correspondence review |
| C12 | Omitted diagnostic branch changes observed control flow | Boundary conflict/obligation; no silent path pruning |
| C13 | Move source files or retarget an existing binding ID | Report changed location/target and reevaluate inputs; ID stability is not semantic equivalence |
| C14 | Two model bindings on one statement with only one selected for capture | Other binding remains inspectable; no source mutation or automatic cross-model composition |

## Structural controls and future test placement

Also reject duplicate IDs/options, unknown target aliases, unresolved imported
binding IDs, missing terminators, ambiguous attachments and invalid native
endpoint selectors. Do not resolve a broken target by same-name search.

At implementation time, partition executable tests by actual prerequisites:

- Core: lexical rules, configuration validation, scope selection, source
  attachment, dispositions and unavailable-capability errors that do not need a
  native frontend.
- TLA+ frontend/PlusCal: authoritative endpoint resolution and freshness.
- TLC: native finite exploration and whole-execution trace witness/rejection.
- Apalache: typing, bounded safety and complete induction obligations.
- Dafny: declaration resolution, native verification and explicit oracle cases.
- P: native declaration resolution and each actually provisioned checker claim.

A missing prerequisite or deadline is incomplete. A structural failure cannot
satisfy a native property-rejection control. A positive native result cannot
satisfy a different engine's claim. All cases retain the distinction between
native outcomes, archive consistency, current applicability and acceptance.
