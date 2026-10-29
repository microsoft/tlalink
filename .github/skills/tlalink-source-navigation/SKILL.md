---
name: tlalink-source-navigation
description: 'Find C# symbols and Roslyn declaration IDs with TlaLink; resolve source positions, inspect member code, find references, callers and callees, or obtain compiler-backed source facts. Use before selecting correspondence attachments or following calls. Read-only navigation, not link editing or a whole-program effect analysis.'
---

# Navigate C# with compiler-backed queries

Use the existing CLI rather than guessing declaration IDs or scanning source
spellings for semantic references. See [Finding C# symbols](../../../REFERENCE.md#finding-c-symbols)
for the authoritative command contract and [agent safeguards](../../../AGENTS.md).
For model/C# comparison use [correspondence inspection](../tlalink-inspect-correspondence/SKILL.md);
for requested connection edits use [link maintenance](../tlalink-maintain-links/SKILL.md).

## Establish the compilation first

1. Read the selected package's instructions and identify the concrete behavior
   and source members of interest. Keep implementation sources explicit.
2. Supply the actual references, defines, C# dialect, nullable and framework
   settings. Do not silently use host defaults for a different product context.
3. Prefer `--regression-result <native-engine-result.json>` when a suitable
   recorded compiler context exists. For project regression stages use the
   receipt under `<stage>/engine/result.json`, not the enclosing stage receipt.
   Keep selected sources explicit; every selected file must have been observed
   by that compiler. Do not combine this option with context overrides or
   reconstruct its reference list in scripts.
4. Report missing context or compiler diagnostics. Do not turn incomplete
   resolution into plausible partial answers. Analysis must not provision tools.

The following independently synthetic examples run from the repository root.
Use document-relative inputs from the selected package, resolved to CLI paths
against the caller's directory; do not assume the tool changes that directory.

## Choose the smallest query

| Question | Query |
|---|---|
| What IDs correspond to this name? | `symbols find --name <name>`; inspect all overloads |
| What declaration does this exact ID identify? | `symbols show --id <emitted-ID>` |
| What declaration contains this source position? | `symbols resolve --file <selected-file> --line <line> --column <column>` |
| Where is this symbol used in selected sources? | `symbols references --id <emitted-ID>` |
| Which explicit calls target this method? | `symbols callers --id <emitted-ID>` |
| What does this source member explicitly call? | `symbols callees --id <emitted-ID>` |
| Can I read the member and its structural facts together? | `symbols describe --id <emitted-ID>` |

```powershell
.\TlaLink.ps1 symbols find --source .\examples\MethodSummaries\src\PermitGate.cs --name TryTake --format summary
.\TlaLink.ps1 symbols describe --source .\examples\MethodSummaries\src\PermitGate.cs --id 'M:SummaryExample.PermitGate.TryTake' --format summary
.\TlaLink.ps1 symbols callees --source .\examples\MethodSummaries\src\PermitGate.cs --id 'M:SummaryExample.PermitGate.TryTake' --format summary
```

Start with `summary`: it retains symbol matches and utility-query details
without the full fingerprint map. Request full JSON or a new `--output` file
only when the complete pinned result is needed. Symbol commands do not need
`--annotation-version`; annotation-aware `inspect-source` does.

## Follow evidence, not names

- Copy returned declaration IDs. Do not synthesize overload/generic IDs.
  In PowerShell, single-quote IDs containing backticks or pass emitted IDs as
  variables. Renames and signature changes can change IDs.
- `find` returns candidates, not an automatic choice. `resolve` identifies a
  containing declaration, not the symbol referenced at the cursor. Positions
  are physical one-based lines and UTF-16 columns; multiple sources require
  `--file`. Do not use nearest-line guesses to clear ambiguity.
- Follow emitted call targets with `show`/`describe` when a selected source body
  exists. `references`/`callers` can also target metadata IDs but still search
  only selected source; they do not scan framework bodies.
- Use [source inventory](../../../REFERENCE.md#current-c-source-inventory)
  when the question needs member-wide access/await/cleanup facts or CFG flow,
  rather than replacing Roslyn analysis with regexes.

## Interpret and report

Exit `0` means discovery/resolution succeeded, `1` means not found or an
ambiguous position, and `2` means invalid input or incomplete compiler context.
A successfully resolved target with zero scoped references is not unused
globally. State the source selection, compiler context, IDs and unresolved
questions in the answer.

Calls retain declared dispatch, not concrete interface/virtual/delegate or
reflection behavior. Method groups, `nameof` and crefs are not call edges.
Implicit accessors, disposal, awaiters and operator/runtime calls need separate
review. Nested-function sites are marked, not assumed executed. Follow effects
beyond selected sources when the claim needs them, while respecting explicit
interface contracts. Resolved IDs and structural facts do not prove call effects,
atomicity, concurrency correctness or source/model equivalence.
