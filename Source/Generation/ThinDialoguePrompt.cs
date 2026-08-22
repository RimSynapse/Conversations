using System.Linq;
using Verse;
using RimSynapse.Comps;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>A system+user pair for the dialogue call. Splitting them matters: a chat completion with
    /// only a system message and no user turn makes the local model return an empty acknowledgement instead
    /// of generating — the concrete beat has to arrive as the USER message (the WorldNews pattern).</summary>
    public struct ThinPrompt
    {
        public string system;
        public string user;
    }

    /// <summary>
    /// Builds the short, WorldNews-style prompt from a resolved <see cref="ConversationBeat"/>
    /// (Conversations#46): rules and schema in the system message, the concrete beat as the user message.
    /// The model's only job is to phrase the beat, which is what it does well; the heavy per-pawn context
    /// that pushed small models into generic filler is gone (it lives in the agent instead).
    /// </summary>
    public static class ThinDialoguePrompt
    {
        public static ThinPrompt Build(Pawn initiator, Pawn recipient, ConversationBeat beat, string continuationHistory)
        {
            string initName = initiator.Name.ToStringShort;
            string recipName = recipient.Name.ToStringShort;

            string lengthRule = beat.isDeep
                ? "Write a real back-and-forth — 4 to 8 lines, as long as it honestly needs — each line 1-2 heartfelt sentences under 30 words."
                : "Write 2 to 4 short lines, each a single spoken sentence under 20 words.";

            string toneRule;
            switch (beat.tone)
            {
                case BeatTone.Heartfelt:   toneRule = "The mood is close and personal."; break;
                case BeatTone.Coercive:    toneRule = "The mood is cold and controlling — threats and power, NOT friendliness."; break;
                case BeatTone.Negotiating: toneRule = "It's a wary negotiation — each is angling for something."; break;
                default:                   toneRule = "The mood is easy, everyday."; break;
            }

            string system =
                "You write short, natural spoken dialogue between two people on a rimworld colony. " +
                $"{toneRule} {lengthRule} " +
                "Make the two sound like DIFFERENT people and stay concrete about the subject you're given — " +
                "no vague filler, no status-report lines, no restating their mood. " +
                "Do NOT put names, labels, or quotation marks inside the line text. " +
                "Return STRICTLY valid JSON and nothing else: {\"lines\": [\"first line\", \"reply\", \"...\"]}.";

            string framingNote = beat.framing == BeatFraming.InitiatorTells
                ? $" {recipName} was NOT there and is hearing this for the first time — they react to it, they do NOT claim to remember or have witnessed it."
                : "";

            string historyNote = string.IsNullOrEmpty(continuationHistory)
                ? ""
                : $"\n\nThey were just talking; continue naturally and do NOT repeat these lines:\n{continuationHistory}";

            string user =
                $"{initName} — {Identity(initiator)}. Right now: {beat.initiatorStance}.\n" +
                $"{recipName} — {Identity(recipient)}. They respond: {beat.recipientStance}.\n\n" +
                $"What it's about: {beat.subject}.{framingNote}\n\n" +
                $"Write their spoken exchange, alternating and STARTING with {initName}. Return the JSON now.{historyNote}";

            return new ThinPrompt { system = system, user = user };
        }

        /// <summary>A one-line handle on who this pawn is: their authored speaking voice if we have one,
        /// otherwise a trait or two. Deliberately tiny — the beat carries the substance.</summary>
        private static string Identity(Pawn pawn)
        {
            var core = pawn.TryGetComp<SynapseCorePawnComp>();
            if (!string.IsNullOrEmpty(core?.voiceProfile))
            {
                string v = core.voiceProfile.Trim();
                if (v.Length > 140) v = v.Substring(0, 140).TrimEnd() + "…";
                return $"speaks like this: {v}";
            }
            var traits = pawn.story?.traits?.allTraits?.Take(2).Select(t => t.Label).ToList();
            return traits != null && traits.Count > 0 ? string.Join(", ", traits) : "an ordinary colonist";
        }
    }
}
