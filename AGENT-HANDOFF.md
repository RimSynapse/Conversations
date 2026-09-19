# AGENT-HANDOFF — `feature/prompt-lab`

## What this branch adds

The **Conversations contribution** to the universal Prompt Lab: the pure, Verse-free composer the
central lab (in rimworld-claude-dev-tools) links to build the exact conversation prompt without RimWorld.
The lab *console/registry itself* lives in the dev-tools repo — this branch only owns the mod's composer
(authored once, where the prompt lives). Branched off the committed HEAD of `feature/multi-speaker-sessions`.

### Part 1 — pure-composer refactor
Split the Pawn-coupled `ThinDialoguePrompt` into a pure core + a thin adapter:

- **NEW** `Source/Generation/ThinPromptComposer.cs` — pure `Compose(initName, recipName, initIdentity,
  recipIdentity, ConversationBeat, continuation)` → `ThinPrompt` (the `ThinPrompt` struct moved here).
- **NEW** `Source/Generation/IdentityComposer.cs` — pure `Identity(voiceProfile, backstoryTitle,
  traitLabels)`. Carries the **#44** logic (voiceProfile wins; else backstory + traits anchor + the
  plain-speech / "not clinical or technical" steer; else `"an ordinary colonist"`).
- **NEW** `Source/Generation/LenientDialogueParser.cs` — pure port of the game's
  `ParseLinesLenient`/`ExtractJson`/`CleanLine` (names as strings).
- **CHANGED** `Source/Generation/ThinDialoguePrompt.cs` — now a thin `Pawn` adapter delegating to `Compose`.
- **NEW** `Source.Tests/PromptComposerCases.cs` — deterministic `[SynapseTestSet]` asserting
  `Compose`/`Identity` output (framing/tone/depth rules + the #44 branches).

The dev-tools universal console `<Compile>`-links these four pure files from the workspace, so a prompt
change here changes the lab too — no drift. (The `PromptLab/` console that first lived here was moved to
dev-tools when the lab was generalized to a universal, multi-mod framework.)

## ⚠️ Merge note (read before landing)
`feature/multi-speaker-sessions` had **uncommitted** #44 identity edits to `ThinDialoguePrompt.cs`
(and heavy edits to `InteractionDialogue.cs`) in the main checkout when this branch was cut. This
refactor **converges** with that #44 work: `IdentityComposer` already implements the intended #44
identity logic, and `ThinDialoguePrompt.Build` now delegates to it. When merging, resolve
`ThinDialoguePrompt.cs`/`IdentityComposer` to the single #44 implementation here. `InteractionDialogue.cs`
was **not** touched (a later pass can single-source its `ParseLinesLenient` etc. onto `LenientDialogueParser`).

## Verify
- `dotnet build Source/RimSynapseConversations.csproj -c Release` → succeeds (mod compiles with the refactor).
- `Source.Tests` builds/runs in the full workspace (its `GamePath.props` uses workspace-relative assembly
  paths); the harness `build.ps1` builds it and the toolkit runs it in-game under `-synapse-test`.
