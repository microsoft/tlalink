# Evidence example

Shows two different kinds of evidence about the same small function, checked
in one project:

- **C# regression tests:** [Program.cs](src/Increment/Program.cs) is a console
  harness that runs the selected tests `increment` and `limit` against
  `Increment`, which adds one but never exceeds 10. TlaLink builds it from the
  exact sources and records which binary ran.
- **A Dafny proof:** [Increment.dfy](src/Increment/verification/Increment.dfy)
  proves the same rule for every input from 0 to 10.

Both checks pass, but the project is **not accepted** at first. Passing tests
and a proof do not show that the Dafny method matches the C# code, so the
`source-correspondence` review starts pending. A person must review the
correspondence and record a decision with `record-review`.

## Layout

```text
Evidence/
  tlalink.project.json             root: tools and the selected component
  verification/requirements.json   what counts as acceptable evidence
  src/Increment/                   C# harness, project file and regression config
  src/Increment/verification/      component, Dafny proof and proof config
```

## Run it

Restoring the C# project is a separate setup step:

```powershell
dotnet restore .\examples\Evidence\src\Increment\Regression.csproj --configfile .\NuGet.Config
.\TlaLink.ps1 doctor --project .\examples\Evidence\tlalink.project.json --scenario arithmetic
.\TlaLink.ps1 run --project .\examples\Evidence\tlalink.project.json --scenario arithmetic --output .\Results\arithmetic-new
.\TlaLink.ps1 accept --project .\examples\Evidence\tlalink.project.json --run .\Results\arithmetic-new
.\TlaLink.ps1 report --project .\examples\Evidence\tlalink.project.json --run .\Results\arithmetic-new --output .\Results\arithmetic-report.md
```

The report is a readable Markdown summary of the evidence and what remains open.
