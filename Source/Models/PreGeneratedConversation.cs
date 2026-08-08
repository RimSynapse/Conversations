using Verse;

namespace RimSynapse.Conversations
{
    /// <summary>
    /// A conversation generated ahead of need and held in the pre-seed pool
    /// (<see cref="SynapseConversationsWorldComponent"/>), so an interaction can be served instantly
    /// instead of stalling on a live LLM call. Scribed so the pool survives save/load.
    /// </summary>
    public class PreGeneratedConversation : IExposable
    {
        public string initiatorId;
        public string recipientId;
        public string initiatorStatement;
        public string recipientResponse;
        public string topicDefName;
        public bool isContinuation;
        public int generatedAtTick;       // TicksGame — drives TTL expiry
        public long generatedAtAbsTick;   // TicksAbs — drives significant-event invalidation vs memory absTick
        public float trustOffset;
        public float familiarityOffset;
        public float affinityOffset;

        public void ExposeData()
        {
            Scribe_Values.Look(ref initiatorId, "initiatorId");
            Scribe_Values.Look(ref recipientId, "recipientId");
            Scribe_Values.Look(ref initiatorStatement, "initiatorStatement");
            Scribe_Values.Look(ref recipientResponse, "recipientResponse");
            Scribe_Values.Look(ref topicDefName, "topicDefName");
            Scribe_Values.Look(ref isContinuation, "isContinuation", false);
            Scribe_Values.Look(ref generatedAtTick, "generatedAtTick", 0);
            Scribe_Values.Look(ref generatedAtAbsTick, "generatedAtAbsTick", 0L);
            Scribe_Values.Look(ref trustOffset, "trustOffset", 0f);
            Scribe_Values.Look(ref familiarityOffset, "familiarityOffset", 0f);
            Scribe_Values.Look(ref affinityOffset, "affinityOffset", 0f);
        }
    }
}
