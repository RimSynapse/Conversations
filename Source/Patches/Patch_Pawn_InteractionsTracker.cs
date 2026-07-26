using System;
using System.Collections.Generic;
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
        // Per-pair throttle. Vanilla fires social interactions between the same two pawns
        // extremely frequently; we hook that as the trigger but only "upgrade" it into a
        // Synapse reaction (LLM dialogue or a silent bubble) once per cooldown window so that
        // chatter — and the social memories each conversation plants — stays at a sane cadence.
        private static readonly Dictionary<string, int> lastDialogueTickByPair = new Dictionary<string, int>();

        private static string PairKey(Pawn a, Pawn b)
        {
            string ida = a.ThingID;
            string idb = b.ThingID;
            return string.CompareOrdinal(ida, idb) <= 0 ? ida + "|" + idb : idb + "|" + ida;
        }

        public static bool Prefix(Pawn_InteractionsTracker __instance, Pawn recipient, InteractionDef intDef, ref bool __result)
        {
            return true; // Let vanilla proceed, Postfix will handle response checks
        }

        public static void Postfix(Pawn_InteractionsTracker __instance, Pawn recipient, InteractionDef intDef, bool __result)
        {
            if (!__result) return;
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;

            Pawn initiator = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            if (initiator == null || recipient == null || !initiator.RaceProps.Humanlike || !recipient.RaceProps.Humanlike) return;
            if (!initiator.Spawned || !recipient.Spawned) return;

            // Throttle check: skip entirely if this pair reacted too recently.
            int nowTick = Find.TickManager.TicksGame;
            float cooldownHours = RimSynapseConversationsMod.Settings?.dialogueCooldownHours ?? 1f;
            int cooldownTicks = Mathf.Max(0, Mathf.RoundToInt(cooldownHours * 2500f));
            string pairKey = PairKey(initiator, recipient);
            if (cooldownTicks > 0
                && lastDialogueTickByPair.TryGetValue(pairKey, out int lastReactTick)
                && (nowTick - lastReactTick) < cooldownTicks)
            {
                return; // Too soon since this pair last reacted
            }

            // Personality/opinion-based initiation gate (introverts, melancholic temperaments,
            // and pawns who dislike the recipient engage less often). If they decline this time
            // we do NOT consume the cooldown window, so a later interaction can still trigger.
            float initChance = CalculateInitiationChance(initiator, recipient);
            if (Rand.Value > initChance) return;

            // Psychological Response Check
            float respChance = CalculateResponseChance(initiator, recipient, intDef);
            if (Rand.Value > respChance)
            {
                lastDialogueTickByPair[pairKey] = nowTick; // silent reaction still consumes the window
                TriggerNonResponseEffects(initiator, recipient);
                return; // Silent/Ellipses bubble, no LLM dialogue
            }

            // Trigger AI dialogue generation (mark the window synchronously since generation is async).
            lastDialogueTickByPair[pairKey] = nowTick;
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
