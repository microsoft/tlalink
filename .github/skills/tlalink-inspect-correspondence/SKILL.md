---
name: tlalink-inspect-correspondence
description: 'Inspect TlaLink source/model correspondence by inlining TLA+/PlusCal, Dafny or P declarations with resolved C# code. Use for reviewing bindings, guards, captures, cleanup, unresolved attachments or targeted agent responses; filter and page links inspect output instead of post-processing large JSON. Read-only inspection, not editing or proof approval.'
---

# Inspect correspondence without editing it

Use `links inspect`, not manual source/model pairing. The CLI uses authoritative
native spans and the shared Roslyn selector resolver. Read
[Inspecting correspondence](../../../REFERENCE.md#inspecting-correspondence)
for exact options and [agent safeguards](../../../AGENTS.md).
Use [source navigation](../tlalink-source-navigation/SKILL.md) to discover IDs;
switch to [link maintenance](../tlalink-maintain-links/SKILL.md) only for requested edits.

## Establish scope

1. Select the correspondence scope and one explicit target alias. Read its
   package instructions, assumptions and compiler prerequisites.
2. Supply `--annotation-version 4`, explicit C# sources and sufficient compiler
   context. Reuse an applicable native `--regression-result` when available,
   retaining explicit source selection and no context overrides.
3. Select only the required pinned frontend: TLA+/PlusCal `--jar`, Dafny
   `--dafny`, or P `--p`. Missing prerequisites are incomplete, not permission
   to install during analysis or substitute another engine.
4. Use `links list` for the native binding inventory if IDs are unknown. Do not
   infer pairs from matching source/model names.

## Start narrow, then request the missing context

This synthetic recipe runs from the repository root. Keep the common arguments
and command in the same shell invocation if the agent's tool starts fresh shells.

```powershell
$common = @(
    '--annotation-version', '4',
    '--scope', '.\examples\Components\src\Counter\verification\scope.json',
    '--target', 'counter',
    '--source', '.\examples\Components\src\Counter\Counter.cs',
    '--jar', '.\.tools\tla\v1.8.0\tla2tools.jar'
)
.\TlaLink.ps1 links inspect @common --binding 'counter::step' --section model --section source --format text
if ($LASTEXITCODE -ne 0) { throw 'Inspection did not resolve; inspect the emitted diagnostics.' }
```

The Counter example is review-only: it has no executable capture point.
Use `--capture-point none` when narrowing this example; `commit` legitimately
matches nothing. Use a capture-enabled example such as Demo to inspect
executable points, with that example's own explicit scope/source/tool inputs.

Reuse the same explicit context when refining the query:

| Need | Options to add or replace |
|---|---|
| Several related actions | Repeat `--binding`; or repeat `--endpoint` for native names, not both |
| All connections for one C# member | `--symbol <emitted-Roslyn-ID>` |
| One implementation file | `--source-file <exact-path>` |
| Statement rather than an enclosing region | `--syntax ExpressionStatement` or another exact Roslyn kind |
| Native declarations of one kind | `--kind <native-kind>`; use the backend's emitted spelling |
| Unresolved attachments | `--status unresolved` |
| Unlinked support declarations | `--status unlinked`; do not also hide unlinked declarations |
| Successful observation points | `--capture-point commit` |
| Throwing disposer/statement observations | `--capture-point fault` |
| Successful supported implicit disposal | `--capture-point cleanup` |
| Review-only/state metadata | `--capture-point none` |
| Literal name, ID or selected-code fragment | `--search <text>`; not regex or surrounding-context search |
| Earlier guard checks or sibling statements | `--section context --context block` |
| Whole member, handlers and cleanup | `--section context --context member` |
| Explicit call IDs and available source-definition locations | `--section calls`; this requests call resolution automatically |
| Only selected output blocks | Repeat `--section model\|source\|context\|captures\|observations\|calls` |
| Smaller replies | `--include-unlinked false --limit 5`; continue with the returned `next_offset` |
| Metadata/pin details | `--view full --format json`; default compact omits fingerprints |

Replace section selections rather than blindly appending incompatible options.
`--section calls` conflicts with `--calls false`; `--calls true` conflicts with
explicit sections excluding calls. To retain both code and context, request
both `source` and `context`. Sections affect presentation only: search still
uses its documented fields even when those fields are omitted from the reply.

Repeated values within a filter are OR; different filters are AND. Member,
syntax, file and capture filters must match the same binding. Capture-point
filters select bindings and retain their other capture entries. Different
binding IDs may intentionally observe the same C# statement at different points;
do not collapse those model actions as duplicate source locations.

## Read the result before concluding

- Check diagnostics and exit code first: `0` resolved structurally, `1`
  attachment/required-binding failures, `2` input/prerequisite failure.
  Filtering, paging and section selection never suppress diagnostics or turn
  failure into success.
- Read candidate/matched/returned counts, query sections, omitted counts and
  continuation offset. Zero matches or a page beyond the result set says
  nothing about coverage. Inputs must stay stable between pages.
- Check the exact selected syntax, not just marked lines. Positional selectors
  cannot detect equal-count replacement/reordering. A resolved attachment may
  still be semantically wrong.
- Distinguish condition evaluation from entering a branch. Lexical locks and
  conditions are not computed path conditions; outer callback context does not
  establish invocation-time locking. Expand to block/member context as needed.
- Inspect observed field declarations and declared guards with the
  `observations` section. Neither a declared guard nor a visible lock proves all
  accesses use it.
- `commit`/`complete` on selected syntax is not necessarily whole-method or
  returned-Task completion. Cleanup/fault observations are distinct from resource
  return attempts. Inspect implicit disposal, external effects, awaits and
  uncaptured causal edges separately.
- Native delegations are shown as authored, not recursively interpreted.
  Explicit calls retain declared dispatch; implicit and transitive effects are
  outside the call list.

## Return a focused review

State the selected binding/member, structural result, relevant source/model
behavior, selector risks and outstanding semantic questions. Keep native checker
outcomes, current applicability, acceptance and human review separate.

Use `--format markdown --output <new-file>` for a requested reusable report,
or JSON for structured consumption. Reports can contain source; keep private
reports inside the private package. Never insert copied C# into maintained
models, treat an inspection as an acceptance receipt, claim general refinement,
or record an agent as the required human reviewer.
