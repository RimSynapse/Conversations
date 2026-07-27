# Pawn Dialogues and Game Tools

This page details the Pawn-to-Pawn Dialogue System and the game tools implemented in **RimSynapse - Chat**.

---

## 1. Pawn-to-Pawn Dialogue System

When pawns attempt to socialize in-game, RimSynapse intercepts the interaction and routes it through our LLM prompt-generation pipeline:

### Dynamic Interception and Prompting
*   **Harmony Hook:** Intercepts `Pawn_InteractionsTracker.TryInteractWith` to block vanilla text generation and run our AI dialogue model instead.
*   **Context Gathering:** The system inspects both the speaker and recipient, pulling active activities, mood percentages, personal traits, and MBTI or temperament traits from their psychology profiles.
*   **Single-Sentence Dialogue:** The model produces exactly one natural, conversational, in-character comment matching the vanilla interaction type (chitchat, deep talk, insult, etc.).

### Social Composure and Non-Responses
Introverted pawns, or those with very low opinions of the speaker, are likely to ignore conversations.
*   **Silent Non-Responses:** Ignored pawns display ellipsis bubbles (`...`) instead of dialogue. Responding to or ignoring chitchat applies minor social memories like `Slighted` or `Chitchat`.
*   **Insulting Spree Composure:** If the initiator is in a mental state on an insulting spree:
    *   **Doctors** (Medicine level >= 8) remain professional and stay silent (response chance reduced by 75%).
    *   **Trusted Colonists** (Trust > 20, queried dynamically from Psychology) stay calm and silent (response chance reduced by 70%).
    *   If the target remains calm and silent, the insulting pawn gains a large positive opinion shift (`RapportBuilt`) once they snap out of their break.

### Earshot Propagation and Noise Calculations
*   **Dialogue Propagation:** Comments are written to local text bubbles and recorded in both the speaker's and recipient's memories.
*   **Bystanders:** The closest colonist within earshot hears the comment and registers an `overheard` memory.
*   **Noise Mechanics:** The base earshot range is 8 cells, which is reduced by 1 cell for every local noise-making task (mining, deconstructing, plant cutting, harvesting) or active generator in the area.
*   **Enclosed Rooms:** Sounds do not propagate through walls. Noise originating in a different room is blocked unless both pawns are outdoors.

---

## 2. Chat History UI

Players can track ongoing conversations between their colonists via a personal messaging interface:

*   **Pawn Gizmo Command:** Click the new **Chat History** button on any player colonist inspect pane to open the history dialogue.
*   **Split-Pane Layout:**
    *   **Left Sidebar:** Shows all contacts the selected pawn has talked to during the active memory period (up to 72 hours, matching the Core settings slider), complete with colonist portraits.
    *   **Right Message Feed:** Displays standard messaging bubbles aligned to the left or right, styled after personal chat applications.
*   **Relative Timestamps:** Every message features a relative time indicator (e.g. `"Just now"`, `"1.5h ago"`, or `"2.0d ago"`).
*   **Thread Management:** Includes a **Clear** button at the bottom of the dialogue to purge the active thread's short-term history.

---

## 3. Game Tools (DLC and Balanced Actions)

Conversational LLMs can call game tools registered with Core's native tool-calling engine on-demand, enabling balanced interventions and deep DLC context:

### Balanced Universe Actions
*   `trigger_mood_booster`: Applies a temporary mood buff (`KindWords`) or debuff (`Slighted`). Capped at once per 4 hours per colonist.
*   `trigger_relationship_shift`: Adjusts opinion by up to +/-15 using vanilla memories. Cooldown is once per 6 hours per pair.
*   `inspire_colonist`: Triggers a vanilla inspiration on happy pawns (mood above major break risk). Cooldown is once per game day across the colony.

### Royalty DLC
*   `get_royal_demands`: Returns noble titles, active decrees, and bed/throneroom requirements.

### Ideology DLC
*   `get_faith_precepts`: Returns active ideoligion precepts, role titles, and certainty levels.
*   `apply_conversion_attempt`: Executes standard vanilla certainty reduction calculations.

### Biotech DLC
*   `get_xenotype_identity`: Returns gene lists, age group, and custom xenotypes.
*   `get_mechanitor_status`: Returns mechanitor bandwidth and list of active mechs.

### Anomaly DLC
*   `get_void_melancholy`: Returns monolith study progress and void obsession hediffs.
*   `attempt_mental_soothe`: Runs a social check to calm panic or void mental break states.

### Odyssey (Save Our Ship 2)
*   `get_orbital_hazards`: Exposes spatial coordinates and ship systems damage.
