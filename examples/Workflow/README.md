# Workflow example

A complete project with three scenarios. Together they show model checking,
checking a real C# execution against a model, comparing C# with verified
Dafny, and a review that a person must record before acceptance.

| Scenario | Checks | Expected result |
|---|---|---|
| `lanes` | TLC explores the [Lanes model](src/Lanes/verification/Lanes.tla), then an instrumented copy of [Lanes.cs](src/Lanes/Lanes.cs) runs and TLC checks the recorded execution against the model. | Both pass and are accepted. |
| `credits` | Selected C# calls in the [Integrated example](../Integrated/README.md) are compared with verified Dafny methods. | All required cases pass and are accepted. |
| `cancellation` | [Cancellation.cs](src/Cancellation/Cancellation.cs) is compared with a Dafny method, and a deliberately wrong Dafny proof must be rejected. | Checks complete as expected, but acceptance waits for the `cleanup-adapter` review. |

## What the code does

- **Lanes:** two threads each move a lane from 0 to 1, the second after the
  first, with the handoff between them recorded explicitly. The model's
  `SecondNeedsFirst` invariant states that lane 2 never moves before lane 1,
  and the annotations tell TlaLink where each move happens in the C#.
- **Cancellation:** `Process` always runs its cleanup in a `finally` block,
  even when cancellation throws. The adapter `Observe` records whether
  cancellation happened and whether cleanup ran. [Rejected.dfy](src/Cancellation/verification/Rejected.dfy)
  is a negative control: its proof is wrong, so Dafny must reject it.

## Layout

```text
Workflow/
  tlalink.project.json             root: tools, scenarios and imported components
  verification/requirements.json   what counts as acceptable evidence
  src/Lanes/                       C# code, with its model and component in verification/
  src/Cancellation/                C# code, with its Dafny proofs and component in verification/
```

The `credits` component lives beside its code in
[Integrated/verification](../Integrated/verification/component.json).

## Run it

```powershell
.\TlaLink.ps1 doctor --project .\examples\Workflow\tlalink.project.json --scenario lanes
.\TlaLink.ps1 plan --project .\examples\Workflow\tlalink.project.json --scenario lanes
.\TlaLink.ps1 run --project .\examples\Workflow\tlalink.project.json --scenario lanes --output .\Results\lanes-new
.\TlaLink.ps1 accept --project .\examples\Workflow\tlalink.project.json --run .\Results\lanes-new
```

Use `--scenario credits` or `--scenario cancellation` for the other scenarios.
The `cancellation` scenario stays unaccepted until a person reviews the adapter
and records a decision with `record-review`; see the
[project workflow](../../REFERENCE.md#project-workflow).
