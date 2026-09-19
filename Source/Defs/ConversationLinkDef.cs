using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimSynapse.Comps;
using RimSynapse.Conversations.Generation;

namespace RimSynapse.Conversations
{
    /// <summary>
    /// A pawn's role for LINK formation (#60 §2 "Link"). Deliberately coarser than
    /// <c>SynapseCoreProviders.ConversationRole</c>: the link bank is keyed by the role PAIR, and only
    /// pairs that would otherwise fall silent need openers. Prisoners/slaves are <see cref="None"/> —
    /// the warden path (#42) owns those exchanges.
    /// </summary>
    public enum LinkRole
    {
        None,
        /// <summary>A settled colonist / resident.</summary>
        Resident,
        /// <summary>A colonist recruited within the last quadrum — a valid target for "settling in" links.</summary>
        NewCitizen,
        /// <summary>A quest lodger staying with the colony.</summary>
        Guest,
        /// <summary>A non-hostile, non-trading outsider passing through.</summary>
        Visitor,
        /// <summary>A caravan trader or their guards.</summary>
        Trader
    }

    /// <summary>
    /// Authored conversation OPENERS for a role pair and direction (#60 §2, folds #52/#53). A pawn with no
    /// psychology background has an empty agenda, so these are the cheap half of a resident↔outsider
    /// exchange: the speaker's line is authored; the listener's answer is a normal psychology-voiced line.
    /// Several defs may cover the same pair (mods extend freely); one is picked at random.
    /// </summary>
    public class ConversationLinkDef : Def
    {
        public LinkRole speaker;
        public LinkRole listener;
        /// <summary>The authored openers, each a complete line the speaker wants to ask/say.</summary>
        public List<string> lines = new List<string>();
    }

    public static class LinkBank
    {
        /// <summary>One quadrum: how long a recruit counts as a "new citizen" (§9 settled).</summary>
        public const long NewCitizenTicks = 15L * 60000L;

        public static LinkRole RoleOf(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.RaceProps == null || !pawn.RaceProps.Humanlike) return LinkRole.None;
            if (pawn.IsPrisonerOfColony || pawn.IsSlaveOfColony) return LinkRole.None;
            if (pawn.IsColonist || RimSynapse.SynapseCoreProviders.IsResident(pawn))
                return IsNewCitizen(pawn) ? LinkRole.NewCitizen : LinkRole.Resident;
            if (pawn.IsQuestLodger()) return LinkRole.Guest;
            if (pawn.Faction != null && pawn.HostileTo(Faction.OfPlayer)) return LinkRole.None;
            var outsider = OutsiderLineBank.ResolveRole(pawn);
            if (outsider == OutsiderRole.Trader) return LinkRole.Trader;
            if (outsider == OutsiderRole.Visitor) return LinkRole.Visitor;
            return LinkRole.None;
        }

        /// <summary>A resident whose Core "Recruited" pivotal memory is younger than one quadrum. Starting
        /// colonists carry no such memory and are plain residents.</summary>
        public static bool IsNewCitizen(Pawn pawn)
        {
            var core = pawn?.TryGetComp<SynapseCorePawnComp>();
            var joined = core?.memories?.FirstOrDefault(m => m != null && m.memoryType == RimSynapse.SynapsePivotalMemory.Recruited);
            if (joined == null) return false;
            long nowAbs = Find.TickManager != null ? Find.TickManager.TicksAbs : 0L;
            return nowAbs - joined.absTick < NewCitizenTicks;
        }

        /// <summary>Whether this ordered pair is one the bank speaks to at all (roles differ, or resident→new citizen).</summary>
        public static bool IsLinkPair(LinkRole speaker, LinkRole listener)
        {
            if (speaker == LinkRole.None || listener == LinkRole.None) return false;
            if (speaker == listener) return false;
            return true;
        }

        public static ConversationLinkDef FindDef(LinkRole speaker, LinkRole listener)
        {
            if (!IsLinkPair(speaker, listener)) return null;
            return DefDatabase<ConversationLinkDef>.AllDefsListForReading
                .Where(d => d.speaker == speaker && d.listener == listener && d.lines != null && d.lines.Count > 0)
                .RandomElementWithFallback(null);
        }

        public static string PickLine(ConversationLinkDef def)
            => def?.lines != null && def.lines.Count > 0 ? def.lines.RandomElement() : null;
    }
}
