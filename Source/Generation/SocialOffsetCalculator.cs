using UnityEngine;
using Verse;
using RimSynapse.Conversations.Generation;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>The social aftermath of a conversation, computed in code (Conversations#46) instead of being
    /// guessed by the LLM. Deterministic from the pair's opinion and the beat's tone, so offsets stay
    /// consistent and the dialogue call can return prose only.</summary>
    public struct SocialOffsets
    {
        public float trust;
        public float familiarity;
        public float affinity;
    }

    public static class SocialOffsetCalculator
    {
        /// <summary>Derive trust/familiarity/affinity from who's talking and the beat's register. Thresholds
        /// line up with ApplyVanillaAffinityThought: a warm heart-to-heart can reach +2 (plants "Chitchat"),
        /// a coercive session reaches -2 (plants "Slight"); everything between plants nothing.</summary>
        public static SocialOffsets Compute(Pawn initiator, Pawn recipient, ConversationBeat beat)
        {
            int opinion = recipient.relations?.OpinionOf(initiator) ?? 0;

            if (beat.IsCoercive)
            {
                // Coercion never warms: no familiarity, trust erodes, resentment lands.
                return new SocialOffsets { trust = -0.6f, familiarity = 0f, affinity = -2f };
            }

            float familiarity = beat.isDeep ? 2f : 1.2f;

            float trust = opinion > 0 ? 0.4f : opinion < 0 ? -0.3f : 0.1f;
            if (beat.isDeep) trust *= 1.5f;

            float affinity = opinion > 40 ? 1.6f : opinion > 0 ? 0.9f : opinion > -20 ? 0.3f : -0.6f;
            if (beat.isDeep) affinity += 0.6f; // opening up bonds

            return new SocialOffsets
            {
                trust = Mathf.Clamp(trust, -2f, 2f),
                familiarity = Mathf.Clamp(familiarity, 0f, 3f),
                affinity = Mathf.Clamp(affinity, -2f, 2f)
            };
        }
    }
}
