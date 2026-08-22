using System.Collections.Generic;
using Verse;

namespace RimSynapse.Conversations
{
    public class RimSynapseConversationsSettings : ModSettings
    {
        public List<string> disabledTopicDefNames = new List<string>();
        public bool enablePreGeneratedCaching = true;
        // EXPERIMENTAL: serve conversations from the pre-seed pool. Off by default — the pool can serve
        // stale context (weather, indoor pawns), so live generation is the measured baseline. See #29.
        public bool experimentalPreSeeding = false;
        // Event pre-staging (#35): unlike generic pre-seeding, concrete events don't go stale, so we
        // pre-stage per-pair retellings of recent episodes and fire one with eventPreStageFireChance on
        // each conversation start (a crow attack ends up retold A-B, B-C, A-C, each uniquely). On by default.
        public bool preStageEventConversations = true;
        public float eventPreStageFireChance = 0.30f;
        // Minimum in-game hours between AI-generated conversations for the same pawn pair.
        // Vanilla social interactions fire very frequently; this throttles how often they
        // are upgraded into a full Synapse dialogue so chatter (and the social memories it
        // plants) does not accumulate far faster than intended. 0 = no throttle (legacy behavior).
        public float dialogueCooldownHours = 1f;
        // Load-adaptive shedding (#38): when the LLM can't keep up (a request backlog is building or Core
        // is actively throttling), skip the dialogue generation for a conversation but still apply the
        // code-computed social offsets (#46) so relationships keep evolving for free. A big colony, a slow
        // provider and high game speed all surface the same way — a growing queue — so we gate on real load,
        // not on speed. Bubbles already only render at 1x, so nothing visible is lost.
        public bool adaptiveConversationShedding = true;
        // Shed live generation once the shared LLM queue is deeper than this. Lower = shed sooner (cheaper,
        // less chatter under load); higher = keep generating longer. 0 disables the depth gate.
        public int conversationQueueDepthCap = 4;
        public float bubbleRed = 0.12f;
        public float bubbleGreen = 0.12f;
        public float bubbleBlue = 0.12f;
        public float bubbleAlpha = 0.85f;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref disabledTopicDefNames, "disabledTopicDefNames", LookMode.Value);
            Scribe_Values.Look(ref enablePreGeneratedCaching, "enablePreGeneratedCaching", true);
            Scribe_Values.Look(ref experimentalPreSeeding, "experimentalPreSeeding", false);
            Scribe_Values.Look(ref preStageEventConversations, "preStageEventConversations", true);
            Scribe_Values.Look(ref eventPreStageFireChance, "eventPreStageFireChance", 0.30f);
            Scribe_Values.Look(ref dialogueCooldownHours, "dialogueCooldownHours", 1f);
            Scribe_Values.Look(ref adaptiveConversationShedding, "adaptiveConversationShedding", true);
            Scribe_Values.Look(ref conversationQueueDepthCap, "conversationQueueDepthCap", 4);
            Scribe_Values.Look(ref bubbleRed, "bubbleRed", 0.12f);
            Scribe_Values.Look(ref bubbleGreen, "bubbleGreen", 0.12f);
            Scribe_Values.Look(ref bubbleBlue, "bubbleBlue", 0.12f);
            Scribe_Values.Look(ref bubbleAlpha, "bubbleAlpha", 0.85f);
            if (disabledTopicDefNames == null)
            {
                disabledTopicDefNames = new List<string>();
            }
        }
    }
}
