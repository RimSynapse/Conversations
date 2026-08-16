namespace RimSynapse.Conversations.Generation
{
    /// <summary>Overall register of a beat — steers the thin prompt's tone without a wall of style rules.</summary>
    public enum BeatTone { Casual, Heartfelt, Coercive, Negotiating }

    /// <summary>
    /// Who owns the concrete subject. <see cref="Shared"/> = both participants genuinely lived it, so they
    /// reminisce together. <see cref="InitiatorTells"/> = only the initiator experienced it and the recipient
    /// is hearing it fresh — so the recipient must react, not claim first-hand memory. This is the
    /// Conversations-side guard for the Bill/Lipos relevance bug (see Core#103); until Core exposes a real
    /// involvement API, the resolver infers framing from whether the recipient shares the memory.
    /// </summary>
    public enum BeatFraming { Shared, InitiatorTells }

    /// <summary>
    /// One concrete conversational beat, resolved by the AGENT (C#) rather than left for the LLM to invent
    /// (Conversations#46). It names exactly one thing to talk about and each speaker's angle on it, so the
    /// model's only job is wording — the WorldNews pattern that produces concrete, in-character text instead
    /// of generic filler.
    /// </summary>
    public class ConversationBeat
    {
        /// <summary>The single concrete thing the exchange is about (an event, a chore, a need, an observation).</summary>
        public string subject;

        /// <summary>The initiator's concrete angle on the subject (what they feel / want / are getting at).</summary>
        public string initiatorStance;

        /// <summary>The recipient's concrete angle — their reaction, coloured by opinion and their own state.</summary>
        public string recipientStance;

        public BeatTone tone = BeatTone.Casual;
        public BeatFraming framing = BeatFraming.Shared;
        public bool isDeep;

        /// <summary>Label for recent-topic tracking / metrics (a ChatTopicDef defName, "event:&lt;id&gt;", or a
        /// synthetic tag like "job"/"need"). Never shown to the player.</summary>
        public string topicKey;

        public bool IsCoercive => tone == BeatTone.Coercive;
    }
}
