using System.Linq;
using Verse;
using RimSynapse.Comps;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>
    /// Builds the short, WorldNews-style prompt from a resolved <see cref="ConversationBeat"/>
    /// (Conversations#46): a concrete subject, each speaker's stance, and one-line identities — nothing
    /// more. The model's only job is to phrase the beat, which is what it does well; the heavy context that
    /// made small models retreat to filler is gone (it lived in the agent instead).
    /// </summary>
    public static class ThinDialoguePrompt
    {
        public static string Build(Pawn initiator, Pawn recipient, ConversationBeat beat)
        {
            string initName = initiator.Name.ToStringShort;
            string recipName = recipient.Name.ToStringShort;
            string initId = Identity(initiator);
            string recipId = Identity(recipient);

            string framingNote = beat.framing == BeatFraming.InitiatorTells
                ? $"\n{recipName} was NOT there and is hearing this for the first time — they react to it, they do NOT claim to remember or have witnessed it themselves."
                : "";

            string lengthRule = beat.isDeep
                ? "Write a real back-and-forth — 4 to 8 lines, as long as it honestly needs — each line 1-2 heartfelt sentences under 30 words."
                : "Write 2 to 4 short lines, each a single spoken sentence under 20 words.";

            string toneRule;
            switch (beat.tone)
            {
                case BeatTone.Heartfelt:  toneRule = "The mood is close and personal."; break;
                case BeatTone.Coercive:   toneRule = "The mood is cold and controlling — threats and power, NOT friendliness."; break;
                case BeatTone.Negotiating:toneRule = "It's a wary negotiation — each is angling for something."; break;
                default:                  toneRule = "The mood is easy, everyday."; break;
            }

            return
                $"Write the spoken exchange between two people.\n\n" +
                $"{initName} — {initId}. Right now: {beat.initiatorStance}.\n" +
                $"{recipName} — {recipId}. They respond: {beat.recipientStance}.\n\n" +
                $"What it's about: {beat.subject}.{framingNote}\n\n" +
                $"{toneRule} {lengthRule} " +
                $"Alternate speakers, STARTING with {initName}. Make them sound like DIFFERENT people and be concrete about what it's about — no vague filler, no status-report lines. " +
                $"Do NOT put names, labels, or quotation marks inside the line text.\n" +
                $"Return strictly valid JSON: {{ \"lines\": [\"{initName}'s line\", \"{recipName}'s reply\", \"...\"] }}";
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
