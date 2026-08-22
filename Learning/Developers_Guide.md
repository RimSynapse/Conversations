# Developer's Guide — RimSynapse Conversations

A reference for mod developers who want to hook into, extend, or read from **RimSynapse - Conversations**. Everything below is documented against the code on the current branch (v0.9.0), not against intentions or roadmap notes. Where a member is `internal` or `private`, it is called out as such so you know whether you can reach it without reflection.

Conversations is a consumer of **RimSynapse Core**: it never talks to an LLM provider directly. It asks Core to run prompts (`SynapseClient`), registers its game tools on Core's registry (`SynapseToolRegistry`), reads per-pawn state off Core's comp (`SynapseCorePawnComp`), and yields to Core's backpressure signals when the shared pipeline is behind. See [Core Dependencies](#core-dependencies) for that surface.

The two extension points a content modder will reach for most are **[ChatTopicDef](#adding-conversation-topics-chattopicdef)** (pure XML, no code) and the **[context-key system](#context-keys)** (a small C# switch). The rest of this guide documents the generation pipeline, the warden system, the Harmony surface, and the registered game tools for deeper integration.

Namespaces you will see:
- `RimSynapse.Conversations` — defs, context resolver, world component
- `RimSynapse.Conversations.Generation` — the v0.9 agent-first pipeline
- `RimSynapse.Conversations.Warden` — warden (prisoner/slave) conversations
- `RimSynapse.Conversations.Patches` — Harmony patches and dialogue application
- `RimSynapse.Conversations.API` — registered game tools
- `RimSynapse.Conversations.UI` — debug actions

---

## Registered Game Tools

Conversations registers its tools with Core's tool-calling engine at startup via `ConversationMcpTools.RegisterTools()` (`Source/API/ConversationMcpTools.cs`). Each is a `SynapseToolRegistry.RegisterTool(name, description, schema, handler)` call. Handlers take a JSON argument string and return a JSON string. They are reachable by the LLM during a conversation and headlessly through `SynapseToolRegistry.ExecuteTool(name, argsJson, allowMutating)`.

Five tools mutate world state and are flagged via `SynapseToolRegistry.MarkMutating(...)`; a gated or autonomous run with `allowMutating: false` will refuse those and run the read-only ones freely.

### Read-only tools

| Tool | Args | Purpose / return shape |
|------|------|------------------------|
| `get_chat_history` | `pawnName` (string, required), `maxMessages` (number, optional, default 20) | Recent pawn-to-pawn messages for that colonist, newest-first, pulled from `SynapseConversationsWorldComponent.pawnConversations`. Returns `{ pawn, count, messages:[{speaker, text, tick}] }`. The player↔storyteller log is not included (it lives in Core). |
| `get_colonist_interests` | `pawnName` (string, required) | Conversational interests derived from Minor/Major-passion skills plus traits. Returns `{ pawn, interests:[{topic, source}] }` where `source` is `"passion:Minor"`, `"passion:Major"`, or `"trait"`. Empty list if no passions/traits. |
| `get_royal_demands` | `pawnName` (string, required) | Noble titles, demands, active decrees, privileges (Royalty). |
| `get_faith_precepts` | `pawnName` (string, required) | Active ideoligion, role, social precepts, relics (Ideology). |
| `get_xenotype_identity` | `pawnName` (string, required) | Genes, xenotype classification, age group (Biotech). |
| `get_mechanitor_status` | `pawnName` (string, required) | Bandwidth, complexity, controlled mechs (Biotech). |
| `get_void_melancholy` | `pawnName` (string, required) | Monolith study level, void obsession progress, void mental states (Anomaly). |
| `get_orbital_hazards` | *(none)* | Space biome coordinates, ship-systems damage, orbital travel hazards (SOS2/Odyssey). |

### Mutating tools (flagged via `MarkMutating`)

| Tool | Args | Purpose |
|------|------|---------|
| `trigger_mood_booster` | `pawnName`, `effectType` (`"boost"`\|`"penalty"`), `reason` — all required | Applies a balanced temporary thought buff/debuff. Limit once per 4 hours per colonist. |
| `trigger_relationship_shift` | `speakerName`, `recipientName`, `shiftAmount` (number, −15..15), `reason` — all required | Opinion shift between two colonists via social thought memories. Max once per 6 hours per pair. |
| `inspire_colonist` | `pawnName`, `inspirationType` (`"frenzy"`\|`"trade"`\|`"surgery"`\|`"creative"`) — both required | Attempts a vanilla inspiration on a happy colonist. Once per day colony-wide. |
| `apply_conversion_attempt` | `initiatorName`, `recipientName` — both required | Vanilla faith conversion check (opinion, social skill, certainty). Ideology. |
| `attempt_mental_soothe` | `initiatorName`, `recipientName` — both required | Tries to calm a panic or light void mental break via conversation. Anomaly. |

All handlers resolve pawns by short label via `ConversationMcpTools.FindPawnByName(string)` (case-insensitive; searches all maps then world pawns) and return `{ "error": "..." }` on failure rather than throwing.

**Worked example — call a read tool headlessly** (this is exactly what the `Dump agent read-tools` debug action does):

```csharp
string args = "{\"pawnName\": \"" + pawn.LabelShort + "\"}";
string json = SynapseToolRegistry.ExecuteTool("get_chat_history", args, allowMutating: false);
// json => {"pawn":"Bran","count":3,"messages":[{"speaker":"Bran","text":"...","tick":123456}, ...]}
```

To add your own tool, register it in your own startup (do not edit `RegisterTools`):

```csharp
SynapseToolRegistry.RegisterTool(
    "my_tool",
    "What it does, one sentence.",
    new { type = "object",
          properties = new { pawnName = new { type = "string", description = "Target colonist" } },
          required = new[] { "pawnName" } },
    argsJson => { /* parse, act, return a JSON string */ return "{}"; });
// If it changes world state, flag it so gated runs can refuse it:
SynapseToolRegistry.MarkMutating("my_tool");
```

---

## Adding Conversation Topics (ChatTopicDef)

`ChatTopicDef : Verse.Def` (`Source/Defs/ChatTopicDef.cs`) is the data-driven topic. Add topics purely in XML — no code needed. Topics are the flavour catalogue for the ambient conversation system; each is individually toggleable in mod settings.

**Fields:**

| Field | Type | Meaning |
|-------|------|---------|
| `defName` | string | Def identity (also used as a topic key for pool/recent-topic tracking). |
| `label` | string | Standard `Def.label`. |
| `topicName` | string | Human-readable name, used when tagging the social memories a conversation plants. |
| `prompts` | `List<string>` | Prompt-flavour lines the topic can be about. |
| `isChitchat` | bool | Fires on light social interactions. |
| `isDeepTalk` | bool | Fires on `DeepTalk` interactions. |
| `contextKeys` | `List<string>` | Live-data hooks resolved into prompt text when the topic is chosen (see [Context Keys](#context-keys)). Empty = a pure prompt-flavour topic. **Unknown keys are silently ignored**, so you can invent keys or add topics without a code change. |

**Worked example** (from `Defs/ChatTopicDefs/ChatTopics.xml`):

```xml
<RimSynapse.Conversations.ChatTopicDef>
  <defName>RimSynapse_Topic_Quarters</defName>
  <label>their quarters</label>
  <topicName>Their Quarters</topicName>
  <isChitchat>true</isChitchat>
  <isDeepTalk>false</isDeepTalk>
  <contextKeys><li>ownedRoom</li></contextKeys>
  <prompts>
    <li>their sleeping quarters and how comfortable (or not) they are</li>
    <li>something they'd change about where they bunk</li>
    <li>whether they'd like a room to themselves</li>
  </prompts>
</RimSynapse.Conversations.ChatTopicDef>
```

A deep-talk topic can list multiple context keys (e.g. `RimSynapse_Topic_HomeAndBelonging` uses `<li>ownedRoom</li><li>residency</li>`). Chit-chat is kept lean; deep talk maximizes the context budget.

> **Note on v0.9:** live ambient beat selection now runs through `ConversationBeatResolver` (see [The Generation Pipeline](#the-generation-pipeline)), which resolves a concrete beat from live pawn state rather than reading a `ChatTopicDef`'s `prompts` directly. `ChatTopicDef` remains the catalogue for the pre-seed pool, recent-topic avoidance, memory tagging (`topicName`), and the settings toggles; its `defName`/`isDeepTalk` are read by the world component's pool logic (`TopicIsDeep`).

---

## Context Keys

`ConversationContextResolver` (`Source/ConversationContextResolver.cs`, `public static`) turns a topic's `contextKeys` into live prompt text at generation time. Every read is Core-only and side-effect-free, so it is safe on the background pre-generation thread.

**Public API:**

```csharp
// Resolve every key to text, one per line; "" when nothing applies.
public static string ResolveAll(List<string> keys, Pawn pawn, Pawn recipient, SynapseCorePawnComp core)

// Resolve one key; null for an unknown/inapplicable key.
public static string Resolve(string key, Pawn pawn, Pawn recipient, SynapseCorePawnComp core)

// The memory line included in every prompt: today's events for chit-chat,
// long-standing burdens for deep talk.
public static string BaseMemoryLine(SynapseCorePawnComp core, bool isDeepTalk)
```

`Resolve` is a `switch` on the key string; an unknown key returns `null` and is dropped by `ResolveAll`. That is the extension seam: **a modder adds a context key by adding a `case` to this switch** (or, since unknown keys are ignored, by shipping their own resolver and reading `contextKeys` themselves). Each resolver returns a single human-readable line or `null` when it doesn't apply.

**Known keys** (all degrade to `null` when the relevant data/DLC is absent):

| Key | Produces |
|-----|----------|
| `ownedRoom` | Quarters description (throne room / owned room quality via `RoomStatDefOf.Impressiveness` / barracks / none). |
| `apparel` | Up to 3 worn items with quality and condition. |
| `health` | Pain level and injury count. |
| `bondedAnimal` | Name of a bonded animal, if any. |
| `food` | Hunger state plus last-meal impression. |
| `memoriesToday` | Up to 2 memory summaries from the last in-game day. |
| `memoriesLongTerm` | Top 3 memories ordered by `isLongTerm`, then `salience`, then `weight`. |
| `griefMemories` | Memories tagged `Death`/`Died`/`Grief` (via `core.GetMemoriesByTag`). |
| `traumaMemories` | Memories tagged `Trauma`/`Horror`/`TraitShift`/`Desensitization`. |
| `recipientRelationship` | Opinion (−100..100) and relation label toward the recipient. |
| `personalitySummary` | `core.personalitySummary` as a self-image line. |
| `residency` | Whether the pawn is a settled resident (`SynapseCoreProviders.IsResident`). |
| `resistanceWill` | Prisoner recruitment resistance, enslavement will, ideological certainty. Captives only. |
| `captivity` | How they're held and the current warden interaction-mode policy. |
| `prisonerComfort` | Hunger, comfort, bed quality/cell, clothing condition. Captives only. |
| `ideoConflict` | Concrete meme clash between the prisoner's ideoligion and the colony's. Ideology only. |
| `captureMemory` | A Core `EventReflection` memory that reads like a capture, plus a rough duration since. |

The prisoner-side keys (`resistanceWill`, `captivity`, `prisonerComfort`, `ideoConflict`, `captureMemory`) are what the warden system leans on; see [Warden Conversations](#warden-conversations).

**Worked example:**

```csharp
var core = pawn.TryGetComp<SynapseCorePawnComp>();
string block = ConversationContextResolver.ResolveAll(
    new List<string> { "ownedRoom", "health", "recipientRelationship" },
    pawn, recipient, core);
// block =>
//   Quarters: a modest bedroom (impressiveness 34), their own.
//   Health: minor pain, 1 injury.
//   Toward Mara: opinion 22 (-100..100), friend.
```

---

## The Generation Pipeline

v0.9 is **agent-first** (Conversations#46, the "WorldNews pattern"): the agent (C#) decides *what* a conversation is about and *each speaker's angle* before any LLM call, so the model's only job is wording. This replaced the old per-pawn psychology dump that pushed small local models into generic filler.

The flow for an ambient exchange:

1. **Trigger** — the Harmony postfix on `Pawn_InteractionsTracker.TryInteractWith` fires (see [Harmony Surface](#harmony-surface)), passes the initiation/response gates, and calls `TriggerLlmDialogue`.
2. **Resolve a beat** — `ConversationBeatResolver.Resolve(...)` reads live pawn state and returns one `ConversationBeat`.
3. **Build a thin prompt** — `ThinDialoguePrompt.Build(...)` puts rules + JSON schema in the *system* message and the concrete beat in the *user* message.
4. **Generate** — via `SynapseClient.PromptAsync` (system + user, never a lone system message — a local model returns an empty ack otherwise).
5. **Parse** — `ParseLinesLenient` extracts the `lines` array tolerantly.
6. **Compute offsets** — `SocialOffsetCalculator.Compute(...)` derives trust/familiarity/affinity *in code*.
7. **Apply** — first line lands immediately as a bubble; the rest drip-feed via the map component; memories propagate; offsets apply.

### ConversationBeat (`public class`, `Source/Generation/ConversationBeat.cs`)

One concrete beat — exactly one thing to talk about and each speaker's angle.

| Field | Type | Meaning |
|-------|------|---------|
| `subject` | string | The single concrete thing the exchange is about. |
| `initiatorStance` | string | The initiator's concrete angle (what they feel/want/are getting at). |
| `recipientStance` | string | The recipient's angle, coloured by opinion and their own state. |
| `tone` | `BeatTone` | `Casual`, `Heartfelt`, `Coercive`, or `Negotiating`. |
| `framing` | `BeatFraming` | `Shared` (both lived it) or `InitiatorTells` (recipient hears it fresh). |
| `isDeep` | bool | Deep talk vs chit-chat. |
| `topicKey` | string | Label for recent-topic tracking/metrics (a `ChatTopicDef` defName, `"event:<id>"`, or a synthetic tag like `"burden"`/`"day"`/`"warden:<mode>"`). Never shown to the player. |
| `IsCoercive` | bool (get) | `tone == BeatTone.Coercive`. |

`BeatTone` and `BeatFraming` are public enums in the same file. `BeatFraming.InitiatorTells` is the guard against a pawn claiming a memory that isn't theirs (the Bill/Lipos relevance bug): the recipient must react, not claim first-hand memory.

### ConversationBeatResolver (`public static`, `Source/Generation/ConversationBeatResolver.cs`)

```csharp
public static ConversationBeat Resolve(
    Pawn initiator, Pawn recipient, bool isDeep, ICollection<string> avoidTopics)
```

How it picks a beat, in priority order:
1. **Event beat** — a concrete `EventReflection` memory the initiator actually lived through. Deep talk always reaches for one; chit-chat takes one with `EventBeatChance = 0.55`. Framing is `Shared` only if the recipient holds a matching memory (same `memId` or near-identical summary), else `InitiatorTells`. Recent = `isLongTerm` or within `RecentEventTicks` (~3 days). Honours `avoidTopics`.
2. **Deep-talk burden** — if no event, the weightiest memory quietly weighing on them (`Heartfelt`).
3. **Chit-chat** — something concrete in the initiator's day: a recent job (`GetRecentJobsSummary`), a pressing physical/emotional state (hunger/rest/pain/mood), or a weather observation.

The recipient's stance is coloured by opinion (`OpinionOf`) and their own pressing state, without scripting words.

`SelectEventMemoryCandidate(SynapseCorePawnComp core, bool deep, ICollection<string> avoid)` is **public** on `Patch_Pawn_InteractionsTracker_TryInteractWith` — the deterministic, Rand-free candidate pick, retained for the test suite. Live selection runs through the resolver's private `SelectEventMemory`.

### SocialOffsetCalculator (`public static`, `Source/Generation/SocialOffsetCalculator.cs`)

```csharp
public struct SocialOffsets { public float trust; public float familiarity; public float affinity; }

public static SocialOffsets Compute(Pawn initiator, Pawn recipient, ConversationBeat beat)
```

Deterministic from the pair's opinion and the beat's tone, so offsets stay consistent and the LLM call can return prose only. Thresholds line up with `ApplyVanillaAffinityThought`: affinity `>= +2` plants a `Chitchat` thought, `<= −2` plants a `Slight`, and everything between plants nothing. A coercive beat always returns `{ trust = −0.6, familiarity = 0, affinity = −2 }` (coercion never warms). Otherwise familiarity is `2` (deep) / `1.2` (chit-chat), trust and affinity scale with opinion, and deep talk nudges both up. All three are clamped: trust/affinity to `[−2, 2]`, familiarity to `[0, 3]`.

### ThinDialoguePrompt (`public static`, `Source/Generation/ThinDialoguePrompt.cs`)

```csharp
public struct ThinPrompt { public string system; public string user; }

public static ThinPrompt Build(
    Pawn initiator, Pawn recipient, ConversationBeat beat, string continuationHistory)
```

Builds the WorldNews-style prompt: length rule (2–4 short lines for chit-chat, 4–8 heartfelt lines for deep talk) and tone rule + JSON schema (`{"lines":[...]}`) go in the *system* message; the concrete beat (identities, stances, subject, framing note, optional continuation history) goes in the *user* message. Identity is a one-line handle — the pawn's authored `voiceProfile` if present, else one or two traits. Pass `continuationHistory` (the last few lines joined) when picking up a recent exchange, else `null`.

### Other pipeline paths (internal, in `Patch_Pawn_InteractionsTracker_TryInteractWith`)

These live in `Source/Patches/InteractionDialogue.cs` and are `internal`/`private` — informational, not a stable API:

- **Pre-seed pool** (experimental, off by default): `QueuePreGeneration` fills a per-pair pool; `PopFreshPreGen` serves it instantly. Public entry `TryTopUpPreGenPool(SynapseConversationsWorldComponent)` is called from the world component's rare tick.
- **Pre-staged event retellings** (`preStageEventConversations`, on by default): `TryStageEventConversations(...)` (public) generates per-pair retellings of recent events; one fires at random per conversation start (`eventPreStageFireChance`, default 0.30).
- **Load-adaptive shedding** (`adaptiveConversationShedding`): when Core's pipeline is behind, `ShouldShedForLoad()` returns true and `ApplyShedConversation` moves relationships via code-computed offsets with **no LLM call**. Public headless validator: `DebugForceShed(Pawn initiator, Pawn recipient, bool deep)` returns a one-line summary string.
- **Environmental dialogue**: `TriggerEnvironmentalLlmDialogue(Pawn, Pawn, string type, string description)` is **public** — a two-call statement/response exchange for environmental triggers (darkness, freezer, etc.).

---

## Warden Conversations

`Source/Warden/` turns vanilla warden work (recruit, convert, enslave, suppress) into spoken exchanges (Conversations#42). These conversations are **flavour only** — they never touch resistance, will, or ideology certainty; the vanilla roll stands untouched.

### WardenMode (`public enum`, `Source/Warden/WardenMode.cs`)

`Recruitment`, `Conversion`, `Enslavement`, `Suppression`. Recruitment covers both "reduce resistance" and "attempt recruit". Conversion/enslavement/suppression are Ideology-DLC activities and degrade to silence without the DLC.

`WardenModeExtensions` (public):
- `bool IsCoercive(this WardenMode mode)` — true for `Enslavement`/`Suppression` (these must never plant warm familiarity).
- `string Label(this WardenMode mode)` — short label used in memory summaries and the history topic tag.

### WardenConversationController (`public static`, `Source/Warden/WardenConversationController.cs`)

```csharp
// Trigger entry from the InteractionWorker postfixes. Applies the enable toggle, validity
// gate, and per-pair cooldown, then fires an async live generation.
public static void OnWardenInteraction(Pawn warden, Pawn prisoner, WardenMode mode)

// Debug/validation path: force the exchange regardless of job state or cooldown, and dump
// the resolved prisoner context. Safe to call headlessly.
public static void ForceWardenConversation(Pawn warden, Pawn prisoner, WardenMode mode)
```

`OnWardenInteraction` gates on `enableWardenConversations`, a valid humanlike warden/captive pair, and a per-pair cooldown (`wardenConversationCooldownHours`). It resolves prisoner-side context (`ContextKeysFor(mode)` — a mode-specific subset of `captivity`/`resistanceWill`/`prisonerComfort`/`ideoConflict`/`captureMemory`), builds a mode-appropriate system+user prompt, calls `SynapseClient.PromptAsync`, and applies the exchange through the same bubble + drip-feed + `SocialOffsetCalculator` path as ambient chatter. Coercive modes route through the calculator's coercive branch automatically.

**Worked example:**

```csharp
// Fire a recruitment exchange between a warden and a prisoner, respecting cooldown/toggle:
WardenConversationController.OnWardenInteraction(warden, prisoner, WardenMode.Recruitment);

// Or force one for validation, dumping the resolved prisoner context to the log:
WardenConversationController.ForceWardenConversation(warden, prisoner, WardenMode.Enslavement);
```

---

## Harmony Surface

Conversations patches these vanilla methods. If your mod patches the same targets, be aware of the interaction.

| Patch class | Target | Kind |
|-------------|--------|------|
| `Patch_Pawn_InteractionsTracker_TryInteractWith` | `Pawn_InteractionsTracker.TryInteractWith` | Prefix (pass-through) + Postfix |
| `Patch_InteractionWorker_RecruitAttempt` | `InteractionWorker_RecruitAttempt.Interacted` | Postfix → `WardenMode.Recruitment` |
| `Patch_InteractionWorker_ConvertIdeoAttempt` | `InteractionWorker_ConvertIdeoAttempt.Interacted` | Postfix → `WardenMode.Conversion` |
| `Patch_InteractionWorker_EnslaveAttempt` | `InteractionWorker_EnslaveAttempt.Interacted` | Postfix → `WardenMode.Enslavement` |
| `Patch_InteractionWorker_Suppress` | `InteractionWorker_Suppress.Interacted` | Postfix → `WardenMode.Suppression` |
| `Patch_FloatMenuMakerMap_AddHumanlikeOrders` | `FloatMenuMakerMap.GetOptions` | — |
| `Patch_Pawn_GetGizmos` | `ThingWithComps.GetGizmos` | Adds the Chat History gizmo |
| `Patch_MapInterface_MapInterfaceOnGUI` | `MapInterface.MapInterfaceOnGUI_AfterMainTabs`, `MoteMaker.MakeInteractionBubble` | Speech-bubble rendering |

### The core interaction postfix

`Patch_Pawn_InteractionsTracker_TryInteractWith.Postfix` is the ambient trigger. It bails unless the vanilla interaction succeeded (`__result`), both pawns are spawned humanlikes, and the per-pair cooldown (`dialogueCooldownHours`) has elapsed. It then rolls two gates — `CalculateInitiationChance` (introverts, melancholic/phlegmatic temperaments, and low opinion reduce it) and `CalculateResponseChance` (temperament, opinion, passions, speaker charisma, and an insulting-spree composure branch for high-Medicine or high-trust recipients) — before calling `TriggerLlmDialogue`. A declined initiation does **not** consume the cooldown; a silent non-response does.

### Public entry points on the patch class

These are callable without reflection (`public static`):

| Member | Signature | Use |
|--------|-----------|-----|
| `ForceConversation` | `(Pawn initiator, Pawn recipient, InteractionDef intDef)` | Force a live ambient conversation now. |
| `TriggerEnvironmentalLlmDialogue` | `(Pawn initiator, Pawn recipient, string type, string description)` | Fire an environmental-trigger exchange. |
| `SelectEventMemoryCandidate` | `(SynapseCorePawnComp core, bool deep, ICollection<string> avoid)` → `WeightedMemory` | Deterministic event-memory pick (test/validation). |
| `DebugForceShed` | `(Pawn initiator, Pawn recipient, bool deep)` → `string` | Run a shed (no-LLM) conversation, return an offset summary. |
| `TryTopUpPreGenPool` | `(SynapseConversationsWorldComponent worldComp)` | Idle-cycle pool top-up (called from world tick). |
| `TryStageEventConversations` | `(SynapseConversationsWorldComponent worldComp)` | Pre-stage event retellings (called from world tick). |

### Internal helpers (not a stable API — documented so you recognise them)

`internal`/`private` members of the patch class you will see referenced in the code and the warden controller:

- `ParseLinesLenient(string content)` → `List<string>` (**internal**) — extracts the `lines` array, tolerating malformed JSON from small models (stray brackets, truncated closes, trailing prose).
- `CleanLine(string line, Pawn a, Pawn b)` → `string` (**internal**) — strips leading `- `, wrapping quotes, and a leading `Name:`/`Name,` prefix from a generated line.
- `ExtractJson(string content)` → `string` (**internal**) — trims to the first `{` … last `}`.
- `ApplyPsychologyOffsets(Pawn, Pawn, float trust, float familiarity)` (**internal**) — writes trust/familiarity into the Psychology mod's `socialNetwork` via reflection (no hard dependency on Psychology).
- `ApplyVanillaAffinityThought(Pawn, Pawn, float affinity)` (**internal**) — plants a `Chitchat` (`>= +2`) or `Slight` (`<= −2`) vanilla memory.
- `PropagateContextMemories(Pawn, Pawn, string topicName, string reply)` (**internal**) — writes social memories to initiator, recipient, and the closest bystander within earshot. Earshot base range is 8 cells, reduced 1 per local noise source (running generators/turbines/mills, or nearby mining/cutting/deconstruct/repair/attack/harvest jobs), and blocked by walls unless both pawns are outdoors (`CalculateEarshotRange`).

The warden controller reaches `ParseLinesLenient`, `CleanLine`, `ApplyPsychologyOffsets`, `ApplyVanillaAffinityThought`, and `PropagateContextMemories` (all same-assembly `internal`), which is why they are `internal` rather than `private`.

---

## Debug Actions

All debug actions live under the shared **"RimSynapse"** menu category (dev mode → wrench → Debug Actions). Actions declared `DebugActionType.ToolMapForPawns` take a single `Pawn` and are **headlessly triggerable** via the dev-tools bridge (`run_debug_action` with `pawnName`, or `list_debug_actions`) through the MCP `execute_game_tool`; `DebugActionType.Action` entries run immediately with no target.

`Source/UI/DebugActions_Conversations.cs`:

| Action | Type | What it does |
|--------|------|--------------|
| `Conversations: Dump context (Log)` | ToolMapForPawns | Logs every context key resolved for the pawn (nearest colonist as recipient), both memory tiers, and pool state for the pair. |
| `Conversations: Force chit-chat (Tool)` | ToolMapForPawns | Forces a live chit-chat with the nearest colonist. |
| `Conversations: Force deep talk (Tool)` | ToolMapForPawns | Forces a live deep talk with the nearest colonist. |
| `Conversations: Force nearby exchange (Tool)` | ToolMapForPawns | Teleports the nearest colonist adjacent, then forces a chit-chat so the drip-feed can play out in range. |
| `Conversations: Force shed exchange (Tool)` | ToolMapForPawns | Runs the no-LLM shed path and logs offsets + live backpressure readings. |
| `Conversations: Dump last exchange (Tool)` | ToolMapForPawns | Logs the pawn's most recent exchange, speaker by speaker. |
| `Conversations: Dump metrics (Log)` | Action | Logs `ConversationMetrics.Summary()`. |
| `Conversations: Force environmental scan (Log)` | Action | Forces the environmental-trigger scan for all colonists. |
| `Conversations: Dump agent read-tools (Tool)` | ToolMapForPawns | Runs `get_chat_history` and `get_colonist_interests` on the pawn and logs the JSON. |
| `Conversations: Dump pre-gen pool (Log)` | Action | Logs the whole pre-gen pool. |

`Source/UI/DebugActions_Warden.cs`:

| Action | Type | What it does |
|--------|------|--------------|
| `Warden: Force recruitment (Tool)` | ToolMapForPawns | Forces a recruitment exchange (nearest colonist as warden). |
| `Warden: Force conversion (Tool)` | ToolMapForPawns | Forces a conversion exchange (requires Ideology). |
| `Warden: Force enslavement (Tool)` | ToolMapForPawns | Forces an enslavement exchange (requires Ideology). |
| `Warden: Force suppression (Tool)` | ToolMapForPawns | Forces a suppression exchange (requires Ideology). |
| `Warden: Spawn test prisoner (Log)` | Action | Spawns a deliberately poorly-kept prisoner (resistance 18, hungry, stripped) to validate recruitment dialogue against real conditions. |

The warden force actions pick the nearest free colonist as warden and call `WardenConversationController.ForceWardenConversation`, which dumps the resolved prisoner context and the generated lines to the log.

---

## Core Dependencies

Conversations depends on **RimSynapse Core** for the LLM pipeline, the tool registry, and per-pawn state. It never contacts a provider directly.

### SynapseClient — running prompts

```csharp
// Preferred path (system + user). A lone system message makes local models return an empty ack,
// so the concrete beat/scene MUST be the user message.
SynapseClient.PromptAsync(modHandle, string system, string user,
                          Action<Result> onComplete, ChatOptions options);

// Full message-list path (used for the two-call environmental exchange).
SynapseClient.ChatAsync(modHandle, List<ChatMessage> messages,
                        ChatOptions options, Action<Result> onComplete);
```

- `modHandle` is `RimSynapseConversationsMod.ModHandle`.
- The result callback receives an object with `bool success` and `string content`; always check both before parsing.
- `ChatOptions` fields used here: `int priority` (lower = higher priority; ambient conversations use `1`, background pre-staging uses `6`), `string requestName`, `string targetName`.
- `ChatMessage` has `string role` (`"system"`/`"user"`) and `string content`.
- Callbacks run off the game thread — marshal any world mutation back with `SynapseGameComponent.Enqueue(Action)`.

### SynapseToolRegistry — registering and running tools

```csharp
SynapseToolRegistry.RegisterTool(string name, string description, object schema, Func<string,string> handler);
SynapseToolRegistry.MarkMutating(params string[] toolNames);
SynapseToolRegistry.ExecuteTool(string name, string argsJson, bool allowMutating); // → string (JSON)
```

See [Registered Game Tools](#registered-game-tools).

### SynapseCorePawnComp — per-pawn state

`pawn.TryGetComp<SynapseCorePawnComp>()` (namespace `RimSynapse.Comps`). Members read across Conversations:

| Member | Type | Use |
|--------|------|-----|
| `memories` | `List<WeightedMemory>` | The pawn's memory store. `WeightedMemory` exposes `summary`, `memoryType` (e.g. `"EventReflection"`, `"social"`), `tags`, `memId`, `absTick`, `gameTick`, `weight`, `baseWeight`, `decayRate`, `salience`, `isLongTerm`. |
| `GetMemoriesByTag(string tag)` | `IEnumerable<WeightedMemory>` | Tag lookup used by `griefMemories`/`traumaMemories`. |
| `GetRecentJobsSummary()` | `string` | Recent-activity phrase for chit-chat beats. |
| `voiceProfile` | `string` | Authored speaking voice (used by `ThinDialoguePrompt.Identity`). |
| `llmTraits` | `List<string>` | Psychology traits (Jungian type, temperament) used by the initiation/response gates. |
| `personalitySummary` | `string` | Self-image line for the `personalitySummary` context key. |

Also from Core: `SynapseCoreProviders.IsResident(Pawn)` (residency), `RimSynapseMod.Instance.Settings.shortTermMemoryHours` (memory-age slider), and `Utils.SynapseDateHelper.GameTickToAbsTick(int)`.

### Backpressure signals

Conversations is the lowest-value LLM consumer, so it yields when the shared pipeline is behind (Conversations#38):

- `SynapseClient.TotalQueueDepth` (int) — pending requests across the pipeline. Compared against the `conversationQueueDepthCap` setting.
- `SynapseClient.ThrottleLevel` (float, `1.0` full speed → `0.0` paused) — Conversations sheds below `0.5` (`ShedThrottleFloor`).

When `ShouldShedForLoad()` is true, live generation and background pre-gen/pre-staging are skipped; relationships still move via the code-computed offsets.

---

## The World Component

`SynapseConversationsWorldComponent : WorldComponent` (`Source/SynapseConversationsWorldComponent.cs`) owns pawn-to-pawn dialogue state (the player↔storyteller log moved to Core in #99). Reach it with `Find.World.GetComponent<SynapseConversationsWorldComponent>()`.

Public surface of interest:

| Member | Type | Use |
|--------|------|-----|
| `pawnConversations` | `List<PawnConversation>` | All per-pair conversation records. A `PawnConversation` has `pawnAId`, `pawnBId`, `messages` (`List<SynapseConversationMessage>` with `sender`, `message`, `gameTick`), `lastTick`, `recentTopics`, and `PushRecentTopic(string)`. |
| `preGenPool` | `List<PreGeneratedConversation>` | The pre-seed / event-retelling pool. |
| `PawnFromId(string thingId)` | `static Pawn` | Resolve a spawned pawn from its `ThingID`. |
| `PoolCountForPair` / `PoolTopicsForPair` / `PairNeedsFill` / `PoolAtTotalCap` | — | Pool inspection/gating per pair. |
| `AddToPool` / `PopFreshPreGen` | — | Chit-chat pool add/take. |
| `AddEventPreGen` / `PopEventPreGenForPair` / `PairHasStagedEvent` / `CanStageMoreEvents` / `EventPreGenCount` | — | Event-retelling pool (#35). |
| `PrunePool` / `HasSignificantMemorySince` | — | TTL + significant-event (`Death`/`Grief`/`Betrayal`/`TraitShift`) invalidation. |

Bounds: `MaxPreGenPerPair = 3`, `MaxPreGenTotal = 40`, `MaxEventPreGensTotal = 24`.

**Worked example — read a pawn's latest exchange:**

```csharp
var wc = Find.World.GetComponent<SynapseConversationsWorldComponent>();
string id = pawn.ThingID;
var conv = wc.pawnConversations
    .Where(c => c.pawnAId == id || c.pawnBId == id)
    .OrderByDescending(c => c.lastTick)
    .FirstOrDefault();
foreach (var m in conv?.messages ?? Enumerable.Empty<SynapseConversationMessage>())
{
    Pawn speaker = SynapseConversationsWorldComponent.PawnFromId(m.sender);
    Log.Message($"{speaker?.LabelShort ?? m.sender}: {m.message}");
}
```
