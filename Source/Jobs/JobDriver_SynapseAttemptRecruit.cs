using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI;
using RimWorld;
using RimSynapse.Comps;

namespace RimSynapse.Conversations
{
    public class JobDriver_SynapseAttemptRecruit : JobDriver
    {
        private Pawn Resident => (Pawn)job.GetTarget(TargetIndex.A).Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(Resident, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOnDowned(TargetIndex.A);
            this.FailOnNotAwake(TargetIndex.A);

            // Path to the resident
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);

            // Talk face-to-face toil
            Toil talkToil = new Toil();
            talkToil.initAction = delegate
            {
                // Force target to wait a brief moment for conversation
                if (Resident.CurJob != null && Resident.CurJob.def != JobDefOf.Wait_Combat)
                {
                    Job waitJob = JobMaker.MakeJob(JobDefOf.Wait_Combat, 300);
                    Resident.jobs.StartJob(waitJob, JobCondition.InterruptForced);
                }
                pawn.rotationTracker.FaceCell(Resident.Position);
                Resident.rotationTracker.FaceCell(pawn.Position);
            };

            talkToil.tickAction = delegate
            {
                pawn.rotationTracker.FaceCell(Resident.Position);
                Resident.rotationTracker.FaceCell(pawn.Position);
            };

            talkToil.defaultCompleteMode = ToilCompleteMode.Delay;
            talkToil.defaultDuration = 300; // 5 seconds in game time (300 ticks)

            talkToil.AddFinishAction(delegate
            {
                if (!Resident.Dead && !pawn.Dead && Resident.Spawned && pawn.Spawned)
                {
                    ExecuteRecruitmentAttempt();
                }
            });

            yield return talkToil;
        }

        private void ExecuteRecruitmentAttempt()
        {
            var comp = Resident.TryGetComp<SynapseCorePawnComp>();
            if (comp == null) return;

            // Set cooldown
            comp.lastRecruitmentAttemptTick = Find.TickManager.TicksGame;

            // Calculate success chance
            int socialLevel = pawn.skills?.GetSkill(SkillDefOf.Social).Level ?? 0;
            int opinion = Resident.relations?.OpinionOf(pawn) ?? 0;
            
            float socialChance = socialLevel * 0.025f;
            float opinionFactor = opinion * 0.003f;
            float relationFactor = 0f;
            if (Resident.Faction != null)
            {
                var rKind = Resident.Faction.RelationWith(Faction.OfPlayer).kind;
                if (rKind == FactionRelationKind.Ally) relationFactor = 0.20f;
                else if (rKind == FactionRelationKind.Hostile) relationFactor = -0.80f;
            }
            float baseChance = Mathf.Clamp(0.10f + socialChance + opinionFactor + relationFactor, 0.01f, 0.99f);
            float finalChance = RimSynapse.Utils.SynapseRecruitmentMath.CalculateRecruitmentChance(pawn, Resident, baseChance);

            float roll = Rand.Value;
            bool success = roll <= finalChance;

            if (success)
            {
                var faction = Resident.Faction;
                var map = Resident.Map;

                // Join Faction
                Resident.SetFaction(Faction.OfPlayer);

                if (map != null && faction != null)
                {
                    bool otherResidentAlive = map.mapPawns.AllPawns
                        .Any(p => p != Resident && p.Faction == faction && p.RaceProps.Humanlike && !p.Dead && RimSynapse.SynapseCoreProviders.IsResident(p));

                    if (!otherResidentAlive)
                    {
                        var thingsToClear = map.listerThings.AllThings
                            .Where(t => t.Faction == faction)
                            .ToList();
                        
                        foreach (var t in thingsToClear)
                        {
                            t.SetFaction(null);
                        }
                        
                        Messages.Message($"The residents of {faction.Name} on this map have all died or been recruited. Their property is now unclaimed.", MessageTypeDefOf.NeutralEvent, true);
                    }
                }
                
                // Send success message/letter
                string msg = $"{Resident.Name.ToStringShort} has been persuaded by {pawn.Name.ToStringShort} to join the colony!\n\nThey have officially packed up their things and became a member of your faction.";
                Find.LetterStack.ReceiveLetter(
                    "Pawn Recruited",
                    msg,
                    LetterDefOf.PositiveEvent,
                    Resident
                );
            }
            else
            {
                // Decrease opinion
                if (Resident.relations != null)
                {
                    var thoughtDef = DefDatabase<ThoughtDef>.GetNamed("RefusedMyProposal", false);
                    if (thoughtDef != null)
                    {
                        Thought_MemorySocial socialThought = (Thought_MemorySocial)ThoughtMaker.MakeThought(thoughtDef);
                        socialThought.opinionOffset = -15;
                        Resident.needs?.mood?.thoughts?.memories?.TryGainMemory(socialThought, pawn);
                    }
                    else
                    {
                        var fallbackDef = DefDatabase<ThoughtDef>.GetNamed("Slighted", false);
                        if (fallbackDef != null)
                        {
                            Thought_MemorySocial socialThought = (Thought_MemorySocial)ThoughtMaker.MakeThought(fallbackDef);
                            socialThought.opinionOffset = -15;
                            Resident.needs?.mood?.thoughts?.memories?.TryGainMemory(socialThought, pawn);
                        }
                    }
                }

                // Check for critical failure (e.g. rolled near 1.0 or very low chance)
                bool critFail = (roll > 0.95f) || (finalChance < 0.15f && Rand.Value < 0.33f);
                if (critFail && Resident.Faction != null)
                {
                    Resident.Faction.TryAffectGoodwillWith(Faction.OfPlayer, -15);
                    string failMsg = $"{Resident.Name.ToStringShort} was offended by {pawn.Name.ToStringShort}'s recruitment pitch! Faction goodwill decreased by 15.";
                    Messages.Message(failMsg, MessageTypeDefOf.CautionInput, true);
                }
                else
                {
                    string failMsg = $"{Resident.Name.ToStringShort} politely declined {pawn.Name.ToStringShort}'s invite to join the colony.";
                    Messages.Message(failMsg, MessageTypeDefOf.NeutralEvent, true);
                }
            }
        }
    }
}
