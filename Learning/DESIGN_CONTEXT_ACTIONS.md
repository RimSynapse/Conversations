# RimSynapse-Chat — In-Memory Actions Addendum

> **Source:** Ideas extracted from Core context-planning-1 review.
> Chat's universe-changing actions operate through the game engine's runtime
> APIs, not through the save file directly.
>
> **Status:** Design notes for future planning iteration.

---

## 1. In-Memory Universe Actions

Chat allows conversations to produce **game-engine effects**, but these are transient interactions with RimWorld's own systems — Chat doesn't write to Scribe directly.

### Action Model

```csharp
public enum ChatActionType
{
    // Mood / thought effects
    MoodBoost,           // Add a temporary thought to the pawn
    MoodPenalty,         // Add a negative thought

    // Social effects
    OpinionShift,        // Adjust opinion via pawn.relations API
    StartSocialInteraction, // Trigger RimWorld's social interaction system

    // Behavioral effects
    InspireAction,       // Queue a job on the pawn (e.g., go relax, visit someone)
    ChangeIdeoRole,      // Suggest role change (if Ideology DLC)
}
```

### Key Principle: Game Engine Persistence, Not Mod Persistence

When Chat adds a thought to a pawn:
```csharp
// Chat uses RimWorld's own API — the game handles persistence
pawn.needs.mood.thoughts.memories.TryGainMemory(ThoughtDefOf.KindWords);
```

This thought is saved by **RimWorld itself** in its normal save process. Chat doesn't call Scribe. The effect is "in-memory" from Chat's perspective — it interacts with live game objects and lets RimWorld's own systems handle what persists.

### What Chat Does NOT Do

- ❌ Write to `SynapsePawnComp` (that's Psychology's domain)
- ❌ Create narrative threads (that's Storyteller's domain)
- ❌ Modify faction goodwill directly (that's Storyteller's domain)
- ❌ Persist conversation history to save file (kept in-memory, session-scoped)

### What Chat CAN Trigger via Other Mods

If Psychology is loaded, Chat can request that a conversation creates a memory:

```csharp
// After a meaningful conversation, Chat asks Psychology to form a memory
if (SynapsePsychology.IsLoaded)
{
    SynapsePsychology.AddMemory(pawn, new WeightedMemory
    {
        summary = "Had a meaningful conversation with the player about their past",
        memoryType = "social",
        tags = new List<string> { "conversation", "trust" },
        weight = 0.6f,
        baseWeight = 0.6f,
        decayRate = 0.05f,
    });
}
```

This cleanly separates concerns: Chat drives the interaction, Psychology stores the memory.

---

## 2. Conversation Context Requests

When Chat starts a dialogue, it requests context from Core with a lightweight tier selection:

```csharp
// Chat's system prompt focuses on in-character dialogue
var chatHandle = SynapseCore.Register(
    "rimsynapse.chat",
    "RimSynapse Chat",
    "You are role-playing as a colonist in a sci-fi colony. " +
    "Respond in character. Be concise and natural."
);

// Chat requests Tier 1 (identity) + Tier 2 (pawn state) + Tier 5 (AI memories)
chatHandle.DefaultTiers = ContextTierMask.Standard;

// On dialogue start, set context for both pawns
var options = new ChatOptions
{
    eventType = "dialogue",
    // Budget fraction: 50% of available context window
    // Core handles the adaptive budget calculation
};
```

---

## 3. Relationship to Core

```
Core (vanilla+)                      Chat (interaction layer)
────────────────                     ──────────────────────────
Provides context for dialogue        Manages conversation UI & history
Handles LLM request pipeline         Parses structured JSON responses
Budget-trims context automatically   Triggers game-engine effects
                                     Delegates memory creation to Psychology
```

Chat is the **player interaction surface**. It reads from Core's context and writes effects through RimWorld's own APIs (or Psychology's API for memories).
