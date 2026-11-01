---
name: tlalink-inspect-correspondence
description: 'Inspect TlaLink source/model correspondence with inline TLA+/PlusCal, Dafny or P and resolved C#. Use for bindings, guards, captures, multi-step ownership/cleanup review, grouped member context, bounded callee source, unresolved attachments or targeted responses; filter and page links inspect rather than post-process large JSON. Read-only, not editing or proof approval.'
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

Use the package's selected wrapper/distribution, not an unrelated executable
from PATH or the workspace build. A package can pin a tool older than this
skill: a usage error rejecting `links inspect` is a capability/setup gap, not
an unresolved source/model attachment. Pause analysis and use its documented
distribution-selection setup with user authorization; then repeat preflight.
Never silently bypass the wrapper or switch formal engines. Updating the tool
can make recorded compiler evidence or review support stale. Obtain fresh
execution evidence where required, and keep reviews pending until separately
revisited rather than renewing them merely to clear drift.

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
| Related attachments in source order, shared context once | `--group-by member --context member --section context` plus needed model/source sections |
| Explicit call IDs and available source-definition locations | `--section calls`; this requests call resolution automatically |
| One-level selected-source callee excerpts, deduplicated | `--section calls --callee-source true --callee-limit 5 --callee-max-lines 80` |
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

## Multi-step ownership and cleanup review

1. Discover the real member ID, then start with a member filter and
   `--group-by member --context member --section model --section source
   --section context --section captures`. The shared map orders attachments
   physically and keeps capture points distinct; it is not an execution trace.
2. Add `--section calls --callee-source true --callee-limit 5
   --callee-max-lines 80` if understanding a selected call requires another
   source fetch. Callee source implies call resolution; do not combine it with
   `--calls false` or sections excluding calls.
3. Read the deduplicated `callee_sources` entries referenced by each call's
   `callee_keys`. They are one-level declarations in the selected compilation,
   not a recursively expanded program. Calls are collected from the selected
   syntax, not automatically from the surrounding context excerpt.
4. Inspect `budget-omitted`, `truncated`, `declaration-only`,
   `no-selected-source` and `no-declaration-id` statuses. Omitted cleanup can
   invalidate a conclusion. Use `symbols describe` on an emitted ID for the
   next justified step rather than blindly raising every budget or recursively
   following all calls. Partial method declarations/implementation parts can
   have separate catalogue entries.
5. Account for acquisitions, ownership transfers, normal/exception exits and
   release attempts versus successful effects in the authored review. Grouping
   and body availability do not infer those semantics. Follow relevant implicit
   disposal/getters and callback/await behavior separately.

Paging remains endpoint-based: a member group may contain only part of its
matching attachments, and member-wide filtering is not whole-member coverage.
Unavailable bodies are a review boundary, not permission to invent their
effects. Even when concrete source is present, do not cross a declared
interface/environment boundary or treat a virtual body as the runtime target.
These options never introduce fault injection, scheduling hooks or execution.

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
- For a reviewed method summary, also read the selected scope's
  `method_summaries` entry: its abstraction, precondition, assumptions, limits,
  dependencies and inspection obligation are not established by the inline
  relation/source excerpt. Follow competing writers and real caller storage.
  Keep publication, action dispatch, settlement, cleanup and completion
  distinct; a resolved whole-member attachment does not make the method atomic.

## Return a focused review

State the selected binding/member, structural result, relevant source/model
behavior, selector risks and outstanding semantic questions. Keep native checker
outcomes, current applicability, acceptance and human review separate.

Use `--format markdown --output <new-file>` for a requested reusable report,
or JSON for structured consumption. Reports can contain source; keep private
reports inside the private package. Never insert copied C# into maintained
models, treat an inspection as an acceptance receipt, claim general refinement,
or record an agent as the required human reviewer.
