using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimSynapse.Conversations.Patches
{
    [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions))]
    public static class Patch_FloatMenuMakerMap_AddHumanlikeOrders
    {
        public static void Postfix(List<Pawn> selectedPawns, Vector3 clickPos, ref List<FloatMenuOption> __result)
        {
            if (selectedPawns == null || selectedPawns.Count != 1) return;
            Pawn pawn = selectedPawns[0];

            if (pawn == null || pawn.Map == null) return;
            if (pawn.Faction != Faction.OfPlayer) return;
            if (pawn.skills == null || pawn.skills.GetSkill(SkillDefOf.Social).TotallyDisabled) return;
            if (pawn.Downed || pawn.Dead || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Talking)) return;

            IntVec3 c = IntVec3.FromVector3(clickPos);
            Pawn target = c.GetThingList(pawn.Map).OfType<Pawn>().FirstOrDefault(p => p.RaceProps.Humanlike && !p.Dead && p != pawn);
            if (target == null) return;

            var comp = target.TryGetComp<RimSynapse.Comps.SynapseCorePawnComp>();
            if (comp == null || !comp.isResident) return;

            // Check if hostile
            if (target.Faction != null && target.Faction.HostileTo(Faction.OfPlayer))
            {
                return;
            }

            // Cooldown check
            int currentTick = Find.TickManager.TicksGame;
            int ticksSinceAttempt = currentTick - comp.lastRecruitmentAttemptTick;
            int cooldownTicks = 60000; // 1 day
            int remainingTicks = cooldownTicks - ticksSinceAttempt;

            if (remainingTicks > 0)
            {
                float hoursLeft = remainingTicks / 2500f;
                __result.Add(new FloatMenuOption($"Attempt recruitment on {target.Name.ToStringShort} (Cooldown: {hoursLeft:F1}h)", null));
            }
            else
            {
                // Calculate success chance
                int socialLevel = pawn.skills.GetSkill(SkillDefOf.Social).Level;
                int opinion = target.relations?.OpinionOf(pawn) ?? 0;
                
                float socialChance = socialLevel * 0.025f;
                float opinionFactor = opinion * 0.003f;
                float relationFactor = 0f;
                if (target.Faction != null)
                {
                    var rKind = target.Faction.RelationWith(Faction.OfPlayer).kind;
                    if (rKind == FactionRelationKind.Ally) relationFactor = 0.20f;
                    else if (rKind == FactionRelationKind.Hostile) relationFactor = -0.80f;
                }
                float baseChance = Mathf.Clamp(0.10f + socialChance + opinionFactor + relationFactor, 0.01f, 0.99f);
                float finalChance = RimSynapse.Utils.SynapseRecruitmentMath.CalculateRecruitmentChance(pawn, target, baseChance);

                string label = $"Attempt recruitment on {target.Name.ToStringShort} (Success: {finalChance:P0})";
                JobDef jobDef = DefDatabase<JobDef>.GetNamed("SynapseAttemptRecruit", false);
                if (jobDef == null) return;

                __result.Add(new FloatMenuOption(label, () =>
                {
                    Job job = JobMaker.MakeJob(jobDef, target);
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }));
            }
        }
    }
}
