# Future Features and MCP Architecture: RimSynapse Chat

This document compiles the roadmap, completed features, and backlog items for **RimSynapse Chat**. It outlines how the conversational AI interfaces with RimWorld and details the Model Context Protocol (MCP) tool designs for future features.

---

## 1. Accomplished Features

### Direct Storyteller Dialogue UI - MIGRATING TO CORE
The player-facing storyteller chat window originally shipped inside Conversations
(`StorytellerConversationWindow.cs`, `SynapseConversationsWorldComponent` chat state,
the storyteller-chat MCP handlers, and the `Patch_MapInterface_MapInterfaceOnGUI`
gizmo hook) is being moved to Core. Conversations owns pawn-to-pawn dialogue only.

Tracking issue: RimSynapse/Core#99 (this migration). Design umbrella:
RimSynapse/Core#68 (two-agent Chat + Storyteller architecture).

### Pawn-to-Pawn Contextual Dialogues
*   **Social Hook:** Intercepts vanilla social interactions (`Pawn_InteractionsTracker.TryInteractWith`) via Harmony.
*   **Dialogue Generation:** Queries the LLM to output a 1-sentence, in-character comment matching the interaction type (chitchat, deep talk, insult, etc.), utilizing the pawn's current mood, activity, personal traits, and MBTI/temperament traits.
*   **Silent Non-Responses:** If a pawn is quiet (due to introversion, low opinion, or high medical/trust checks during an insulting spree), they ignore the speak attempt and ellipsis bubbles (`...`) are displayed. Calmly ignoring an insulting spree generates a large positive opinion shift (`RapportBuilt`).
*   **Dialogue Preservation:** Preserves active dialogue history in short-term memory (using the Core mod's settings slider threshold, up to 72 hours) so pawns continue ongoing conversations when they meet again.
*   **Earshot Propagation:** Comments are spoken in visual text bubbles and fed to the speaker, recipient, and the closest bystander within earshot. Range is dynamically reduced by local noise (mining, generators, combat) and blocked by walls (different rooms).

---

## 2. Unimplemented Features (MCP-First Backlog)

To keep the LLM prompt payload minimal and maintain modularity, future features should be designed using an **MCP-first tool-calling architecture** (registering C# queries as tools for the LLM to call on-demand, rather than push-packaging large data packets).

### In-Memory Universe Actions
*   **Description:** Allow LLM conversations to trigger live game-engine events.
*   **MCP-First Tools:**
    *   `trigger_mood_booster`: Takes target pawn ID and thought type to apply temporary thought buffs or debuffs.
    *   `trigger_relationship_shift`: Allows the LLM to shift direct opinions after an intense chat.
    *   `inspire_colonist`: Directs a pawn to perform a relaxing or productive job (e.g. go meditate, play chess, visit a friend).

### Royalty DLC Integration
*   **Description:** Nobles demand formal, structured tone. Telepathic psycast whispers interjected during conversation.
*   **MCP-First Tools:**
    *   `get_royal_demands`: Inspects if a pawn holds noble titles and returns their title constraints, demanded honorifics, and penalties for informal tone.
    *   `trigger_telepathic_thought`: Allows a noble psycaster to telepathically inject thoughts directly into the recipient's mind.

### Ideology DLC Integration
*   **Description:** Dynamic faith debates, conversion attempts, and trust shifts based on precepts and relics.
*   **MCP-First Tools:**
    *   `get_ideoligion_rules`: Exposes the target's ideoligion def name, active precepts, liked/disliked works, and worshiped relics.
    *   `apply_conversion_attempt`: Executes a conversion calculation in the game if the conversation takes a theological debate turn.

### Biotech DLC Integration
*   **Description:** Mechanitor comm links, Sanguophage archaic dialects, and children asking naive questions.
*   **MCP-First Tools:**
    *   `get_xenotype_identity`: Returns gene definitions, xenotype label, and age group (child vs adult) so the LLM adjusts its dialect (archaic, childlike, tech-focused).
    *   `get_mechanitor_status`: Verifies if the pawn is a mechanitor and lists their active mechs, allowing the LLM to speak through mechs.

### Anomaly DLC Integration
*   **Description:** Paranoid, void-induced chatter when studying the monolith or fighting entities. Player can soothe paranoia.
*   **MCP-First Tools:**
    *   `get_void_melancholy`: Returns monolith study progress, void obsession indicators, and active void mental states.
    *   `attempt_mental_soothe`: Checks if a calm talk successfully lowers void panic or prevents a void break state.

### Odyssey (Save Our Ship 2) Integration
*   **Description:** Spatial orbit dialogues, reflecting on home, travel hazards, and space melancholy.
*   **MCP-First Tools:**
    *   `get_orbital_hazards`: Exposes space biomes, active travel parameters, ship structural damage, and space loneliness levels to shape conversation themes.

### Chat History and Interests Tools
*   **Description:** Avoid loading full chats in prompt headers; let the LLM pull logs on-demand.
*   **MCP-First Tools:**
    *   `get_chat_history`: Queries recent chat logs for a specific colonist name.
    *   `get_colonist_interests`: Queries what topics (e.g. skills with high passion) a colonist is interested in discussing.
