using System.Collections.Generic;
using Verse;

namespace RimSynapse.Chat
{
    /// <summary>
    /// Stores the short-term active conversation history between two pawns.
    /// </summary>
    public class PawnConversation : IExposable
    {
        public string pawnAId;
        public string pawnBId;
        public List<SynapseChatMessage> messages = new List<SynapseChatMessage>();
        public int lastTick;

        public PawnConversation()
        {
        }

        public PawnConversation(string pawnAId, string pawnBId, int lastTick)
        {
            this.pawnAId = pawnAId;
            this.pawnBId = pawnBId;
            this.lastTick = lastTick;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref pawnAId, "pawnAId");
            Scribe_Values.Look(ref pawnBId, "pawnBId");
            Scribe_Collections.Look(ref messages, "messages", LookMode.Deep);
            Scribe_Values.Look(ref lastTick, "lastTick");

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (messages == null)
                {
                    messages = new List<SynapseChatMessage>();
                }
            }
        }
    }
}
