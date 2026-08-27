using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.AI.Group;
using RimWorld;
using RimWorld.Planet;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>
    /// Resolves an outsider's role and picks one of their authored barks (#52). Outsiders are pawns the
    /// colony has no standing relationship with (Core.MayConverse == false) — raiders, passing traders,
    /// unaffiliated visitors — so they never enter the LLM conversation path; this is their cheap,
    /// deterministic, offline voice. Hybrid: an optional LLM-generated flavor bank (cached at world-gen)
    /// is merged in on top of the authored baseline when present.
    /// </summary>
    public static class OutsiderLineBank
    {
        /// <summary>The outsider role this pawn barks as, or null if they are not an outsider we speak for
        /// (colony-related pawns go through the LLM path; animals and factionless pawns are skipped).</summary>
        public static OutsiderRole? ResolveRole(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead) return null;
            if (pawn.RaceProps == null || !pawn.RaceProps.Humanlike) return null;
            if (RimSynapse.SynapseCoreProviders.MayConverse(pawn)) return null; // ours only if NOT colony-related

            var faction = pawn.Faction;
            if (faction == null || faction.IsPlayer) return null;

            if (faction.HostileTo(Faction.OfPlayer))
                return faction.leader == pawn ? OutsiderRole.RaidLeader : OutsiderRole.RaidMember;

            // Non-hostile: a caravan trader (or its escort travelling to trade) vs a plain visitor.
            if (pawn.trader != null) return OutsiderRole.Trader;
            if (pawn.GetLord()?.LordJob is LordJob_TradeWithColony) return OutsiderRole.Trader;
            return OutsiderRole.Visitor;
        }

        /// <summary>Pick one bark for this pawn, or null if they are not an outsider or no line matches.
        /// The choice is stable for a stretch of time (seeded by pawn id + a slow tick bucket) so a pawn
        /// doesn't flicker between lines frame to frame, but does vary across a long visit.</summary>
        public static string PickLine(Pawn pawn)
        {
            var role = ResolveRole(pawn);
            if (role == null) return null;

            var def = BestDef(pawn, role.Value);
            if (def == null || def.lines.Count == 0) return null;

            // Vary slowly: a new bucket roughly every in-game hour (2500 ticks).
            int bucket = (Find.TickManager?.TicksGame ?? 0) / 2500;
            int idx = Mathf.Abs(pawn.thingIDNumber + bucket) % def.lines.Count;
            return def.lines[idx];
        }

        /// <summary>Most specific matching def for this pawn: faction+meme &gt; faction &gt; meme &gt; role-only.</summary>
        private static OutsiderChatterDef BestDef(Pawn pawn, OutsiderRole role)
        {
            var faction = pawn.Faction;
            OutsiderChatterDef best = null;
            int bestScore = -1;
            var all = DefDatabase<OutsiderChatterDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                var d = all[i];
                if (d.role != role || d.lines == null || d.lines.Count == 0) continue;
                if (!string.IsNullOrEmpty(d.factionDef) && (faction?.def?.defName != d.factionDef)) continue;
                if (!string.IsNullOrEmpty(d.ideoMeme) && !HasMeme(pawn, d.ideoMeme)) continue;

                int score = (!string.IsNullOrEmpty(d.factionDef) ? 2 : 0)
                          + (!string.IsNullOrEmpty(d.ideoMeme) ? 1 : 0);
                if (score > bestScore) { best = d; bestScore = score; }
            }
            return best;
        }

        /// <summary>Whether the pawn's ideoligion carries a meme, guarded for no-Ideology / no-ideo pawns.</summary>
        private static bool HasMeme(Pawn pawn, string memeDefName)
        {
            if (!ModsConfig.IdeologyActive) return false;
            var ideo = pawn.Ideo;
            if (ideo?.memes == null) return false;
            for (int i = 0; i < ideo.memes.Count; i++)
                if (ideo.memes[i]?.defName == memeDefName) return true;
            return false;
        }
    }
}
