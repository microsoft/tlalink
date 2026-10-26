# Apalache example

Shows Apalache's symbolic checks on a small TLA+ model. There is no C# in this
example; it focuses on the different claims Apalache can establish.

[Credit.tla](Credit/Credit.tla) models a credit counter that grows from 0 up to
`Capacity` (3) and then stays there. The project runs five checks:

| Check | Configuration | Expected result |
|---|---|---|
| Type checking | [typecheck.json](Credit/typecheck.json) | Passes: the model's types are consistent. |
| Bounded safety | [bounded.json](Credit/bounded.json) | Passes: `Safety` holds for every execution up to 5 steps. |
| Induction | [inductive.json](Credit/inductive.json) | Passes: `TypeOK` is inductive and implies `Safety`, so safety holds at any length. |
| Counterexample control | [rejected.json](Credit/rejected.json) | Rejected, as intended: credit can reach 2, which violates `TooSmall`. |
| Non-inductive control | [noninductive.json](Credit/noninductive.json) | Rejected, as intended: `WeakInvariant` excludes the reachable state where credit is 3. |

The two controls show that the checks really detect problems; a pass alone
would not show that. The project also declares that its safety obligation is
covered by the bounded and inductive checks.

## Layout

```text
Apalache/
  tlalink.project.json             root: tools and the selected component
  verification/requirements.json   what counts as acceptable evidence
  Credit/                          component, model and the five check configurations
```

## Run it

```powershell
.\TlaLink.ps1 doctor --project .\examples\Apalache\tlalink.project.json
.\TlaLink.ps1 run --project .\examples\Apalache\tlalink.project.json --scenario credit --output .\Results\apalache-credit
.\TlaLink.ps1 accept --project .\examples\Apalache\tlalink.project.json --run .\Results\apalache-credit
```

See [Apalache setup and analysis](../../REFERENCE.md#using-apalache-with-tla)
for installing Apalache and running a single check.
