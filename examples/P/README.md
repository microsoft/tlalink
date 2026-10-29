# P example

Shows a C# method linked to a P state machine and checked with PChecker.

- [Credit.cs](Credit.cs) returns one more available credit, within a valid range.
- [Credit.p](Credit.p) is a P machine with the same rule, asserting that
  returning a credit from 0 gives 1 and from 1 gives 2.
- [ExtraCredit.p](ExtraCredit.p) is a deliberately wrong version that adds 2,
  so PChecker must find the assertion failure. It serves as a negative control.
- [credit.json](credit.json) selects PChecker, the files, the link between the
  C# method and the P function by their shared `credit-p::credit-return` ID,
  and the exploration limits.

PChecker explores a bounded number of schedules. A pass means no failure was
found within those limits, not that the model was explored exhaustively.
PChecker executes the P machine, not the C# method. The P comments' Roslyn selectors establish
structural correspondence only; matching return semantics, exceptions and
concurrent shutdown remain separate review obligations. The negative control
intentionally keeps the same ID while changing the P implementation.

## Run it

Point `$p` at the installed P compiler (see the
[P analysis workflow](../../REFERENCE.md#p-analysis-workflow)):

```powershell
$p = ".\.tools\p\.store\p\3.1.0\p\3.1.0\tools\net8.0\any\p.dll"
.\TlaLink.ps1 inspect-p --config .\examples\P\credit.json --p $p --output .\Results\p-links
.\TlaLink.ps1 check-p --config .\examples\P\credit.json --p $p --output .\Results\p-credit --timeout 60
```

These P commands obtain annotation version 4 from the explicitly selected
[scope.json](scope.json), rather than a separate CLI version option.

To try the same checks as a project, including the `ExtraCredit` control,
generate one in a new directory:

```powershell
.\examples\P\Initialize-Project.ps1 -PCompiler $p -Output .\Results\p-project
.\TlaLink.ps1 run --project .\Results\p-project\project.json --scenario credit --output .\Results\p-project-run
.\TlaLink.ps1 accept --project .\Results\p-project\project.json --run .\Results\p-project-run
```

The project is generated rather than kept in this folder because it pins the
exact P compiler you installed.
