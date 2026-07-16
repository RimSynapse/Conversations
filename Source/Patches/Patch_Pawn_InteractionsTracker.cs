using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;
using RimWorld;
using RimSynapse.Comps;

namespace RimSynapse.Conversations.Patches
{
    /// <summary>
    /// Harmony patch on Pawn_InteractionsTracker.TryInteractWith to intercept pawn-to-pawn social interactions
    /// and generate dynamic AI-driven dialogues based on psychology profiles, activities, and environments.
    /// Handler logic is split across partial files:
    ///   - InteractionNonResponse.cs (non-response effects)
    ///   - InteractionDialogue.cs (LLM dialogue, memory propagation, earshot)
    /// </summary>
    [HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.TryInteractWith))]
    public static partial class Patch_Pawn_InteractionsTracker_TryInteractWith
    {
        private static bool s_AbortedByPsychology = false;

        public static bool Prefix(Pawn_InteractionsTracker __instance, Pawn recipient, InteractionDef intDef, ref bool __result)
        {
            s_AbortedByPsychology = false;

            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return true;
            if (Find.Storyteller?.def?.defName != "Synapse") return true;

            // Fetch private initiator pawn field via Traverse
            Pawn initiator = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            if (initiator == null || recipient == null || !initiator.RaceProps.Humanlike || !recipient.RaceProps.Humanlike) return true;
            if (!initiator.Spawned || !recipient.Spawned) return true;

            // 1. Psychological Initiation Check
            float initChance = CalculateInitiationChance(initiator, recipient);
            if (Rand.Value > initChance)
            {
                s_AbortedByPsychology = true;
                __result = false;
                return false; // Block vanilla interaction
            }

            // 2. Psychological Response Check
            float respChance = CalculateResponseChance(initiator, recipient, intDef);
            if (Rand.Value > respChance)
            {
                TriggerNonResponseEffects(initiator, recipient);
                s_AbortedByPsychology = true;
                __result = false;
                return false; // Block vanilla interaction
            }

            return true; // Let vanilla proceed, Postfix will handle LLM dialogue
        }

        public static void Postfix(Pawn_InteractionsTracker __instance, Pawn recipient, InteractionDef intDef, bool __result)
        {
            if (s_AbortedByPsychology || !__result) return;

            Pawn initiator = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            if (initiator == null || recipient == null) return;

            // Trigger AI dialogue generation
            TriggerLlmDialogue(initiator, recipient, intDef);
        }

        private static float CalculateInitiationChance(Pawn initiator, Pawn recipient)
        {
            float chance = 1.0f;
            var coreComp = initiator.TryGetComp<SynapseCorePawnComp>();
            if (coreComp != null && coreComp.llmTraits != null)
            {
                // Jungian introvert type check
                if (coreComp.llmTraits.Any(t => t.ToLower().Contains("jungian type") && t.ToLower().Contains("i")))
                {
                    chance *= 0.5f;
                }
                // Temperament checks
                if (coreComp.llmTraits.Any(t => t.ToLower().Contains("melancholic") || t.ToLower().Contains("phlegmatic")))
                {
                    chance *= 0.7f;
                }
            }

            if (initiator.relations != null)
            {
                int opinion = initiator.relations.OpinionOf(recipient);
                if (opinion < 0)
                {
                    chance *= 0.6f;
                }
                else if (opinion > 50)
                {
                    chance *= 1.5f;
                }
            }

            return Mathf.Clamp01(chance);
        }

        private static float CalculateResponseChance(Pawn initiator, Pawn recipient, InteractionDef intDef)
        {
            float chance = 1.0f;
            var coreComp = recipient.TryGetComp<SynapseCorePawnComp>();
            if (coreComp != null && coreComp.llmTraits != null)
            {
                // Jungian introvert type check
                if (coreComp.llmTraits.Any(t => t.ToLower().Contains("jungian type") && t.ToLower().Contains("i")))
                {
                    chance *= 0.5f;
                }
                // Temperament checks
                if (coreComp.llmTraits.Any(t => t.ToLower().Contains("melancholic") || t.ToLower().Contains("phlegmatic")))
                {
                    chance *= 0.7f;
                }
            }

            if (recipient.relations != null)
            {
                int opinion = recipient.relations.OpinionOf(initiator);
                if (opinion < 0)
                {
                    chance *= 0.5f;
                }
                else if (opinion > 50)
                {
                    chance *= 1.5f;
                }
            }

            // Interest/passions check (liked topics)
            if (recipient.skills != null && recipient.skills.skills.Any(s => s.passion != Passion.None))
            {
                chance *= 1.2f;
            }

            // Speaker Charisma multiplier: high charisma increases response likelihood
            float speakerCharisma = initiator.GetStatValue(StatDefOf.SocialImpact);
            if (initiator.skills != null)
            {
                speakerCharisma += initiator.skills.GetSkill(SkillDefOf.Social).Level * 0.05f;
            }
            chance *= (speakerCharisma >= 1f ? (1f + (speakerCharisma - 1f) * 0.5f) : speakerCharisma);

            // Insulting spree edge case: High Doctor and/or high trust reduces chance to respond
            bool isInsulting = initiator.InMentalState && 
                               initiator.MentalState.def != null &&
                               initiator.MentalState.def.defName.IndexOf("insulting", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isInsulting)
            {
                int docLevel = recipient.skills?.GetSkill(SkillDefOf.Medicine)?.Level ?? 0;
                if (docLevel >= 8)
                {
                    chance *= 0.25f; // Doctors remain professional/silent
                }

                float trust = 0f;
                var psychComp = recipient.AllComps.FirstOrDefault(c => c.GetType().FullName == "RimSynapse.Psychology.Comps.SynapsePawnComp");
                if (psychComp != null)
                {
                    var socialNetworkField = psychComp.GetType().GetField("socialNetwork");
                    if (socialNetworkField != null)
                    {
                        if (socialNetworkField.GetValue(psychComp) is System.Collections.IDictionary dict)
                        {
                            string initId = initiator.GetUniqueLoadID();
                            if (dict.Contains(initId))
                            {
                                var record = dict[initId];
                                if (record != null)
                                {
                                    var trustField = record.GetType().GetField("trust");
                                    if (trustField != null)
                                    {
                                        trust = (float)trustField.GetValue(record);
                                    }
                                }
                            }
                        }
                    }
                }

                if (trust > 20f)
                {
                    chance *= 0.3f; // High trust keeps the recipient calm and silent
                }
            }

            return Mathf.Clamp01(chance);
        }
    }
}
