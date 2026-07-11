# RimChat Inspirations - Chat Module

### Feature 1
**Rimchat Feature:** Async Chat Service (API Parsers)
**RimSynapse Feature:** Core handles the raw HTTP, but Chat needs a dedicated prompt builder.
**AI Suggestion:** Write a fresh `ChatPromptBuilder` that pulls short-term history and formats it for RimSynapse-Core.
**User Input:** I agree, but the context should also include chat history, but only session based, meaning if you end a chat and start a new one, the new one should not have the context of the old one unless its within one game hour.  If the player promises a pawn something, that should get immediately recorded into long term memory.  This is Colony context, this will feed into whether or not the pawn trusts the player.  If the player lies to the pawn, it should also get recorded into long term memory.

Lastly, the pawns current status (it's dark, I ate insect meat, and other current pawn modifiers should be included in the prompt).  We need to find a way to keep this as small as possible so the context window of llms get too bogged down in the prompt.  We want it to be as responsive as possible. I"m looking for ideas here.

### Feature 2
**Rimchat Feature:** Live Context Tracker
**RimSynapse Feature:** <missing>
**AI Suggestion:** Create a `SessionContext` class that holds the rolling window of the current conversation (last N messages).
**User Input:** I think I answered this in feature 1.

### Feature 3
**Rimchat Feature:** Pawn-to-Pawn & Player-to-Pawn Interactions
**RimSynapse Feature:** <missing>
**AI Suggestion:** Write a new harmony patch on Pawn social interactions to trigger a live chat session when the player initiates it.
**User Input:** while pawn to pawn is cool, i think the player being involved is a future feature.  We will address this later.

### Feature 4
**Rimchat Feature:** Chat UI Overlays & History Viewer
**RimSynapse Feature:** <missing>
**AI Suggestion:** Build a fresh, clean RimWorld UI Window (`Dialog_SynapseChat`) rather than porting the old, tangled RimChat UI code.
**User Input:** Agree.  No need to reinvent the wheel here, but we can do it better than the original.  Let's start with the new clean window.  

## Expansion Inspirations

### Feature 5: Noble Demands & Psycast Whispers (Royalty)
**Source Credit:** Vanilla Expanded Empire / Vanilla Royalty
**RimSynapse Feature:** <missing>
**AI Suggestion:** Nobles demand specific tones in chat; addressing a Stellarch informally loses trust. Psycasters randomly interject telepathic thoughts during a live chat.

### Feature 6: Proselytizing & Faith Debates (Ideology)
**Source Credit:** Vanilla Ideology Expanded
**RimSynapse Feature:** <missing>
**AI Suggestion:** Pawns actively try to proselytize to the player or other pawns during chat. Agreeing with their precepts builds trust; insulting their relics destroys it.

### Feature 7: Xenotype Dialects & Mechanitor Links (Biotech)
**Source Credit:** Vanilla Races Expanded / Biotech
**RimSynapse Feature:** <missing>
**AI Suggestion:** Mechanitors prefer chatting through their mechs or via comms. Sanguophages use ancient, haughty dialects. Children have unique, naive prompts and ask the player about the world.

### Feature 8: Void Whispers & Paranoia (Anomaly)
**Source Credit:** Anomaly DLC
**RimSynapse Feature:** <missing>
**AI Suggestion:** Pawns studying the monolith or attacked by entities have paranoid, erratic chat options. The player can attempt to "soothe" a pawn driven mad by the void through careful dialogue choices.

### Feature 9: The Journey Ahead (Odyssey)
**Source Credit:** Save Our Ship 2 / RimNauts
**RimSynapse Feature:** <missing>
**AI Suggestion:** While journeying across the world or stars, pawns discuss the stars, the journey ahead, and long-term survival in space or on the road, reflecting on the home they left behind.
