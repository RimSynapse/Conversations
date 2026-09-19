using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimSynapse.Comps;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>
    /// The cheap facts a pawn can draw on when ANSWERING a link or a colony rumor (#60 §2 "Link"): tenure,
    /// arrival, what's new, the place. Pure C#, resolved at generation time, pair-and-role reads — NOT the
    /// retired colonist self-describers (room / apparel / food). Returns one short clause or null.
    /// </summary>
    public static class LinkContext
    {
        private const int MaxLen = 260;

        public static string FactsFor(Pawn answerer, Pawn asker)
        {
            if (answerer == null) return null;
            var parts = new List<string>();
            var core = answerer.TryGetComp<SynapseCorePawnComp>();
            bool resident = answerer.IsColonist || RimSynapse.SynapseCoreProviders.IsResident(answerer);

            if (resident)
            {
                string tenure = Tenure(answerer, core);
                if (tenure != null) parts.Add(tenure);
                string place = Place();
                if (place != null) parts.Add(place);
            }
            else
            {
                string arrival = Arrival(answerer);
                if (arrival != null) parts.Add(arrival);
            }

            string today = core != null ? ConversationContextResolver.Resolve("memoriesToday", answerer, asker, core) : null;
            if (!string.IsNullOrEmpty(today)) parts.Add("what's new — " + today.Substring(0, 1).ToLowerInvariant() + today.Substring(1));

            if (parts.Count == 0) return null;
            string s = string.Join("; ", parts);
            return s.Length <= MaxLen ? s : s.Substring(0, MaxLen).TrimEnd() + "…";
        }

        /// <summary>"joined the colony N days ago" from the Core Recruited pivotal memory, else "here since the
        /// colony was founded" for a colonist with no join memory.</summary>
        public static string Tenure(Pawn pawn, SynapseCorePawnComp core)
        {
            long nowAbs = Find.TickManager != null ? Find.TickManager.TicksAbs : 0L;
            var joined = core?.memories?.FirstOrDefault(m => m != null && m.memoryType == RimSynapse.SynapsePivotalMemory.Recruited);
            if (joined != null)
            {
                int days = (int)((nowAbs - joined.absTick) / 60000L);
                return days <= 0 ? "joined the colony only today" : days == 1 ? "joined the colony yesterday" : $"joined the colony {DaysPhrase(days)} ago";
            }
            if (pawn.IsColonist) return $"here since the colony was founded, {DaysPhrase(GenDate.DaysPassed)} ago";
            return null;
        }

        /// <summary>Who an outsider is and how they got here: role + faction.</summary>
        public static string Arrival(Pawn pawn)
        {
            string faction = pawn.Faction?.Name ?? pawn.Faction?.def?.label;
            var role = OutsiderLineBank.ResolveRole(pawn);
            string what = pawn.IsQuestLodger() ? "a guest lodging here on a quest"
                : role == OutsiderRole.Trader ? "here with a trade caravan"
                : role == OutsiderRole.Visitor ? "passing through as a visitor"
                : null;
            if (what == null && faction == null) return null;
            return what != null
                ? (faction != null ? $"{what}, from {faction}" : what)
                : $"from {faction}";
        }

        public static string Place()
        {
            string name = Faction.OfPlayer?.Name;
            int days = GenDate.DaysPassed;
            if (string.IsNullOrEmpty(name)) return null;
            return $"this is {name}, settled {DaysPhrase(days)} ago";
        }

        private static string DaysPhrase(int days)
        {
            if (days < 15) return $"{days} day{(days == 1 ? "" : "s")}";
            int quadrums = days / 15;
            if (quadrums < 4) return $"{quadrums} quadrum{(quadrums == 1 ? "" : "s")}";
            int years = days / 60;
            return $"{years} year{(years == 1 ? "" : "s")}";
        }
    }
}
