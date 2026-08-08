using System.Collections.Generic;
using Verse;

namespace RimSynapse.Conversations
{
    /// <summary>
    /// Stores the short-term active conversation history between two pawns.
    /// </summary>
    public class PawnConversation : IExposable
    {
        public string pawnAId;
        public string pawnBId;
        public List<SynapseConversationMessage> messages = new List<SynapseConversationMessage>();
        public int lastTick;

        /// <summary>Recently-used topic defNames for this pair (most recent last), so consecutive
        /// conversations avoid repeating a topic. Capped ring buffer.</summary>
        public List<string> recentTopics = new List<string>();
        private const int MaxRecentTopics = 4;

        public PawnConversation()
        {
        }

        public PawnConversation(string pawnAId, string pawnBId, int lastTick)
        {
            this.pawnAId = pawnAId;
            this.pawnBId = pawnBId;
            this.lastTick = lastTick;
        }

        /// <summary>Record a topic as most-recently used, de-duplicating and capping the history.</summary>
        public void PushRecentTopic(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return;
            recentTopics.Remove(defName);
            recentTopics.Add(defName);
            while (recentTopics.Count > MaxRecentTopics) recentTopics.RemoveAt(0);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref pawnAId, "pawnAId");
            Scribe_Values.Look(ref pawnBId, "pawnBId");
            Scribe_Collections.Look(ref messages, "messages", LookMode.Deep);
            Scribe_Values.Look(ref lastTick, "lastTick");
            Scribe_Collections.Look(ref recentTopics, "recentTopics", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (messages == null)
                {
                    messages = new List<SynapseConversationMessage>();
                }
                if (recentTopics == null)
                {
                    recentTopics = new List<string>();
                }
            }
        }
    }
}
