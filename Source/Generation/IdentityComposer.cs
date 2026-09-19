using System.Collections.Generic;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>
    /// The PURE identity handle for a speaker — the "who is this" one-liner the thin prompt drops in front of
    /// each pawn's stance. Split out from <see cref="ThinDialoguePrompt"/> so it has ZERO <c>Verse</c>
    /// dependencies and can be exercised by unit tests and the game-free Prompt Lab. The Pawn-reading lives in
    /// <see cref="ThinDialoguePrompt"/>; this only shapes primitives into text.
    ///
    /// This is the exact branch the #44 register work turns on: an authored <paramref name="voiceProfile"/>
    /// wins outright (and may be as clinical as the character warrants); a voiceless pawn is anchored to who
    /// they are and steered to plain speech, rather than collapsing to a bare handle that lets a small model
    /// default to flat, clinical status-report register.
    /// </summary>
    public static class IdentityComposer
    {
        /// <param name="voiceProfile">The pawn's authored speaking voice (Psychology's async voice pipeline), or null/empty.</param>
        /// <param name="backstoryTitle">Adulthood title, falling back to childhood title, or null.</param>
        /// <param name="traitLabels">Up to a couple of the pawn's trait labels; may be null or empty.</param>
        public static string Identity(string voiceProfile, string backstoryTitle, IReadOnlyList<string> traitLabels)
        {
            if (!string.IsNullOrEmpty(voiceProfile))
            {
                string v = voiceProfile.Trim();
                // 220, not 140: Psychology 0.9.2 voiceProfiles end with a sample line of actual speech
                // ("... Might say: «Roof's patched. Next time tell me before it rains.»") — the strongest
                // register anchor the small model gets, so the cap must not cut it off (#44).
                if (v.Length > 220) v = v.Substring(0, 220).TrimEnd() + "…";
                return $"speaks like this: {v}";
            }

            // No authored voice yet — the Psychology voice pipeline is async and can lag or drop out. Rather
            // than collapse every voiceless pawn to the same bare handle (which lets a small model default to
            // a flat, clinical status-report register), anchor them to who they are and steer to plain speech.
            // A real voiceProfile, once it lands, overrides this — and may well be clinical if that fits the
            // character; the plain-speech steer is only the DEFAULT for pawns with nothing to voice them yet
            // (Conversations#44).
            string traits = null;
            if (traitLabels != null && traitLabels.Count > 0)
            {
                var picked = new List<string>();
                for (int i = 0; i < traitLabels.Count && picked.Count < 2; i++)
                {
                    if (!string.IsNullOrEmpty(traitLabels[i])) picked.Add(traitLabels[i]);
                }
                if (picked.Count > 0) traits = string.Join(", ", picked);
            }

            string anchor = string.IsNullOrEmpty(backstoryTitle) ? null : backstoryTitle;
            if (!string.IsNullOrEmpty(traits))
                anchor = string.IsNullOrEmpty(anchor) ? traits : $"{anchor} ({traits})";
            if (string.IsNullOrEmpty(anchor)) anchor = "an ordinary colonist";

            return $"has no fixed way of speaking — voice them as {anchor}, in plain everyday words, not clinical or technical";
        }
    }
}
