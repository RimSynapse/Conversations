using System.Collections.Generic;
using Verse;

namespace RimSynapse.Conversations
{
    public class RimSynapseConversationsSettings : ModSettings
    {
        public List<string> disabledTopicDefNames = new List<string>();
        public bool enablePreGeneratedCaching = true;
        // Minimum in-game hours between AI-generated conversations for the same pawn pair.
        // Vanilla social interactions fire very frequently; this throttles how often they
        // are upgraded into a full Synapse dialogue so chatter (and the social memories it
        // plants) does not accumulate far faster than intended. 0 = no throttle (legacy behavior).
        public float dialogueCooldownHours = 1f;
        public float bubbleRed = 0.12f;
        public float bubbleGreen = 0.12f;
        public float bubbleBlue = 0.12f;
        public float bubbleAlpha = 0.85f;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref disabledTopicDefNames, "disabledTopicDefNames", LookMode.Value);
            Scribe_Values.Look(ref enablePreGeneratedCaching, "enablePreGeneratedCaching", true);
            Scribe_Values.Look(ref dialogueCooldownHours, "dialogueCooldownHours", 1f);
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
