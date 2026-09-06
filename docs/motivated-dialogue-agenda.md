# Phase 1 — Conversational Agenda (Motivated Dialogue substrate)

Build spec for epic **#60**. The reactive topic model is replaced by a per-pawn **agenda** of
talking points: pawns talk because they *want* to say something, the topic and its dialogue are
pregenerated at event time, and conversations become cache hits at interaction time.

Locked decisions (see #60): Psychology is a **hard dependency** (done); the trigger is a
**psychology hook**, not vanilla `Chitchat`/`DeepTalk`; firing is a **capped, per-pair,
proximity-prioritised pre-gen pool**; selection is **pure C#**, only phrasing is LLM
(pregenerated). Chit-chat vs deep-talk survive as a **register**, derived from topic weight + mood.

---

## 1. Data model

### `TalkingPoint` (IExposable) — one thing a pawn wants to say
| field | type | meaning |
| --- | --- | --- |
| `id` | string | unique |
| `subjectMemId` | string | the Core `SynapseCorePawnComp` memory it's grounded in (or a synthetic key for activity/observation points) |
| `subjectSummary` | string | cached human phrase — becomes the beat subject |
| `register` | `Register` | `ChitChat` \| `DeepTalk`, derived at formation from memory weight + mood |
| `salience` | float | how much they want to share — drives priority and decay rate |
| `provenance` | `Provenance` | `FirstHand` \| `Heard` (Heard = a rumor) |
| `sourcePawnId` | string | if Heard, who told them — for rumor chains and "don't retell it to the source" |
| `audience` | `Audience` | `Anyone` \| `Only(pawnId)` \| `Not(pawnId)` |
| `secret` | bool | limits spread (rumor mill); derived from memory tags (trauma/betrayal) + salience |
| `toldIds` | List&lt;string&gt; | listeners already told — consumed **per listener**, not globally |
| `formedTick` | int | for decay |

Storage: a **new `ThingComp` `SynapseConversationAgendaComp`** on humanlike pawns (def-injected via a
patch on humanlike ThingDefs). Rationale: per-pawn state that scribes with the pawn and is cleaned up
automatically when the pawn is destroyed/leaves — no world-component pawn-lifecycle bookkeeping.
Holds `List<TalkingPoint>`, capped (see §3). *(Alternative considered: a `Dictionary<pawnId,…>` on
`SynapseConversationsWorldComponent`, consistent with `pawnConversations`, but it needs manual cleanup
when pawns leave. Comp wins.)*

The **pregenerated dialogue** for a point is NOT on the point — it's pair state, and lives in the
existing pre-gen pool (`SynapseConversationsWorldComponent.preGenPool`, `PreGeneratedConversation`),
extended with a `pointId` so a pooled conversation is keyed `(speaker, listener, pointId)`.

---

## 2. Formation — the psychology hook

Points form from psychological state, on a rare-tick cadence per conversant pawn (`MayConverse`),
reading Core memories + Psychology:
- **Event** — a significant `EventReflection` memory (Core comp) → a `FirstHand` point. Salience from
  the memory's `salience`/`weight`. Register: heavy/emotional → `DeepTalk`, light → `ChitChat`.
- **Burden** — the weightiest thing on them (Core `GetTopMemoryBurdens`) → a `DeepTalk` point.
- **Activity** (#43 folds in here) — the work they've been doing (Core `GetRecentJobsSummary`, already
  humanised) → a low-salience `ChitChat` point ("been fixing the cooler all day").
- **Bond shift** — a notable change in Psychology `socialNetwork` (trust/familiarity) → a point about
  a third pawn ("worried about X"), audience-restricted so they don't say it *to* X.
- **Link** (outsider pairs — folds #52 + #53) — a pawn with **no psychology background** (a visitor,
  trader or guest with no memories) has an empty agenda, and a resident has no point *about* them, so
  outsider pairs would fall silent. Instead: seed a low-salience `ChitChat` point from an authored
  **`ConversationLinkDef`** bank keyed by the *role pair and direction* — visitor→resident ("what's new
  around here", "how long have you lived here"), resident→visitor ("what brings you this way", "how's the
  road been"), resident→new citizen ("settling in yet", "when did you join up"). Audience is role-scoped.
  A link is **half generic, half psychology**: the blank pawn's line is authored and costs nothing; the
  resident's answer is a normal psychology-voiced line with a few **cheap C# facts** injected — tenure
  (time as colonist), arrival (faction + `ConversationRole`, `RelationshipTone` #53), what's new (the
  today-tier memories, kept in the scrub), and the place (colony name / age). These are *pair-and-role*
  reads, not the retired colonist self-describers (room/apparel/food). A recurring guest who *does* have
  a Psychology profile gets the full treatment; the bank is the fallback for blank pawns, not the rule.
- **Colony rumor** (outsiders) — visitors arrive **already carrying `Heard` points about the colony**,
  seeded at arrival by a randomised pick over Core's colony event history
  (`SynapseCoreWorldComponent.shortTermEvents` / `pawnEventRecords`): "heard you took on someone new",
  "heard the raid went badly for the pirates". `sourcePawnId` is null (the road told them); audience is
  residents. This is the rumor mill (§5) applied to outsiders — the same `Heard` provenance, no separate
  system — and it means the colony's own history becomes what the world says about it.

This **replaces** `ConversationBeatResolver`'s reactive subject-picking. That resolver's per-branch
logic (event / burden / activity / observation) becomes the **formation rules** above; the point *is*
the beat. Reactive picking survives only as a last-resort low-salience ambient point (weather /
"nothing much") for a pawn whose agenda is empty.

---

## 3. Lifecycle

`form` → `match` → `pregenerate` → `serve` → `consume` → `decay`, plus `propagate`.

- **Cap & prioritise** — keep the top N (start N=4) points by salience; drop the weakest over cap.
  Forces pawns to have a *few* things they actually care about.
- **Match** — pick a listener for a point: in proximity, satisfies `audience`, not in `toldIds`, and —
  cheaply — doesn't already hold the same `subjectMemId` (they were there / already heard it).
- **Pregenerate** — build the beat from the point (subject, register, stances coloured by psychology +
  `RelationshipTone` #53), generate the exchange **once** via `ThinDialoguePrompt` (per-line speaker,
  #40), cache in the pool keyed `(speaker, listener, pointId)`. Priority by proximity / likelihood of
  meeting, capped by the pool total.
- **Serve** — on the trigger firing (§4), a pooled conversation for `(speaker, listener, point)` plays
  instantly — **no interaction-time LLM call**.
- **Consume** — add the listener to `toldIds`. A point fully told to its audience is removed.
- **Decay** — salience falls over time; a point below threshold, or older than the retention window
  (24h/72h, the #40 setting), is pruned. Points don't outlive the transcript.

---

## 4. Trigger — replace the vanilla entry point

Delete the `Patch_Pawn_InteractionsTracker` ambient trigger (riding vanilla `Chitchat`/`DeepTalk`) and
the `IsTalkative` environmental gating as the *drivers*. New driver: a **psychology/proximity scan** on
a cadence over `MayConverse` pawns —

1. pawn has a non-empty agenda,
2. a valid listener is in proximity (reuse the earshot logic),
3. the pawn is in a talking mood (needs/mood/downtime — the psychology hook),
4. off the per-pair cooldown.

If a pooled conversation exists for the matched `(speaker, listener, point)` → fire it (cache hit). If
not → optionally generate live at low priority (or skip and let pregeneration catch up). The register
(chit-chat vs deep) comes from the **point**, never a vanilla `InteractionDef`. The warden path (#42)
keeps its own trigger; outsiders (#52) keep the bark path.

Consolidation: the pre-gen pool (#28) and event pre-staging (#35) fold into point-driven
pregeneration — #35's per-pair event retellings *are* a `FirstHand` event point pregenerated for nearby
pairs.

---

## 5. Propagation — rumor mill (#36)

When A serves a point to B, B gains a **derived** point: `provenance = Heard`, `sourcePawnId = A`,
same `subjectSummary` (v1: deterministic drift in code; later: an optional LLM "retell in your words"
pass), `salience` scaled by a spread factor. `secret` points resist forming a derived point (B rarely
re-spreads a confidence). Audience of the derived point excludes A (don't retell it to the teller).
This is the whole rumor mill: propagation over the agenda graph, no separate system.

## 6. Cliques — emergent (later)

Track agenda-exchange counts per ordered pair (who tells whom / who receives). High mutual exchange is
a clique; can feed a small relationship offset or just be exposed as a read. v1 tracks counts only;
clique detection is a follow-on.

---

## 7. Build order (within Phase 1)

1. **Data model** — `TalkingPoint` + `SynapseConversationAgendaComp` + def-injection patch + scribe.
   Debug: "Dump agenda". *(compile-safe, no behaviour)*
2. **Formation** — form points from Core memories + Psychology on a cadence; port
   `ConversationBeatResolver`'s branches into formation rules. Debug: "Force-form point". *(behavioural)*
3. **Pregeneration** — point → beat → pooled `(speaker,listener,pointId)` conversation. *(behavioural)*
4. **Trigger swap** — psychology/proximity scan fires pooled conversations; retire the vanilla trigger.
   *(behavioural, the big one)*
5. **Propagation** — #36 derived points + drift. *(behavioural)*
6. **Cliques** — exchange counts. *(follow-on)*

Steps 2–5 change behaviour and need the game to validate (queue on #59). Step 1 is pure structure and
can land anytime.

## 8. Debug actions (required per step)
- **Dump agenda** — a pawn's points (subject, register, salience, provenance, audience, told, age).
- **Force-form point** — from a selected memory, so formation is inspectable without waiting.
- **Force serve / propagate** — fire a point to the nearest valid listener and show the derived point.

## 9. Open questions to settle at build time
- **Formation cadence** — its own rare tick, or piggyback the Psychology daily review (which already
  reads the same memories)? The latter is cheaper and keeps one psychology read.
- **Live-generation fallback** — when no pooled conversation exists at trigger time, generate live at
  low priority, or stay silent and rely on pregeneration? Silent is cheaper and truer to "pregenerated".
- **Register threshold** — the memory-weight/mood cutoff that sorts a point into DeepTalk vs ChitChat.

### Settled
- **`subjectMemId`** = Core `WeightedMemory.memId` (stable, back-filled on load). No new id scheme.
  Synthetic keys use a prefix: `link:<defName>`, `rumor:<eventId>`, `activity:<tick>`.
- **Link bank format** — a separate `ConversationLinkDef` (keyed by role pair + direction), not a
  `links` list on `OutsiderChatterDef`: barks and openers have different audiences, and the *pair* is
  the key, not a single role.
- **"New citizen"** — a resident whose Core `Recruited` pivotal memory is younger than one quadrum
  (15 days) is an eligible target for the settling-in links. This is also the natural hook for making
  recruitment a real conversation (#62).
- **Colony-rumor source** — Core's world-level event ledger, randomised at the visitor's arrival, capped
  at 1–2 `Heard` points per visitor so they don't arrive as a newspaper.
