# Motivated Dialogue — the talking-point agenda

The 0.10 model for *why* pawns talk. Instead of reacting to a vanilla social interaction and inventing a topic on the spot, every pawn carries a short **agenda** of things they *want* to say. Points form from psychology and memory, their dialogue is pregenerated while nothing is happening, and a conversation at interaction time is a cache hit. The same substrate is the rumor mill and, later, cliques.

Tracking epic: Conversations#60. Build spec: `docs/motivated-dialogue-agenda.md` in the repo. **RimSynapse - Psychology is a hard dependency** — every line is voiced from the pawn's psychology profile; there is no generic fallback voice.

---

## The talking point

One thing a pawn wants to say. Grounded in a Core memory or a synthetic key; the *dialogue* for it is not stored on the point (that is pair state, in the pre-generation pool keyed speaker + listener + point).

| Field | Type | Meaning |
| --- | --- | --- |
| `id` | string | Unique. |
| `subjectMemId` | string | The Core `WeightedMemory.memId` it is grounded in, or a synthetic key (`link:<def>`, `rumor:<eventId>`, `activity:<tick>`). |
| `subjectSummary` | string | Cached human phrase — becomes the beat subject. |
| `register` | `ChitChat` \| `DeepTalk` | Derived at formation from memory weight + mood. **Never** from a vanilla InteractionDef. |
| `salience` | float | How much they want to share. Drives priority, the cap, and decay. |
| `provenance` | `FirstHand` \| `Heard` | `Heard` = a rumor: something another pawn (or the road) told them. |
| `sourcePawnId` | string | If `Heard`, who told them. A rumor is never retold to its source. Null when the road told them. |
| `audience` | `Anyone` \| `Only(pawn)` \| `Not(pawn)` | Who it may be told to. `Not(X)` = "don't say it *to* X". |
| `secret` | bool | Resists spreading (trauma / betrayal + high salience). |
| `toldIds` | list | Listeners already told. Consumed **per listener**, not globally. |
| `formedTick` | int | For decay and the retention window. |

**The agenda** (`SynapseConversationAgendaComp`, on every humanlike pawn): keeps the **top 4** points by salience. Same-subject duplicates collapse to the stronger one. A weak point offered to a full agenda is rejected — pawns are forced to have a *few* things they actually care about.

---

## Formation rules — every way a point comes to exist

| # | Rule | Who | Reads | Register | Salience | Provenance | Audience | Example line | Status |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | **Event** | any conversant | a significant Core `EventReflection` memory | heavy / emotional → DeepTalk, light → ChitChat | memory salience / weight | FirstHand | Anyone (matching skips anyone who holds the same memory — they were there) | "Still can't believe the pirates came through the east wall." | built (step 2; in-game validation queued on #59) |
| 2 | **Burden** | any conversant | the weightiest thing on them (Core `GetTopMemoryBurdens`) | DeepTalk | memory weight | FirstHand | Anyone; `secret` if trauma / betrayal tagged | "I keep seeing Ada's face when I close my eyes." | built (step 2; in-game validation queued on #59) |
| 3 | **Activity** | any conversant | the work they've been doing (Core `GetRecentJobsSummary`) | ChitChat | low | FirstHand | Anyone | "Been fixing that cooler all day. Again." | built (step 2, folds #43; in-game validation queued on #59) |
| 4 | **Bond shift** | any conversant | a notable trust / familiarity change in Psychology `socialNetwork` | by magnitude | by magnitude | FirstHand | `Not(X)` — never said *to* the pawn it's about | "Something's off with Marco lately." | built (step 2; in-game validation queued on #59) |
| 5 | **Link** | outsider pairs (resident ↔ visitor / trader / guest, resident ↔ new citizen) | the authored `ConversationLinkDef` bank, keyed by **role pair + direction** | ChitChat | low | FirstHand | role-scoped (a visitor's link only aims at residents) | visitor → resident: "What's new around here?" · resident → visitor: "What brings you this way?" · resident → new citizen: "Settling in yet?" | built (step 2, folds #52 / #53; in-game validation queued on #59) |
| 6 | **Colony rumor** | visitors, at arrival | a random pick over Core's world-level event ledger (`shortTermEvents` / `pawnEventRecords`), capped at 1–2 per visitor | ChitChat | medium | **Heard**, no source pawn (the road told them) | residents | "Heard you took on someone new." · "Heard the raid went badly for the pirates." | built (step 2; in-game validation queued on #59) |
| 7 | **Propagated** | any listener, after being told | the point they were just served | inherits | scaled by a spread factor; `secret` points rarely spread | **Heard**, source = the teller | excludes the teller | "Joseph says he can't sleep since the raid." | planned (step 5, #36) |
| 8 | **Ambient** (last resort) | a pawn whose agenda is empty | weather / "nothing much" — the only surviving reactive pick | ChitChat | lowest | FirstHand | Anyone | "Cold one today." | built (step 2; in-game validation queued on #59) |

### Why Link exists

A visitor with no psychology background has an empty agenda, and a resident has no point *about* them — so outsider pairs would simply fall silent. A link is **half generic, half psychology**:

- the **blank pawn's line** is authored and costs nothing (no memories, no LLM needed to *ask* "how long have you lived here?");
- the **resident's answer** is a normal psychology-voiced line, with a few cheap facts injected so it's about *this* colony.

Those facts are pure C#, resolved at generation time, and are pair-and-role reads — not descriptions of the pawn's own room or clothes:

| Fact | Answers | Read from |
| --- | --- | --- |
| Tenure | "when did you become a citizen?" | time as colonist (Core `Recruited` pivotal memory / records) |
| Arrival | "how did you come here?" | faction + role (`ConversationRole`), captor / host / guest colouring (`RelationshipTone`) |
| What's new | "what's new around here?" | the resident's today-tier memories |
| The place | "how long has this colony been here?" | colony name, settlement age |

A recurring guest who *does* have a Psychology profile gets the full treatment; the bank is the fallback for blank pawns, not the rule. A **new citizen** is a resident whose `Recruited` memory is younger than one quadrum — for that window they are a valid target for the settling-in links, which is also the hook for making recruitment itself a real conversation (#62).

### Register

Chit-chat vs deep talk survive only as a **register** on the point. It is derived at formation from the memory's weight and the pawn's mood, and it steers the prompt's tone. Vanilla `Chitchat` / `DeepTalk` InteractionDefs no longer drive anything.

---

## Lifecycle

`form → match → pregenerate → serve → consume → decay`, plus `propagate`.

| Stage | What happens |
| --- | --- |
| **Form** | The rules above run on a cadence for every conversant pawn (Core `MayConverse`). |
| **Cap** | Keep the top 4 by salience; drop the weakest over cap. |
| **Match** | Pick a listener: in proximity, admitted by `audience`, not already in `toldIds`, and not already holding the same subject. |
| **Pregenerate** | Build the beat from the point (subject, register, stances coloured by psychology + relationship tone), generate the exchange **once**, cache it keyed speaker + listener + point. Prioritised by likelihood of meeting, capped by pool size. |
| **Serve** | When the trigger fires, the pooled conversation plays instantly — **no LLM call at interaction time**. |
| **Consume** | Add the listener to `toldIds`. A point whose whole audience has heard it is removed. |
| **Decay** | Salience falls over time; below threshold or past the retention window (24 h default, 72 h max) it is pruned. Points don't outlive the transcript. |
| **Propagate** | The listener gains a derived `Heard` point (rule 7). This *is* the rumor mill. |

## The trigger

A **psychology / proximity scan** over conversant pawns replaces the vanilla social-interaction hook. Every few seconds each map serves at most one pooled point conversation whose speaker and listener are within 8 cells, both in a **talking mood** (awake, upright, not drafted or broken, able to speak, not mid-combat or in an uninterruptible job — mood itself is *not* a gate, a low mood is exactly when a deep-talk point wants out), neither mid-exchange, and off the pair cooldown (the settings slider). Serving is a cache hit: no LLM call at interaction time. A miss stays silent and nudges pregeneration for one pawn, so the pool converges on who is actually near whom. The register comes from the point, never from a vanilla InteractionDef. The old environmental driver (darkness / freezer remarks) and the personality "initiation / response chance" gates went with the vanilla hook. The warden path (prisoners) and outsider barks keep their own triggers.

## Debug actions

All under the **RimSynapse** debug category; the pawn-targeted ones work headlessly via `run_debug_action` with `pawnName`.

| Action | Proves |
| --- | --- |
| Conversations: Dump agenda | The comp is present and its points are readable (subject, register, salience, provenance, audience, told, age). |
| Conversations: Add sample point (newest memory) | Add / cap / dedupe, seeded from the pawn's newest memory. |
| Conversations: Force-form points | A full formation pass (decay + every rule) for the pawn right now, then the agenda dump. |
| Conversations: Seed colony rumors | Rule 6 on any pawn, from Core's event ledger, with the once-per-visit flag cleared. |
| Conversations: Pregenerate top point | Matches the pawn's strongest point to a listener, logs the built beat, and queues one background generation (result lands async). |
| Conversations: Dump point pool | Every pooled point conversation with its lines, plus how many are in flight. |
| Conversations: Force serve pooled point | Serves the first pooled conversation the pawn speaks in, bypassing range / mood / cooldown: the lines play, offsets apply, the point is consumed for that listener. |
| Conversations: Dump trigger gates | Why the trigger would or wouldn't fire for the pawn right now: the talking-mood gate, then the pair gate for each pooled entry. |
| Force propagate *(step 5)* | Serve a point and show the listener's derived rumor. |

## Build order

1. **Data model** — point, agenda comp, injection, scribe. *Landed.*
2. **Formation** — rules 1–6 and 8 on a cadence; `ConversationLinkDef` + authored bank. *Built; in-game validation pending.*
3. **Pregeneration** — point → beat → pooled conversation. *Built; in-game validation pending.*
4. **Trigger swap** — the psychology / proximity scan replaces the vanilla hook. *Built; in-game validation pending.* The pair-keyed pre-seed pool and event pre-staging folded into point-driven pregeneration here; live generation survives only as a debug force.
5. **Propagation** — rule 7, the rumor mill.
6. **Cliques** — exchange counts per pair (follow-on).

Steps 2–5 change behaviour and are validated in-game before they count as done.
