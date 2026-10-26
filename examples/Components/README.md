# Code-adjacent component authoring

The [root project](tlalink.project.json) explicitly selects the
[counter component](src/Counter/verification/component.json) beside its C# source.
The component owns its model, configurations, review and safety declaration.
Shared [input sets](verification/inputs.json) and
[execution profiles](verification/profiles.json) are declared once.
Every file reference resolves against the file containing it.

From the repository root:

```powershell
.\TlaLink.ps1 expand --project .\examples\Components\tlalink.project.json
.\TlaLink.ps1 doctor --project .\examples\Components\tlalink.project.json --scenario complete
.\TlaLink.ps1 plan --project .\examples\Components\tlalink.project.json --scenario complete
```

Expansion needs no checker installation. Doctor, plan and execution require the
selected pinned TLA+/Java tools. A complete run includes one positive invariant
check and one expected invariant rejection. Acceptance also requires the pending
source/model correspondence review. These synthetic requirements are an example,
not a consumer acceptance policy.

`$schema` selects editor validation using the generated repository schema.
Regenerate that schema with `project-schema --output <new-schema-file>` after
changing the typed authoring contracts. Expanded project snapshots are generated
evidence, not a second user-maintained project.
