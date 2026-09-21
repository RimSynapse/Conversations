using System.Collections.Generic;
using Verse;

namespace RimSynapse.Conversations
{
    public class RimSynapseConversationsSettings : ModSettings
    {
        // (Retired in #60 step 4: experimentalPreSeeding, preStageEventConversations, eventPreStageFireChance.
        // The pair-keyed pre-seed pool and event pre-staging folded into point-driven pregeneration, which
        // is always on — it IS how conversations happen now. Old settings files carry the keys harmlessly.)

        // Minimum in-game hours between served conversations for the same pawn pair (#60 trigger). The
        // agenda trigger scans every few seconds; this throttles how often a given pair actually talks so
        // chatter (and the social memories it plants) does not accumulate faster than intended. 0 = no throttle.
        public float dialogueCooldownHours = 1f;
        // Load-adaptive shedding (#38): when the LLM can't keep up (a request backlog is building or Core
        // is actively throttling), hold off background pregeneration. A big colony, a slow provider and
        // high game speed all surface the same way — a growing queue — so we gate on real load, not speed.
        public bool adaptiveConversationShedding = true;
        // Hold off pregeneration once the shared LLM queue is deeper than this. Lower = shed sooner (cheaper,
        // less chatter under load); higher = keep generating longer. 0 disables the depth gate.
        public int conversationQueueDepthCap = 4;
        // Warden conversations (#42): upgrade vanilla warden work (recruit / reduce resistance, ideology
        // conversion, enslavement, slave suppression) into a spoken exchange. Warden toils repeat very
        // frequently, so this has its own cooldown separate from ambient chatter.
        public bool enableWardenConversations = true;
        public float wardenConversationCooldownHours = 2f;
        public float bubbleRed = 0.12f;
        public float bubbleGreen = 0.12f;
        public float bubbleBlue = 0.12f;
        public float bubbleAlpha = 0.85f;
        // How long pawn-to-pawn conversation history is kept before it is actively pruned from the save
        // (default 24 in-game hours, capped at 72). Distinct from Core's shared short-term memory window;
        // this governs only the Conversations transcript. Also the window within which a fresh interaction
        // continues the existing thread instead of starting a new one.
        public const float MinHistoryHours = 6f;
        public const float MaxHistoryHours = 72f;
        public float conversationHistoryHours = 24f;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref dialogueCooldownHours, "dialogueCooldownHours", 1f);
            Scribe_Values.Look(ref adaptiveConversationShedding, "adaptiveConversationShedding", true);
            Scribe_Values.Look(ref conversationQueueDepthCap, "conversationQueueDepthCap", 4);
            Scribe_Values.Look(ref enableWardenConversations, "enableWardenConversations", true);
            Scribe_Values.Look(ref wardenConversationCooldownHours, "wardenConversationCooldownHours", 2f);
            Scribe_Values.Look(ref bubbleRed, "bubbleRed", 0.12f);
            Scribe_Values.Look(ref bubbleGreen, "bubbleGreen", 0.12f);
            Scribe_Values.Look(ref bubbleBlue, "bubbleBlue", 0.12f);
            Scribe_Values.Look(ref bubbleAlpha, "bubbleAlpha", 0.85f);
            Scribe_Values.Look(ref conversationHistoryHours, "conversationHistoryHours", 24f);
        }
    }
}
