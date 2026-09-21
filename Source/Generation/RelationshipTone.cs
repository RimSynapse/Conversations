using Verse;
using RimWorld;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>
    /// The pairing → tone layer (#53): given two conversation participants, one short clause naming the
    /// relationship between them so the model voices it truthfully — a captor and captive, a host and guest,
    /// two guests from the same people, or two guests from rival peoples all sound different, and colony
    /// peers get nothing so ordinary chatter stays clean. Generalises the captor/captive and host/guest
    /// notes #41 introduced inline. (Raider↔raider morale is the outsider bark path #52, not an LLM pairing.)
    /// </summary>
    public static class RelationshipTone
    {
        /// <summary>The relationship clause for the prompt, or "" when the two are social equals of the colony
        /// (colonist/resident peers) and need no framing.</summary>
        public static string Note(Pawn a, Pawn b)
        {
            if (a == null || b == null) return "";
            string ra = RimSynapse.SynapseCoreProviders.ConversationRole(a);
            string rb = RimSynapse.SynapseCoreProviders.ConversationRole(b);

            // Captor ↔ captive, and captive ↔ captive.
            if ((Captive(ra) && Free(rb)) || (Captive(rb) && Free(ra)))
                return " They are not equals: one belongs to the colony, the other is held by it. This is a wary captor-and-captive relationship, not friendship — let that show in the words and the mood.";
            if (Captive(ra) && Captive(rb))
                return " Both are held by the colony — they share that lot, and can be franker with each other than with their keepers.";

            // Host ↔ guest.
            if (ra == "guest" && Free(rb)) return HostGuestNote(guest: a, host: b);
            if (rb == "guest" && Free(ra)) return HostGuestNote(guest: b, host: a);

            // Guest ↔ guest.
            if (ra == "guest" && rb == "guest") return GuestGuestNote(a, b);

            return ""; // colonist/resident peers
        }

        private static bool Captive(string r) => r == "prisoner" || r == "slave";
        private static bool Free(string r) => r == "colonist" || r == "resident";

        private static string HostGuestNote(Pawn guest, Pawn host)
        {
            // Colour the distance by how the colony and the guest's people actually get on.
            var gf = guest.Faction;
            if (gf != null && !gf.IsPlayer)
            {
                var kind = gf.RelationKindWith(Faction.OfPlayer);
                if (kind == FactionRelationKind.Ally)
                    return " One is of the colony, the other a guest from an allied people — friendly and at ease, though still a visitor.";
                if (kind == FactionRelationKind.Hostile)
                    return " One is of the colony, the other a guest whose people are no friends of theirs — courtesy stretched thin over real wariness.";
            }
            return " One is of the colony, the other a guest passing through — cordial and a little distant, not old friends.";
        }

        private static string GuestGuestNote(Pawn a, Pawn b)
        {
            var fa = a.Faction;
            var fb = b.Faction;
            if (fa != null && fa == fb)
                return " Both are guests here from the same people — an easy, shared-origin familiarity, and someone to talk to in a strange place.";
            if (fa != null && fb != null && fa.HostileTo(fb))
                return " Guests here from rival peoples — under the colony's roof they hold their tongues, but there is no warmth between them.";
            return " Guests here from different peoples — polite strangers making small talk while they wait.";
        }
    }
}
