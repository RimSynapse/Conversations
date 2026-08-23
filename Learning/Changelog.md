# Changelog

Full version history for RimSynapse - Conversations. The mod page and Workshop description show only the latest release; every earlier version is recorded here.

## v0.9.2 - Cleaner dialogue
- Fixed: raw JobDef defNames no longer leak into dialogue. Labelless job defs are now humanized in Core's pawn comp, so colonists describe what they are doing in readable words instead of surfacing internal defNames. The fix lives entirely in Core; this build raises its Core floor to guarantee it is present.
- Requires RimSynapse Core v0.9.1.

## v0.9.1 - Linked Memories
- Fixed: chit-chat and overheard-conversation memories now record who they are about through Core 0.9's canonical memory linkage, so a conversation about a colonist truly consolidates with later events about them (their death, most importantly). Previously these memories were invisible to relational consolidation.
- Requires RimSynapse Core v0.9.0.

## v0.9.0 - Factions and Living Societies
- NEW - Warden conversations: recruiting, converting, enslaving and suppressing a prisoner now play out as spoken exchanges grounded in that prisoner's real situation - resistance, will, comfort, how they were captured, and (with Ideology) the belief clash. Persuasion can build a little rapport; coercion never does. Flavour only - it never changes the vanilla recruitment/conversion roll (#42).
- IMPROVED - Dialogue quality overhaul (#46): an in-code agent picks one concrete beat (a real event, chore or need, plus each speaker's angle) before the model writes it, so colonists talk about actual specifics instead of interchangeable filler. The heavy per-pawn context dump that pushed small models into filler now lives in the agent, not the prompt; rules go in the system message and the concrete beat in the user message. Trust/familiarity/opinion offsets are computed in code for consistency.
- NEW - Load-adaptive conversation shedding (#38): when the shared LLM queue is backed up or Core is throttling (a big colony, a slower model, or high game speed all surface the same way), conversations skip generation instead of piling up a backlog - the code-computed offsets still apply so relationships evolve, you just don't pay for dialogue you can't watch. Bubbles still render at normal speed only.
- CHANGED - The player-to-storyteller chat window has moved to RimSynapse Core (Core #99); Conversations now owns pawn-to-pawn dialogue only.
- Requires Core v0.9.0; saves and settings carry over unchanged.

## v0.8.0 - Event-Driven Talk, Multi-Line Delivery and Voice
- NEW - Event-driven conversations: chit-chat and deep-talk topics are driven by what's actually happening in your colony, with recent events weighted high and conversations pre-staged around notable moments.
- NEW - Single-call multi-line conversations: a whole exchange is generated at once and drip-fed line by line to pawns in range.
- NEW - Conversations honour the Psychology-authored pawn voice, so each colonist sounds like themselves.
- NEW - Two new agent tools: get_chat_history and get_colonist_interests.
- Changed: Universe Actions now flag mutating actions, so autonomous runs respect the mutation gate. Fixed: a no-op Universe Action mood booster now applies.
- Performance: de-batched the per-tick environmental scan and cached reflection lookups.
- Requires Core v0.8.0.

## v0.7.1 - Deeper, more personal conversations
- NEW - 27 conversation topics (up from 6): small talk about weather, food, chores, animals and gossip, and deep talks about grief, regrets, beliefs, trauma, love, mortality and moral lines.
- NEW - Colonists talk about their actual surroundings - the room they sleep in and the clothes they wear become conversation topics (via a data-driven `contextKeys` on each topic def, modder-extensible).
- Conversations now draw on the Core 0.7.1 memory tiers: light recent (today's) events for small talk, long-standing burdens and tag-filtered losses/trauma for heart-to-hearts. Deep talks maximize the context budget (personality summary, health, relationship); small talk stays lean.
- Colonists no longer repeat the same topic back-to-back (per-pair topic history, which also diversifies pre-generation).
- Experimental: an optional pre-seed pool generates conversations ahead of need for instant delivery, invalidated on significant events. Off by default - it can serve stale context (weather, an indoor pawn), so live generation is the measured baseline. Ships with `[CONV-METRIC]` latency/distance instrumentation and debug actions.
- Requires Core v0.7.1; saves and settings carry over unchanged.

## v0.7.0 - Regions and Territories Compatibility
- Moves in step with RimSynapse Core v0.7.0.
- Requires Core v0.7.0; saves and settings carry over unchanged.

## v0.6.1
- Fixed - mod list metadata: the in-game mod list still showed v0.5.2 with no v0.6.0 notes. Version and changelog now agree in every place they are stated.
- Roadmap updated: 0.7 is now Regions and Territories compatibility - the groundwork the Factions work depends on. Everything after it shifts up one release.

## v0.6.0
- Requires RimSynapse Core v0.6.0. This release moves in step with Core's Agent and Tool Foundation update - your saves and settings carry over unchanged.
- Documentation: in-game wiki guides updated; "MCP" renamed to game tools throughout, matching Core's native tool-calling engine.

## v0.5.2
- Maintenance release: no gameplay changes. Version aligned with the rest of the RimSynapse suite, which carries fixes in Core and Psychology.
- Licence: now PolyForm Noncommercial 1.0.0. Free to use, modify and share for any noncommercial purpose.

## v0.5.1
- Visual Speech Bubbles: displays dynamic speech bubbles above talking colonists at normal speed, matching standard RimWorld UI colors and borders.
- Voice Projection Shifting: the bubble shifts to the left or right towards the listener, and the pointer tail anchors directly at the speaker's head.
- Environmental Chitchat Triggers: talkative pawns will comment dynamically on freezing cold (freezer) or dim lighting (darkness) when entering them.
- Settings Customization: configurable background color and transparency sliders for speech bubbles.
- Persuasion and Recruitment: attempt recruitment on neutral resident NPCs via inspect-pane gizmo and three-state social check (Trust, Familiarity, Opinion).
- Chitchat Latency Cache: pre-generates chats in-memory to eliminate dialogue lag at normal speeds.
- Sequential Generation: dialogue is generated one-turn-at-a-time (initiator statement, recipient response) rather than 5 turns bulked.
- Continuation Rebalancing: reduced continuation chance to 20% to prevent topic overlapping.
- Cleaned Up Non-Responses: removed ellipsis ("...") speech bubbles from non-response interactions.
- UI Layout Fixes: fixed text clipping in dialogue history logs and added Discord-style layouts.

## v0.4.0
- Updated to support RimSynapse Core v0.4.0 (Multi-provider routing and Image generation).
