# Testing Plan: RimSynapse - Chat

## Pawn-to-Pawn Dialogues
*   [ ] **Dialogue Interception:** Verify that natural social interactions between colonists trigger single-sentence visual bubbles (`MoteMaker.ThrowText`) instead of vanilla log lines.
*   [ ] **Social Composure:**
    *   Test that introverts or pawns with low opinions frequently choose silence (ellipses `...` bubbles).
    *   Test that a recipient with a high Doctor skill (Medicine level >= 8) remains silent when targeted by a pawn on an insulting spree.
    *   Test that a recipient with high trust (> 20) with the speaker remains silent when targeted by an insulting spree.
    *   Verify that the target remaining silent during an insulting spree triggers a positive `RapportBuilt` memory for the instigator after they calm down.
*   [ ] **Earshot Range and Rooms:**
    *   Test earshot range at 8 cells base distance.
    *   Place active generators or order mining/cutting jobs nearby and verify the range shrinks by 1 cell per source of noise.
    *   Verify that conversations inside a closed room do not propagate to bystanders outside the room.
*   [ ] **Short-Term Memory:**
    *   Initiate a conversation, wait, and verify the pawns retain their conversation thread.
    *   Verify that threads expire and prune once they exceed the tick count configured on the Core settings slider (e.g. 24 hours vs 72 hours).

## UI and Gizmos
*   [ ] **Gizmo:** Select a colonist and verify the **Chat History** button appears on their inspect card.
*   [ ] **History App Window:** Open the history window and check the dual-pane layout:
    *   Left side showing recent contact list with portraits.
    *   Right side displaying messaging bubbles (right-aligned for self, left-aligned for recipient) and relative timestamps.
    *   Test the **Clear** button to purge the selected thread.

## DLC and Action MCP Tools
Verify that the LLM can trigger the following tools successfully and that game effects are applied:
*   [ ] `trigger_mood_booster`: Boosts or penalizes mood; check for cooldown warning if called again within 4 hours.
*   [ ] `trigger_relationship_shift`: Alters opinions; check for cooldown warning if called again within 6 hours.
*   [ ] `inspire_colonist`: Applies an inspiration if mood is high; check for colony-wide cooldown if called again within 24 hours.
*   [ ] `get_royal_demands`: Returns title details (Royalty DLC).
*   [ ] `get_faith_precepts` and `apply_conversion_attempt`: Returns certainty and offsets it (Ideology DLC).
*   [ ] `get_xenotype_identity` and `get_mechanitor_status`: Returns genes and mechs info (Biotech DLC).
*   [ ] `get_void_melancholy` and `attempt_mental_soothe`: Calms void panic (Anomaly DLC).
*   [ ] `get_orbital_hazards`: Exposes spatial coordinates (SOS2).
