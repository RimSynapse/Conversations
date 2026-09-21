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
        /// Merges the authored baseline (<see cref="OutsiderChatterDef"/>) with any cached LLM flavor for this
        /// role+faction, and lazily kicks off flavor generation for next time. The choice is stable for a
        /// stretch of time (seeded by pawn id + a slow tick bucket) so a pawn doesn't flicker frame to frame,
        /// but does vary across a long visit.</summary>
        public static string PickLine(Pawn pawn)
        {
            var role = ResolveRole(pawn);
            if (role == null) return null;

            var def = BestDef(pawn, role.Value);
            var authored = def?.lines;

            string key = FlavorKey(pawn, role.Value);
            var worldComp = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            var flavor = worldComp?.GetOutsiderFlavor(key);

            // Hybrid: kick off one-time LLM flavor generation for this bucket; this call uses what's on hand.
            EnsureFlavor(pawn, role.Value, key, worldComp);

            int total = (authored?.Count ?? 0) + (flavor?.Count ?? 0);
            if (total == 0) return null;

            int bucket = (Find.TickManager?.TicksGame ?? 0) / 2500;
            int idx = Mathf.Abs(pawn.thingIDNumber + bucket) % total;
            if (authored != null && idx < authored.Count) return authored[idx];
            return flavor[idx - (authored?.Count ?? 0)];
        }

        private static string FlavorKey(Pawn pawn, OutsiderRole role)
            => role + "|" + (pawn.Faction?.def?.defName ?? "none");

        /// <summary>Lazily generate a small flavor bank for this role+faction once (hybrid layer 3). Uses the
        /// LLM if a backend is configured; on success the lines are cached in the world component and reused
        /// forever. If generation never runs or fails, the authored baseline stands in — a pawn is never
        /// without a line.</summary>
        private static void EnsureFlavor(Pawn pawn, OutsiderRole role, string key, SynapseConversationsWorldComponent worldComp)
        {
            if (worldComp == null || pawn.Faction == null) return;
            if (worldComp.FlavorRequestedOrCached(key)) return;
            worldComp.MarkFlavorRequested(key);

            string factionName = pawn.Faction.Name ?? pawn.Faction.def?.label ?? "an unknown faction";
            string ideoHint = "";
            if (ModsConfig.IdeologyActive && pawn.Ideo?.memes != null && pawn.Ideo.memes.Count > 0)
            {
                var names = new List<string>();
                for (int i = 0; i < pawn.Ideo.memes.Count && names.Count < 3; i++)
                    if (!string.IsNullOrEmpty(pawn.Ideo.memes[i]?.label)) names.Add(pawn.Ideo.memes[i].label);
                if (names.Count > 0) ideoHint = $" Their people believe in: {string.Join(", ", names)}.";
            }

            string system =
                "You write very short spoken barks a RimWorld pawn mutters to themselves or a nearby comrade. " +
                "Plain, in-character, everyday spoken words — NOT clinical or technical. One sentence each, under 15 words. " +
                "Return STRICTLY valid JSON and nothing else: {\"lines\": [\"...\", \"...\", \"...\", \"...\"]}.";
            string user = $"Write 4 barks for {RoleBlurb(role)} from the {factionName} faction.{ideoHint}";

            RimSynapse.SynapseClient.PromptAsync(
                RimSynapseConversationsMod.ModHandle,
                system, user,
                result =>
                {
                    if (!result.success || string.IsNullOrEmpty(result.content)) return;
                    var lines = ParseLines(result.content);
                    if (lines == null || lines.Count == 0) return;
                    SynapseGameComponent.Enqueue(() =>
                    {
                        var wc = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
                        wc?.StoreOutsiderFlavor(key, lines);
                        RimSynapse.SynapseLogger.Info("conversations", $"[#52] Cached {lines.Count} LLM flavor line(s) for {key}.");
                    });
                },
                new RimSynapse.ChatOptions { priority = 5, requestName = "Outsider flavor", targetName = key });
        }

        private static string RoleBlurb(OutsiderRole role)
        {
            switch (role)
            {
                case OutsiderRole.RaidLeader: return "the leader of a raiding party staging an attack on a small frontier colony — command, patience, menace";
                case OutsiderRole.RaidMember: return "a fighter in a raiding party waiting to attack a small frontier colony — impatience, greed, or contempt for the defenders";
                case OutsiderRole.Trader:     return "a caravan trader visiting a small frontier colony to deal — practical, a little weary from the road";
                default:                      return "a traveler passing through a small frontier colony, wanting no trouble";
            }
        }

        private static List<string> ParseLines(string content)
        {
            try
            {
                int a = content.IndexOf('{');
                int b = content.LastIndexOf('}');
                if (a < 0 || b <= a) return null;
                var jo = Newtonsoft.Json.Linq.JObject.Parse(content.Substring(a, b - a + 1));
                var arr = jo["lines"] as Newtonsoft.Json.Linq.JArray;
                if (arr == null) return null;
                var outLines = new List<string>();
                foreach (var t in arr)
                {
                    string s = t?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(s)) outLines.Add(s);
                }
                return outLines;
            }
            catch { return null; }
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
