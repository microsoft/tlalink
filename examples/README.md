# Examples

Small, synthetic examples that show how TlaLink connects C# code to formal
models and keeps the evidence. They are demonstrations, not acceptance
policies for your own system.

| Example | What it shows | Tools needed |
|---|---|---|
| [Demo](Demo.cs) and its [model](Demo.tla) | Linking a C# class to a TLA+ model with `@tla 2` annotations, and reviewing those links. | TLA+ tools |
| [Components](Components/README.md) | The project layout in its smallest form: a root project, checks beside the code, and shared inputs and time limits. | TLA+ tools |
| [Workflow](Workflow/README.md) | A complete project: model checking, checking a recorded C# execution against a model, Dafny comparisons and a human review gate. | TLA+ tools, Dafny |
| [Evidence](Evidence/README.md) | C# regression tests and a Dafny proof checked together, with a pending correspondence review. | Dafny, .NET SDK |
| [Apalache](Apalache/README.md) | Symbolic TLA+ checks: type checking, bounded safety, induction and expected rejections. | TLA+ tools, Apalache |
| [Integrated](Integrated/README.md) | Comparing selected C# calls with verified Dafny methods, alongside a TLA+ model. | TLA+ tools, Dafny |
| [P](P/README.md) | Linking C# to a P state machine and exploring it with PChecker. | P |

Every project example uses the same layout: a small root `tlalink.project.json`
selects `component.json` files kept beside the code they check, and a separate
`verification/requirements.json` states what counts as acceptable evidence.

Run commands from the repository root. Each checker is installed separately
and pinned in the local `.tools` cache; see the
[setup instructions](../REFERENCE.md#requirements-and-build).

## Demo

[Demo.cs](Demo.cs) is a lock-protected counter. Its annotations link the field
`active` and the two locked updates to the state and steps of the PlusCal
algorithm in [Demo.tla](Demo.tla). Review the links for one step:

```powershell
.\TlaLink.ps1 review --source .\examples\Demo.cs --model .\examples\Demo.tla --jar .\.tools\tla\v1.8.0\tla2tools.jar --step Admit
```

This checks that the annotations resolve on both sides. It does not run the
C# or prove that the model describes it correctly.
