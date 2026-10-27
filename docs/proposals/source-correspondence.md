# Source correspondence vocabulary

Status: implemented annotation version 3. Payloads are standard JSON in C#
block comments, parsed with `System.Text.Json` into strict typed records.
Version-2 source directives are not accepted as a parallel convention.

The [synthetic conformance cases](source-correspondence-cases.md) define intended
behavior and negative controls. The matrix is not itself an execution result;
the console suites contain executable regression/native controls. The
[reference](../../REFERENCE.md#one-maintained-markup-link) documents commands.

## Purpose and decisions

Keep intended correspondence visible in maintained C# without requiring a
separately annotated source copy for each model. Support TLA+, Dafny and P
without treating their declarations, execution adapters or successful native
outcomes as interchangeable.

Choose one authoring vocabulary:

| Directive | Meaning |
|---|---|
| `bind` | An intended relationship between one C# endpoint and one native formal endpoint |
| `capture` | Execution observation metadata for a supported binding |
| `exclude` | An explicit source-region omission from selected correspondence scopes |

Use explicit formal target aliases, not an ambient model namespace or active
view. A source location may have several independent bindings. Component
configuration selects inputs, analyses, assumptions and checks; it does not
repeat source selectors for inline bindings.

The same scope mechanism supports shared, symbol-resolved logging omission
rules. Omission is not proof of noninterference, an automatic implementation
stutter, a hidden model action or an acceptance exemption.

### Ownership

| Fact | Maintained owner |
|---|---|
| Source endpoint and native endpoint reference | Inline `bind` |
| Source-local qualification | Inline `reason`, with longer arguments in review inputs |
| Capture expressions, guards and observation point | Inline `capture` |
| Formal target alias, language and complete native input context | Component configuration |
| C# compiler context and exact selected sources/references/defines | Explicit workflow inputs |
| Native checker mode, configuration, bounds, properties and tools | Backend-specific configuration |
| Oracle adapters/cases, source premises and correspondence arguments | Backend-specific configuration referencing binding IDs |
| Routine logging omission policy | Component scope rules with exact C# declaration IDs |
| Exceptional region omission | Inline `exclude` |
| Negative-control mutations | Explicit control definitions applied only to copies |
| Resolved index, instrumented sources and execution records | Generated artifacts |
| Review and acceptance decisions | Independent append-only records |

No maintained binding is duplicated in a preparation recipe or manifest. Keeping
adapter cases outside C# is intentional: they describe an experiment, not the
location of the source/model relationship.

## Lexical and identity rules

Use C# block comments beginning with `@link` followed by a JSON object with
optional `bind`, `capture` and `exclude` arrays. Every entry is a typed record:
`BindAnnotation`, `CaptureAnnotation` or `ExcludeAnnotation`. Native references
are typed alias/endpoint pairs with a JSON string converter. Capture points,
classifications, scalar types and scheduling hooks use enums.

Use the repository's shared stable-ID rules, case-sensitive property names,
string expression values and actual JSON arrays for argument/observation/target
lists. Unknown fields, duplicate properties, malformed JSON, null entries,
invalid enums and empty required values fail. There is no custom tokenizer,
XML alternative or raw-JSON binding store. The existing TLC adapter receives a
derived internal projection, not a second maintained annotation format.

Binding IDs, exclusion IDs and rule IDs share one unique disposition-ID
namespace per component. Target aliases have their own namespace. All are
ordinal case-sensitive and are not filepaths. An ID cannot contain `::`.

IDs are local to the explicit correspondence/source scope. Component imports
and shared input sets select declaring-relative scope documents; they do not
implicitly import bindings into another compiler scope. Cross-component
qualified binding references are not supported. No directory discovery, name
matching, inheritance or overrides supply missing definitions.

The annotation version remains selected once at the project root and explicitly
for standalone commands. Grammar 3 does not reinterpret version 2 or silently
accept both conventions. Annotation payloads are not standalone JSON manifests:
their grammar context is selected by the project. JSON manifest schema versions
remain a separate concern.

## Formal targets

A component references one schema-4 `correspondence-scope` document, declaring
`annotation_version: 3`, `targets` and `omissions`. A target has these fields:

| Field | Requirement |
|---|---|
| `id` | Unique scope-local alias |
| `language` | Exactly `tla`, `dafny` or `p` |
| `inputs` | Explicit native files, with declaring-relative paths; one root for TLA+ |
| `required_bindings` | Explicit required inline IDs for TLA+; Dafny/P select IDs in their backend configuration |
| `hidden_actions` | Optional TLA+ endpoint/reason records for explicit hidden transitions |

Tools remain selected and pinned by the existing workflow mechanisms.
Executable configurations reference aliases rather than creating duplicate
model definitions. Imported modules/files retain explicit dependency pinning.

`"target": "alias::native-endpoint"` splits at the first `::`. The remainder is
resolved by the native frontend, never as a path or a C# expression:

- TLA+: an operator in the selected SANY-resolved module context. A qualified
  imported operator requires unambiguous native resolution.
- PlusCal: an authoritative label endpoint from the selected root's frontend
  and freshness-checked translation. PlusCal is a TLA+ frontend, not another
  interchangeable backend or a hand-parsed label format.
- Dafny: a fully qualified resolved declaration, including ghost status and
  native signature information.
- P: a qualified declaration exposed by the authoritative frontend, such as a
  machine, state, event or function. Do not invent endpoints absent from its
  inventory.

Missing/ambiguous declarations are failures; unavailable frontend prerequisites
are incomplete. If native qualification cannot uniquely identify a declaration,
require a frontend-supported explicit selector before accepting the binding.

The selected engine is not part of the alias language: TLC and Apalache can
analyze the same TLA+ inputs but establish different claims. PlusCal translation
is explicit setup; analysis does not silently translate or edit generated bodies.

## `bind`

```csharp
/* @link {
  "bind": [
    {
      "id": "lamp-change",
      "target": "lamp-tla::Set",
      "reason": "The assignment is under the instance gate."
    }
  ]
} */
enabled = requested;
```

Each `bind` entry requires `id` and `target`; `reason` is optional.

A binding attaches to the immediately following supported C# declaration,
statement or expression, allowing only intervening trivia. Roslyn determines
the node and enclosing symbol using the explicit compiler context. Unsupported
or ambiguous attachment is an error; expression bindings must not be silently
promoted to their enclosing statements.

One binding identifies one source endpoint and one formal endpoint. Multiple
`bind` directives in a comment attach to the same node. Several source nodes
may bind to the same formal declaration using distinct IDs. Neither case
asserts model equivalence, grouping, atomicity or refinement.

The common relation is deliberately modest: "this is the authored
correspondence." The resolved index exposes both endpoint kinds. Backend-specific
operations validate allowed pairs and metadata; not every structurally resolved
pair is executable.

Source invocation expressions, including expression-bodied callbacks, remain
eligible for review-only bindings. Automatic capture does not thereby gain
support for those expressions.

Formal files need no duplicate source binding IDs. Native declarations and
authoritative label identities are the endpoint identities; a native rename
requires an explicit binding update. Existing native typing annotations remain
independent. Model-only visibility and external actions stay in the selected
backend configuration, with their reasons and assumptions.

## `capture`

```csharp
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
```

```csharp
/* @link {
  "bind": [
    {
      "id": "lamp-change",
      "target": "lamp-tla::Set"
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
```

Each `capture` entry has a `binding` reference at the same attached source
endpoint. Resolve the
binding before interpreting capture options. For TLA+:

- A state capture requires a supported scalar field and compatible resolved
  observation operator, `type`, and a supported actually held guard when read.
- An action capture requires a supported source point, `point`,
  `classification`, `actor` and `operation`. `observe` references state bindings
  for the same target alias. State and action binding IDs are not raw C# names.
- State properties are `guard`, `type` and `units`. Action properties are `point`,
  `classification`, `actor`, `operation`, `connection`, `entity`, `arguments`,
  `observe`, `guard`, `reason` and `hook`. These split existing state/step
  capabilities; they are not permission to accept unknown combinations.
- Multiple action captures on one binding are allowed only at distinct
  supported points, such as `cleanup` and `fault`. Duplicate points conflict.
  Their observation/capture identities remain distinct in generated evidence.

At initial implementation, only the existing TLA+/TLC execution adapter
supports these capture forms. Apalache does not validate recorded C# traces.
Dafny uses explicit oracle adapters/cases, and P uses its native execution or
monitoring configuration. An unsupported capture request is rejected, not
downgraded to a structural pass.

### Capabilities that must survive the new syntax

These are migration requirements, not requests to broaden current support:

| Capability | Required preservation |
|---|---|
| Multiple implementation objects | Separate entity identities and entity-indexed observations |
| Concurrent invocations on one object | Independent operation identities; receiver identity is not invocation identity |
| Async operations | `operation="$call"` remains stable across awaits, unique per invocation |
| Data-bearing actions | Typed scalar arguments stay distinct from actor/operation/entity identities |
| Shared state | Actually held guards, interference checks and no invented cross-lock snapshots |
| API/callback review | Invocation-expression bindings do not imply executable capture support |
| Completion/failure | Preserve `invoke`, `commit`, `complete`, `fault` and `cleanup` distinctions |
| Resource lifetime | Successful implicit disposal is not just leaving a scope; preserve exceptions and reverse cleanup |
| Control transfers | Preserve statement/local/return/throw support and existing restrictions |
| Scheduling | Hooks remain separate from capture and forbidden under source locks |
| Causal edges | No automatic Task/queue/caller-callee handoffs or global timestamp ordering |
| Source premises | Existing guarded-effect, storage, gate and publication obligations reference binding IDs without becoming `proves` annotations |

Keep the current restricted scalar/identity expressions, action arity and
argument checks. Do not use binding selection to relax them. Reference identity
must not invoke user equality/hash code. Argument values must not create causal
edges. Unresolved classification blocks instrumentation; stutter preserves all
model state, and hidden model transitions are a different mechanism.

When bindings from several aliases attach to one statement, selecting one
changes only generated observation selection, not source execution. Initially,
one instrumented execution selects one target alias and its explicit binding
set. Generated call identities are shared within the enclosing invocation, not
independently allocated for each observation.

## `exclude`

```csharp
/* @link {
  "exclude": [
    {
      "id": "lamp-diagnostics",
      "from": [
        "lamp-tla",
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
```

Each `exclude` entry requires `id`, `from` and `reason`. `from` is a JSON array of
explicit target aliases. Duplicate entries, wildcards and unknown aliases fail.

Attachment is the immediately following declaration, statement or block.
The disposition covers that syntax subtree, including nested function bodies;
the generated inventory must enumerate its extent rather than hide nested code.
No paired start/end markers, file globs or line-number ranges.

An exclusion and binding for the same target anywhere inside its region
conflict. Exclusions from different targets can coexist. Overlapping explicit
exclusions for the same target fail rather than introduce precedence.

Exclusion changes correspondence scope only. It never:

- Removes source from compilation or runtime execution.
- Removes input hashes, compiler dependencies or retention edges.
- Suppresses invalid annotations, compiler errors or selected native failures.
- Proves omitted effects irrelevant to modeled state or control flow.
- Automatically satisfies coverage, source premises or acceptance requirements.

Argument evaluation, calls, state accesses, awaits, cleanup and external effects
remain visible to source/boundary inspection. Known modeled-state interference
is a conflict. Unresolved effects remain boundary obligations; execution or
acceptance that requires their resolution must block or report incomplete.
An external component contract is a separate reviewed input, not an exclusion.

## Routine omissions, especially logging

Do not require an annotation at every routine logging call. Components can own
shared scope rules with these JSON fields:

| Field | Requirement |
|---|---|
| `id` | Stable disposition ID |
| `targets` | Explicit aliases, never "all present and future targets" |
| `calls` | Exact Roslyn method declaration IDs with overloads distinguished |
| `reason` | Nonempty explanation of the omitted behavior |
| `assumptions` | Nonempty authored boundary assumptions; approval remains a separate review |

All such rules have the fixed treatment "omit invocation from correspondence";
there is no arbitrary treatment string or source-rewriting option.

Match resolved symbols, using original declaration identities for constructed
generic methods. Do not match text such as `Log*`, assembly wildcards, arbitrary
overrides, unresolved dynamic calls or transitive wrappers. Interface/virtual
dispatch does not establish concrete-callee effects; retain that boundary.

Initial rules apply to synchronous, void invocation statements. Return-valued
logging scopes, `await` expressions and calls embedded in other expressions need
an explicit region/boundary decision; do not erase value dependencies or Task
edges.

For `Diagnostics.Write(Compute())`, the rule omits the diagnostic callee behavior
from the selected model correspondence. It does not omit receiver or argument
evaluation, `Compute()` or its exceptions/effects. It does not remove the call
from execution. Logger blocking, exceptions, callbacks and internal locking are
not proven harmless; any reliance on their absence is an explicit assumption.

Matching rules cannot suppress source/compiler/native errors. Calls that
modify observed state conflict with the selected boundary. Rule overlap for the
same call/target is an error. An inline exclusion supplies the disposition for
its region, so no additional per-call rule disposition is emitted there. A
binding/capture at a rule-matched invocation conflicts with that rule for the
same target; there is no silent opt-out.

Report concrete matched sites and assumptions, not just rule names. An empty
match set is visible. Changes to a rule, matching callee or omitted code remain
fingerprinted and require applicability/review reevaluation.

## Selection, results and evidence

Stages explicitly select target aliases and bindings. No fallback target,
implicit active view, name-based mapping, inheritance or override chain.
An ID reference never creates an import or expands a source scope.

Component-wide inspection checks all declarations under their explicit compiler
contexts. Selected analysis checks the selected closure, and must not present
that result as a whole-component assessment. Malformed markup in a selected
source file is still an error, even if its binding is not selected.

The generated index must include:

- Binding ID, source span/symbol, alias, native declaration/kind/signature.
- Capture capability and independent metadata/guard validation.
- Excluded and rule-omitted regions with exact extent, reasons and assumptions.
- Undisposed regions and unresolved boundaries, including competing accesses.
- Selected/unselected scope and independent structural/native/coverage outcomes.

Do not calculate validated-code coverage by counting binds or subtracting
exclusions. A linked declaration is not its whole implementation's proof.

Native outcomes, archive consistency, current applicability, correspondence
review and acceptance remain distinct. All authored inputs, including omitted
regions and scope rules, retain evidence fingerprints. Binding ID stability does
not make changed code equivalent or old evidence current. Preserve existing
completed native-result reuse only when that engine's complete existing reuse
key and adapter semantics are unchanged.

## Migration rules

For maintained successor inputs:

1. Review the [cases](source-correspondence-cases.md), including capability
   preservation and omitted-call controls.
2. Use schema-4 correspondence scopes and backend-specific configurations;
   do not maintain a parallel endpoint registry.
3. Use annotation version 3 across parser, resolvers, project
   expansion, review, instrumentation, native adapters, evidence readers,
   schemas, maintained examples and tests together.
4. Move owned-source bindings inline, removing annotation-insertion recipes.
   Keep preparation for immutable/external inputs and explicit copied controls.
5. Keep original evidence immutable. Introduce intentional successor inputs;
   never refresh policy/configuration pins simply to clear failures.

This design adds no provisioning, effect prover, automatic composition theorem,
generic native execution API or permission to edit generated PlusCal bodies.
