using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimSynapse.Conversations
{
    /// <summary>
    /// Stores the active storyteller conversation history in the save game.
    /// </summary>
    public class SynapseConversationsWorldComponent : WorldComponent
    {
        public List<SynapseConversationMessage> chatHistory = new List<SynapseConversationMessage>();
        public List<PawnConversation> pawnConversations = new List<PawnConversation>();
        public bool chatWindowOpen;

        public SynapseConversationsWorldComponent(World world) : base(world)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref chatHistory, "chatHistory", LookMode.Deep);
            Scribe_Collections.Look(ref pawnConversations, "pawnConversations", LookMode.Deep);
            Scribe_Values.Look(ref chatWindowOpen, "chatWindowOpen", false);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (chatHistory == null)
                {
                    chatHistory = new List<SynapseConversationMessage>();
                }
                if (pawnConversations == null)
                {
                    pawnConversations = new List<PawnConversation>();
                }
            }
        }
    }
}
