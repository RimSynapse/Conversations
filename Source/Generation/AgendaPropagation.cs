using Verse;
using RimSynapse.Conversations.Comps;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>
    /// Propagation — the rumor mill (#60 Phase 1 step 5, #36). When A serves a point to B, B gains a DERIVED
    /// point: provenance Heard, source = A, same subject, salience scaled by a spread factor. The whole rumor
    /// mill is this one rule applied over the agenda graph — there is no separate system. A rumor is never
    /// retold to its source (<see cref="TalkingPoint.Accepts"/>), a point about X is never told to X even
    /// second-hand (the Not audience carries over), and secrets rarely spread.
    /// </summary>
    public static class AgendaPropagation
    {
        /// <summary>Second-hand salience = first-hand × this. Chains decay geometrically (0.8 → 0.48 → 0.29 → …).</summary>
        public const float SpreadFactor = 0.6f;
        /// <summary>A secret is confided on, not repeated: chance the listener re-spreads it at all.</summary>
        public const float SecretSpreadChance = 0.15f;
        /// <summary>Below this a rumor isn't worth carrying (a chain dies here, not at the cap).</summary>
        public const float MinDerivedSalience = 0.12f;

        /// <summary>Which points are rumor material: things that happened, shifts between people, and rumors
        /// themselves. Not openers (Only audience), not "been fixing the cooler", not the weather.</summary>
        public static bool Propagates(TalkingPoint point)
        {
            if (point == null || point.audience == AudienceKind.Only) return false;
            switch (AgendaBeatBuilder.Kind(point))
            {
                case "memory":
                case "bond":
                case "rumor":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Hook for the serve path: the listener gains a derived point. Returns it, or null when
        /// nothing propagated. <paramref name="roll"/> is the secret-spread roll (default: random).</summary>
        public static TalkingPoint OnServed(Pawn speaker, Pawn listener, TalkingPoint point, int nowTick, float roll = -1f)
        {
            var agenda = listener?.TryGetComp<SynapseConversationAgendaComp>();
            if (agenda == null || speaker == null || point == null) return null;
            return Derive(speaker.ThingID, point, agenda, nowTick, roll < 0f ? Rand.Value : roll);
        }

        /// <summary>Pure derivation over the comps (testable without pawns).</summary>
        public static TalkingPoint Derive(string tellerId, TalkingPoint point, SynapseConversationAgendaComp listenerAgenda, int nowTick, float roll)
        {
            if (listenerAgenda == null || string.IsNullOrEmpty(tellerId) || !Propagates(point)) return null;
            if (point.secret && roll >= SecretSpreadChance) return null;

            float salience = point.salience * SpreadFactor;
            if (salience < MinDerivedSalience) return null;

            // They already carry it (they were told, or they lived it), or they've recently let it go.
            if (listenerAgenda.HasSubject(point.subjectMemId) || listenerAgenda.IsRetired(point.subjectMemId)) return null;

            var derived = new TalkingPoint
            {
                subjectMemId = point.subjectMemId,
                subjectSummary = point.subjectSummary, // v1: no drift; the beat frames it as second-hand
                register = point.register,
                salience = salience,
                provenance = PointProvenance.Heard,
                sourcePawnId = tellerId,
                secret = point.secret,
                formedTick = nowTick,
                // Keep "never say it TO X"; the teller exclusion is enforced by Accepts via sourcePawnId.
                audience = point.audience == AudienceKind.Not ? AudienceKind.Not : AudienceKind.Anyone,
                audiencePawnId = point.audience == AudienceKind.Not ? point.audiencePawnId : null
            };
            return listenerAgenda.Add(derived) ? derived : null;
        }
    }
}
