using System.Collections.Generic;
using Verse;

namespace RimSynapse.Conversations
{
    /// <summary>One spoken line of a pooled exchange, with who says it (ThingID). Additive (#60 step 3) so a
    /// pooled point conversation can carry the whole multi-line exchange, not just the first pair.</summary>
    public class PooledLine : IExposable
    {
        public string speakerId;
        public string text;

        public PooledLine() { }
        public PooledLine(string speakerId, string text) { this.speakerId = speakerId; this.text = text; }

        public void ExposeData()
        {
            Scribe_Values.Look(ref speakerId, "speakerId");
            Scribe_Values.Look(ref text, "text");
        }
    }

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
        /// <summary>Talking point this exchange was pregenerated for (#60 §1) — the pool key becomes
        /// (speaker, listener, pointId). Null for legacy topic-keyed entries.</summary>
        public string pointId;
        /// <summary>The point's register at generation; drives the pool TTL (deep talk keeps longer).</summary>
        public bool isDeep;
        /// <summary>The full exchange (#60 step 3). <see cref="initiatorStatement"/>/<see cref="recipientResponse"/>
        /// mirror the first two lines for the legacy two-line pool path. Null for legacy entries.</summary>
        public List<PooledLine> lines;
        // Event anchoring (#35): non-null marks this as a pre-staged retelling of a specific episode.
        // eventKey is the source EventReflection memory key so a pair tells a given event only once.
        public string eventKey;
        public string eventSummary;
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
            Scribe_Values.Look(ref pointId, "pointId");
            Scribe_Values.Look(ref isDeep, "isDeep", false);
            Scribe_Collections.Look(ref lines, "lines", LookMode.Deep);
            Scribe_Values.Look(ref eventKey, "eventKey");
            Scribe_Values.Look(ref eventSummary, "eventSummary");
            Scribe_Values.Look(ref isContinuation, "isContinuation", false);
            Scribe_Values.Look(ref generatedAtTick, "generatedAtTick", 0);
            Scribe_Values.Look(ref generatedAtAbsTick, "generatedAtAbsTick", 0L);
            Scribe_Values.Look(ref trustOffset, "trustOffset", 0f);
            Scribe_Values.Look(ref familiarityOffset, "familiarityOffset", 0f);
            Scribe_Values.Look(ref affinityOffset, "affinityOffset", 0f);
        }
    }
}
