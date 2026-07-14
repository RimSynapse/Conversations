using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimSynapse.Chat
{
    /// <summary>
    /// Stores the active storyteller conversation history in the save game.
    /// </summary>
    public class SynapseChatWorldComponent : WorldComponent
    {
        public List<SynapseChatMessage> chatHistory = new List<SynapseChatMessage>();
        public bool chatWindowOpen;

        public SynapseChatWorldComponent(World world) : base(world)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref chatHistory, "chatHistory", LookMode.Deep);
            Scribe_Values.Look(ref chatWindowOpen, "chatWindowOpen", false);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (chatHistory == null)
                {
                    chatHistory = new List<SynapseChatMessage>();
                }
            }
        }
    }
}
