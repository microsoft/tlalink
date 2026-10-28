# Integrated example

Shows C# code, Dafny proofs and a TLA+ model linked through one manifest,
[bindings.json](bindings.json).

- [Decisions.cs](Decisions.cs) contains three small C# decisions: returning
  credits within a window, appending within a maximum length, and inverting
  a boolean.
- [Decisions.dfy](Decisions.dfy) contains verified Dafny versions of the same
  decisions, with contracts such as "the result never exceeds the window".
- [Credits.tla](Credits.tla) is a TLA+ model whose `Return` step is linked to
  the C# `Return` decision.

Each C# and formal declaration independently owns its common `scope::name`;
the manifest selects these IDs and lists
concrete test cases, including edge cases such as overflow and zero. TlaLink
verifies the Dafny code, compiles it to C#, and compares the results with the
real C# for those cases. This checks the selected cases only; it is not a proof
that the C# is correct for every input.

The TLA+ abstraction starts with no available credits and permits accepted
unit returns up to `Window`. It covers neither rejected nor multi-credit
returns. This example resolves that structural link; it does not execute
C# traces against this TLA+ model or prove the cross-model relation. The
manifest keeps its abstraction assumptions and outstanding obligations explicit.

## Run it

```powershell
.\TlaLink.ps1 inspect-integrated --annotation-version 4 --bindings .\examples\Integrated\bindings.json --dafny .\.tools\dafny\Dafny.exe --jar .\.tools\tla\v1.8.0\tla2tools.jar --output .\Results\integrated-review-new
.\TlaLink.ps1 verify-integrated --annotation-version 4 --bindings .\examples\Integrated\bindings.json --dafny .\.tools\dafny\Dafny.exe --jar .\.tools\tla\v1.8.0\tla2tools.jar --output .\Results\integrated-proof-new
.\TlaLink.ps1 test-integrated --annotation-version 4 --bindings .\examples\Integrated\bindings.json --dafny .\.tools\dafny\Dafny.exe --jar .\.tools\tla\v1.8.0\tla2tools.jar --output .\Results\integrated-test-new --timeout 60
```

`inspect-integrated` checks the links, `verify-integrated` runs the Dafny
verifier, and `test-integrated` runs the case comparisons.

The [Workflow example](../Workflow/README.md) reuses these comparisons as its
`credits` scenario through the component in
[verification/component.json](verification/component.json).
