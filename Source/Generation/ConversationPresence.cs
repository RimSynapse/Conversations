using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimSynapse.Conversations.Comps;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>
    /// Who counts as present for a conversation (#40 multiway gating). Raw distance is only the right gate
    /// OUTDOORS; indoors the unit is the ROOM, and only for pawns who are SETTLED there — doing a stationary
    /// thing (working the room, relaxing, eating), not transiting through. This is what stops a line firing
    /// as a pawn merely walks across a room, and what lets everyone settled in one room (the kitchen/dining
    /// case) be treated as a single conversation instead of a tangle of pairwise ones.
    /// </summary>
    public static class ConversationPresence
    {
        /// <summary>Outdoor fallback radius — matches the drip-feed playback range. Indoors, room membership
        /// replaces this entirely.</summary>
        public const float OutdoorRadius = 8f;

        public static Room RoomOf(Pawn p)
            => p != null && p.Spawned ? p.GetRoom(RegionType.Set_Passable) : null;

        /// <summary>Settled = a real presence in the space, not passing through. True when the pawn is not
        /// pathing, or is pathing to a cell WITHIN its current room (working/relaxing here). A pawn whose
        /// destination is out the door is transiting and does not qualify.</summary>
        public static bool Settled(Pawn p)
        {
            if (p == null || !p.Spawned || p.Dead) return false;
            var pather = p.pather;
            if (pather == null || !pather.Moving) return true;

            Room here = RoomOf(p);
            if (here == null) return false; // moving and roomless (open field in transit) — not settled

            IntVec3 dest = pather.Destination.Cell;
            if (!dest.IsValid || p.Map == null) return false;
            Room destRoom = dest.GetRoom(p.Map);
            return destRoom == here; // staying in this room vs. heading out of it
        }

        /// <summary>Are these two co-present for a conversation? Same indoor room (distance ignored), or —
        /// when outdoors — within <see cref="OutdoorRadius"/>. Both must be settled.</summary>
        public static bool CoPresent(Pawn a, Pawn b)
        {
            if (a == null || b == null || !a.Spawned || !b.Spawned || a.Map != b.Map) return false;
            if (!Settled(a) || !Settled(b)) return false;

            Room ra = RoomOf(a), rb = RoomOf(b);
            bool aIndoors = ra != null && !ra.PsychologicallyOutdoors;
            bool bIndoors = rb != null && !rb.PsychologicallyOutdoors;

            if (aIndoors || bIndoors)
                return ra == rb; // at least one indoors: they must share the room

            // Both outdoors (or roomless-outdoor): fall back to distance.
            return a.Position.DistanceTo(b.Position) <= OutdoorRadius;
        }

        /// <summary>Co-present, settled, in a talking mood pawns who are NOT already in <paramref name="roster"/>
        /// — the candidates who may chime into an active conversation anchored on <paramref name="anchor"/>.</summary>
        public static IEnumerable<Pawn> Bystanders(Pawn anchor, IEnumerable<string> roster, Map map)
        {
            if (anchor == null || map == null) yield break;
            var inRoster = new HashSet<string>(roster ?? Enumerable.Empty<string>());
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p == anchor || inRoster.Contains(p.ThingID)) continue;
                if (p.TryGetComp<SynapseConversationAgendaComp>() == null) continue;
                if (!CoPresent(anchor, p)) continue;
                if (!AgendaTrigger.InTalkingMood(p, out _)) continue;
                yield return p;
            }
        }
    }
}
