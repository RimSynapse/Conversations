# RimSynapse-Chat Design Document

## Overview
RimSynapse-Chat provides an in-game dialogue interface, allowing players to converse directly with their colonists. It manages conversation history and parses LLM responses to drive in-game mood and opinion changes.

**Dependencies:** RimSynapse Core (for LLM connectivity and context) and optionally RimSynapse-Psychology (for deeper personality and memory integration).

## Core Features

### 1. In-Game Dialogue UI
*   **Chat Window:** A floating panel or a dedicated tab in RimWorld for initiating and managing conversations.
*   **Message Bubbles:** Visual indicators for pawn replies, optionally displaying thought tags to indicate emotional states.
*   **Opinion Delta Display:** Visual feedback showing how the conversation has impacted the pawn's mood or opinion of the player/other pawns.
*   **Conversation Selector:** UI for selecting which pawn to interact with.

### 2. Structured Dialogue Responses
*   **Expected JSON Format:** The mod expects the LLM to return a specific JSON structure to parse both the dialogue text and the mechanical effects.
    ```json
    {
        "reply": "What the pawn says",
        "thought": {
            "tag": "GRATEFUL",
            "description": "Appreciated the kind words",
            "relation_delta": 5
        },
        "relation_delta": 5
    }
    ```
*   **Validation & Fallback:** Ensures the `reply` field exists. If the LLM returns plain text instead of JSON, the mod provides a fallback mechanism to wrap the raw text into a neutral JSON structure (e.g., tag=`NEUTRAL`, relation_delta=`0`).

### 3. Conversation Management
*   **Pawn Name Tracking:** Extracts and tracks pawn names from prompts and responses using regex patterns (e.g., "You are playing the role of X"). Unused pawn tracking data is pruned after a period of inactivity (e.g., 10 minutes).
*   **Conversation History:** Stores per-pawn conversation threads. This history is appended to subsequent prompts as assistant/user messages, maintaining context over the course of a conversation. The history depth is limited (e.g., last N exchanges) to manage token budgets.

## Integration with Core
The Chat mod uses Core's Context Assembly system to gather the initial state of the pawn (and their environment) before starting a conversation. It utilizes Core's raw/structured inference APIs to send dialogue history and receive structured JSON responses.
