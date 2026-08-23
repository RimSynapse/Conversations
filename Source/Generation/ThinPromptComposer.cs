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
    /// The PURE core of the thin dialogue prompt (Conversations#46): given the two speakers' names + one-line
    /// identities and a resolved <see cref="ConversationBeat"/>, it produces the exact system+user pair the
    /// game sends. It has ZERO <c>Verse</c> dependencies, so it runs outside RimWorld — that is what lets the
    /// game-free Prompt Lab (rimworld-claude-dev-tools <c>simulate_conversation_prompt</c>) build the SAME
    /// prompt the game does without launching the game.
    ///
    /// Authored ONCE here: <see cref="ThinDialoguePrompt.Build(Verse.Pawn, Verse.Pawn, ConversationBeat, string)"/>
    /// is a thin Pawn adapter that extracts the primitives (names via <c>Name.ToStringShort</c>, identity via
    /// <see cref="IdentityComposer"/>) and calls <see cref="Compose"/>. The lab links THIS file directly, so a
    /// prompt change here changes both the game and the lab — no TS reimplementation to drift out of sync.
    /// </summary>
    public static class ThinPromptComposer
    {
        /// <param name="initName">Initiator's short name (e.g. <c>Pawn.Name.ToStringShort</c>).</param>
        /// <param name="recipName">Recipient's short name.</param>
        /// <param name="initIdentity">Initiator's one-line identity handle (from <see cref="IdentityComposer.Identity"/>).</param>
        /// <param name="recipIdentity">Recipient's one-line identity handle.</param>
        /// <param name="beat">The resolved concrete beat — subject, each speaker's stance, tone, framing, depth.</param>
        /// <param name="continuationHistory">Recent lines to continue from, or null/empty for a fresh exchange.</param>
        public static ThinPrompt Compose(string initName, string recipName, string initIdentity,
            string recipIdentity, ConversationBeat beat, string continuationHistory)
        {
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
                $"{initName} — {initIdentity}. Right now: {beat.initiatorStance}.\n" +
                $"{recipName} — {recipIdentity}. They respond: {beat.recipientStance}.\n\n" +
                $"What it's about: {beat.subject}.{framingNote}\n\n" +
                $"Write their spoken exchange, alternating and STARTING with {initName}. Return the JSON now.{historyNote}";

            return new ThinPrompt { system = system, user = user };
        }
    }
}
