using System.Collections.Generic;
using Verse;

namespace RimSynapse.Conversations
{
    public class RimSynapseConversationsSettings : ModSettings
    {
        public List<string> disabledTopicDefNames = new List<string>();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref disabledTopicDefNames, "disabledTopicDefNames", LookMode.Value);
            if (disabledTopicDefNames == null)
            {
                disabledTopicDefNames = new List<string>();
            }
        }
    }
}
