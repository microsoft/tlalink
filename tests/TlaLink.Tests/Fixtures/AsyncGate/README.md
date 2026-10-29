# Async gate source-obligation fixtures

These independently synthetic samples test source structure, not the
implementation semantics of the stub gate or the truth of a C# refinement
claim. The Dafny lemma is a native-pipeline control, not a proof of the C#.

Core and native tests share `AdmissionGate.cs`, `Work.cs`, `Claim.dfy`,
`scope.json` and `bindings.json`. `cases.json` names the expected structural
outcome of each complete source sample under `Cases`. Alternate gate contracts
live under `Gates`. Tests copy the selected source to `Service.cs` in a fresh
work directory; they never modify the maintained samples.

Sample C# is excluded from compiling the test runner and copied beside its
binary as content. Keep samples C# 8-compatible. Add a named source file for
each defect control instead of constructing it through string replacement.
