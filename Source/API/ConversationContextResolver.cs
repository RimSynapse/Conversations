using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;
using RimSynapse.Comps;

namespace RimSynapse.Conversations
{
    /// <summary>
    /// Resolves a topic's <see cref="ChatTopicDef.contextKeys"/> into live prompt text at generation
    /// time, and builds the tiered base context (lean for chit-chat, maximized for deep talk). All reads
    /// are Core-only and side-effect-free, so this is safe to run on the background pre-generation path.
    /// </summary>
    public static class ConversationContextResolver
    {
        private const int TicksPerDay = 60000;

        /// <summary>Resolve every key to text, one per line; empty when nothing applies.</summary>
        public static string ResolveAll(List<string> keys, Pawn pawn, Pawn recipient, SynapseCorePawnComp core)
        {
            if (keys == null || keys.Count == 0) return "";
            var parts = new List<string>();
            foreach (var key in keys)
            {
                string t = Resolve(key, pawn, recipient, core);
                if (!string.IsNullOrEmpty(t)) parts.Add(t);
            }
            return parts.Count > 0 ? string.Join("\n", parts) : "";
        }

        public static string Resolve(string key, Pawn pawn, Pawn recipient, SynapseCorePawnComp core)
        {
            switch (key)
            {
                case "ownedRoom":            return DescribeRoom(pawn);
                case "apparel":              return DescribeApparel(pawn);
                case "health":               return DescribeHealth(pawn);
                case "bondedAnimal":         return DescribeBondedAnimal(pawn);
                case "food":                 return DescribeFood(pawn);
                case "memoriesToday":        return DescribeTodayMemories(core);
                case "memoriesLongTerm":     return DescribeLongTermMemories(core);
                case "griefMemories":        return DescribeTaggedMemories(core, "Loss they carry", "Death", "Died", "Grief");
                case "traumaMemories":       return DescribeTaggedMemories(core, "Scars they carry", "Trauma", "Horror", "TraitShift", "Desensitization");
                case "recipientRelationship":return DescribeRelationship(pawn, recipient);
                case "personalitySummary":   return string.IsNullOrEmpty(core?.personalitySummary) ? null : $"Self-image: {core.personalitySummary}";
                case "residency":            return DescribeResidency(pawn);
                default:                     return null; // unknown key — ignored, so modders can extend freely
            }
        }

        // ── Base tiers ───────────────────────────────────────────────────
        /// <summary>The memory line included in every prompt: today's events for chit-chat, long-standing
        /// burdens for deep talk. Replaces the old "newest memory by tick" (design: 0.7.1 memory tiers).</summary>
        public static string BaseMemoryLine(SynapseCorePawnComp core, bool isDeepTalk)
        {
            return isDeepTalk ? DescribeLongTermMemories(core) : DescribeTodayMemories(core);
        }

        // ── Rooms ────────────────────────────────────────────────────────
        private static string DescribeRoom(Pawn pawn)
        {
            var own = pawn?.ownership;
            if (own == null) return null;
            if (own.AssignedThrone != null)
                return "Quarters: they have their own throne room.";
            var room = own.OwnedRoom;
            if (room == null || room.PsychologicallyOutdoors)
                return "Quarters: no room of their own — a shared barracks or nowhere in particular.";

            string role = room.Role?.label ?? "room";
            float imp = 0f;
            try { imp = room.GetStat(RoomStatDefOf.Impressiveness); } catch { }
            string quality = imp < 20f ? "dreary" : imp < 50f ? "modest" : imp < 120f ? "decent" : "impressive";
            bool shared = false;
            try { shared = room.Owners != null && room.Owners.Count() > 1; } catch { }
            return $"Quarters: a {quality} {role} (impressiveness {imp:F0}){(shared ? ", shared with others" : ", their own")}.";
        }

        // ── Apparel ──────────────────────────────────────────────────────
        private static string DescribeApparel(Pawn pawn)
        {
            var worn = pawn?.apparel?.WornApparel;
            if (worn == null || worn.Count == 0) return "Clothing: wearing nothing notable.";
            var parts = new List<string>();
            foreach (var a in worn.OrderBy(a => a.MaxHitPoints > 0 ? (float)a.HitPoints / a.MaxHitPoints : 1f).Take(3))
            {
                float cond = a.MaxHitPoints > 0 ? (float)a.HitPoints / a.MaxHitPoints : 1f;
                string condStr = cond < 0.35f ? "tattered" : cond < 0.7f ? "worn" : "good condition";
                string q = "";
                var cq = a.TryGetComp<CompQuality>();
                if (cq != null) q = cq.Quality.GetLabel() + " ";
                parts.Add($"{q}{a.def.label} ({condStr})");
            }
            return "Wearing: " + string.Join(", ", parts) + ".";
        }

        // ── Health ───────────────────────────────────────────────────────
        private static string DescribeHealth(Pawn pawn)
        {
            var h = pawn?.health;
            if (h?.hediffSet == null) return null;
            float pain = h.hediffSet.PainTotal;
            int injuries = h.hediffSet.hediffs?.Count(hd => hd is Hediff_Injury) ?? 0;
            string painStr = pain <= 0.001f ? "no pain" : pain < 0.2f ? "minor pain" : pain < 0.4f ? "moderate pain" : "significant pain";
            string inj = injuries > 0 ? $", {injuries} injur{(injuries == 1 ? "y" : "ies")}" : "";
            return $"Health: {painStr}{inj}.";
        }

        // ── Bonded animal ────────────────────────────────────────────────
        private static string DescribeBondedAnimal(Pawn pawn)
        {
            var bond = pawn?.relations?.DirectRelations?.FirstOrDefault(r => r.def == PawnRelationDefOf.Bond && r.otherPawn != null);
            return bond != null ? $"Bonded animal: {bond.otherPawn.LabelShortCap}." : null;
        }

        // ── Food ─────────────────────────────────────────────────────────
        private static string DescribeFood(Pawn pawn)
        {
            var mems = pawn?.needs?.mood?.thoughts?.memories?.Memories;
            string last = null;
            if (mems != null)
            {
                var food = mems.FirstOrDefault(m => m.def.defName.StartsWith("Ate", StringComparison.OrdinalIgnoreCase));
                if (food != null) last = food.def.label ?? food.def.defName;
            }
            float hunger = pawn?.needs?.food?.CurLevelPercentage ?? 1f;
            string h = hunger < 0.25f ? "hungry" : hunger < 0.6f ? "peckish" : "well-fed";
            return $"Food: currently {h}{(last != null ? $"; last meal impression: {last}" : "")}.";
        }

        // ── Memory tiers (0.7.1) ─────────────────────────────────────────
        private static string DescribeTodayMemories(SynapseCorePawnComp core)
        {
            if (core?.memories == null || core.memories.Count == 0) return null;
            long now = Find.TickManager != null ? Find.TickManager.TicksAbs : 0L;
            var today = core.memories
                .Where(m => !string.IsNullOrEmpty(m.summary) && now - m.absTick < TicksPerDay)
                .OrderByDescending(m => m.absTick).Take(2).Select(m => m.summary).ToList();
            return today.Count > 0 ? "Today: " + string.Join("; ", today) : null;
        }

        private static string DescribeLongTermMemories(SynapseCorePawnComp core)
        {
            if (core?.memories == null || core.memories.Count == 0) return null;
            // Read-only tier selection (no reference bump — this may run on the pre-generation thread and
            // the conversation hasn't actually happened yet): long-term / high-salience / high-weight first.
            var top = core.memories
                .Where(m => !string.IsNullOrEmpty(m.summary))
                .OrderByDescending(m => m.isLongTerm)
                .ThenByDescending(m => m.salience)
                .ThenByDescending(m => m.weight)
                .Take(3).Select(m => m.summary).ToList();
            return top.Count > 0 ? "Weighing on them: " + string.Join("; ", top) : null;
        }

        private static string DescribeTaggedMemories(SynapseCorePawnComp core, string label, params string[] tags)
        {
            if (core?.memories == null) return null;
            var seen = new List<string>();
            foreach (var tag in tags)
            {
                foreach (var m in core.GetMemoriesByTag(tag))
                {
                    if (!string.IsNullOrEmpty(m.summary) && !seen.Contains(m.summary)) seen.Add(m.summary);
                }
            }
            var top = seen.Take(2).ToList();
            return top.Count > 0 ? $"{label}: " + string.Join("; ", top) : null;
        }

        // ── Relationship (Core-only: vanilla opinion + relation label) ───
        private static string DescribeRelationship(Pawn pawn, Pawn recipient)
        {
            if (pawn?.relations == null || recipient == null) return null;
            int opinion = pawn.relations.OpinionOf(recipient);
            var rel = pawn.relations.DirectRelations?.FirstOrDefault(r => r.otherPawn == recipient);
            string relStr = rel?.def != null ? rel.def.GetGenderSpecificLabel(recipient) : null;
            return $"Toward {recipient.LabelShort}: opinion {opinion} (-100..100){(relStr != null ? $", {relStr}" : "")}.";
        }

        // ── Residency ────────────────────────────────────────────────────
        private static string DescribeResidency(Pawn pawn)
        {
            bool resident = RimSynapse.SynapseCoreProviders.IsResident(pawn);
            return resident ? "They are a settled resident of this colony." : "They are not a formal resident here.";
        }
    }
}
