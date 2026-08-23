using System.Collections.Generic;
using System.Linq;
using Verse;
using RimSynapse.Comps;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>
    /// The thin <c>Pawn</c> adapter over <see cref="ThinPromptComposer"/> (Conversations#46): it reads live
    /// pawn state — names and each speaker's identity handle — and hands those primitives to the pure composer,
    /// which authors the actual system+user prompt. Keeping the templating in the Verse-free composer is what
    /// lets the game-free Prompt Lab build the SAME prompt without launching RimWorld.
    ///
    /// <see cref="ThinPrompt"/> now lives in <see cref="ThinPromptComposer"/>.
    /// </summary>
    public static class ThinDialoguePrompt
    {
        public static ThinPrompt Build(Pawn initiator, Pawn recipient, ConversationBeat beat, string continuationHistory)
        {
            return ThinPromptComposer.Compose(
                initiator.Name.ToStringShort,
                recipient.Name.ToStringShort,
                PawnIdentity(initiator),
                PawnIdentity(recipient),
                beat,
                continuationHistory);
        }

        /// <summary>Extract the identity primitives from a pawn (authored voice, backstory title, trait labels)
        /// and shape them into the one-line handle via the pure <see cref="IdentityComposer"/>.</summary>
        private static string PawnIdentity(Pawn pawn)
        {
            string voiceProfile = pawn.TryGetComp<SynapseCorePawnComp>()?.voiceProfile;
            string backstoryTitle = pawn.story?.Adulthood?.title ?? pawn.story?.Childhood?.title;
            List<string> traitLabels = pawn.story?.traits?.allTraits?.Take(2).Select(t => t.Label).ToList();
            return IdentityComposer.Identity(voiceProfile, backstoryTitle, traitLabels);
        }
    }
}
