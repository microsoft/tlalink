# Code-adjacent component authoring

The [root project](tlalink.project.json) explicitly selects the
[counter component](src/Counter/verification/component.json) beside its C# source.
The component owns its model, configurations, review and safety declaration.
Shared [input sets](verification/inputs.json) and
[execution profiles](verification/profiles.json) are declared once.
Every file reference resolves against the file containing it.
The root declares `annotation_version` once for all components; binding comments
use `@link` without repeating a grammar version.
The property and update declare the same `counter::value` and `counter::step`
IDs as their formal endpoints, selected by
[scope.json](src/Counter/verification/scope.json).

From the repository root:

```powershell
.\TlaLink.ps1 expand --project .\examples\Components\tlalink.project.json
.\TlaLink.ps1 doctor --project .\examples\Components\tlalink.project.json --scenario complete
.\TlaLink.ps1 plan --project .\examples\Components\tlalink.project.json --scenario complete
.\TlaLink.ps1 inspect --annotation-version 4 --source .\examples\Components\src\Counter\Counter.cs --scope .\examples\Components\src\Counter\verification\scope.json --target counter --jar .\.tools\tla\v1.8.0\tla2tools.jar
```

Expansion needs no checker installation. Doctor, plan and execution require the
selected pinned TLA+/Java tools. A complete run includes one positive invariant
check and one expected invariant rejection. Acceptance also requires the pending
source/model correspondence review. These synthetic requirements are an example,
not a consumer acceptance policy.
The standalone inspection resolves the paired endpoints; this deliberately small
project's two execution stages run TLC, not a C# trace or refinement check.
The review must account for the serialized-call assumption: `Step` is not
thread-safe merely because the finite model's update is atomic.

`$schema` selects editor validation using the generated repository schema.
Regenerate that schema with `project-schema --output <new-schema-file>` after
changing the typed authoring contracts. Expanded project snapshots are generated
evidence, not a second user-maintained project.
