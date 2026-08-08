# Changelog

Full version history for RimSynapse - Conversations. The mod page and Workshop description show only the latest release; every earlier version is recorded here.

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
