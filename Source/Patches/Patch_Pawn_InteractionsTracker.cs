using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;
using RimWorld;
using RimSynapse.Models;
using RimSynapse.Comps;
using Newtonsoft.Json;

namespace RimSynapse.Chat.Patches
{
    /// <summary>
    /// Harmony patch on Pawn_InteractionsTracker.TryInteractWith to intercept pawn-to-pawn social interactions
    /// and generate dynamic AI-driven dialogues based on psychology profiles, activities, and environments.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.TryInteractWith))]
    public static class Patch_Pawn_InteractionsTracker_TryInteractWith
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

        private static void TriggerNonResponseEffects(Pawn initiator, Pawn recipient)
        {
            // Throw visual ellipses indicators
            MoteMaker.ThrowText(recipient.DrawPos, recipient.Map, "...", 3f);

            bool isInsulting = initiator.InMentalState && 
                               initiator.MentalState.def != null &&
                               initiator.MentalState.def.defName.IndexOf("insulting", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isInsulting)
            {
                // Insulting pawn respects the recipient's silent/calm composure.
                // Apply a vanilla positive social thought (RapportBuilt) to improve relation/opinion.
                var thoughtDef = DefDatabase<ThoughtDef>.GetNamed("RapportBuilt", false);
                if (thoughtDef != null)
                {
                    initiator.needs?.mood?.thoughts?.memories?.TryGainMemory(thoughtDef, recipient);
                }
                
                var coreComp = initiator.TryGetComp<SynapseCorePawnComp>();
                if (coreComp != null)
                {
                    coreComp.memories.Add(new WeightedMemory
                    {
                        summary = $"Insulted {recipient.Name.ToStringShort} but they remained calm and stayed silent.",
                        memoryType = "social",
                        tags = new List<string> { "non_response", recipient.ThingID },
                        absTick = Utils.SynapseDateHelper.GameTickToAbsTick(Find.TickManager.TicksGame),
                        gameTick = Find.TickManager.TicksGame,
                        weight = 0.5f,
                        baseWeight = 0.5f,
                        decayRate = 0.05f
                    });
                }
            }
            else
            {
                // Standard chitchat / deep talk ignored
                if (Rand.Value < 0.75f)
                {
                    // Negative shift using Slighted memory
                    var thoughtDef = DefDatabase<ThoughtDef>.GetNamed("Slighted", false);
                    if (thoughtDef != null)
                    {
                        initiator.needs?.mood?.thoughts?.memories?.TryGainMemory(thoughtDef, recipient);
                    }
                    
                    var coreComp = initiator.TryGetComp<SynapseCorePawnComp>();
                    if (coreComp != null)
                    {
                        coreComp.memories.Add(new WeightedMemory
                        {
                            summary = $"Tried to talk to {recipient.Name.ToStringShort} but was ignored.",
                            memoryType = "social",
                            tags = new List<string> { "non_response", recipient.ThingID },
                            absTick = Utils.SynapseDateHelper.GameTickToAbsTick(Find.TickManager.TicksGame),
                            gameTick = Find.TickManager.TicksGame,
                            weight = 0.3f,
                            baseWeight = 0.3f,
                            decayRate = 0.05f
                        });
                    }
                }
                else
                {
                    // Positive shift using Chitchat memory (respected their quietness)
                    var thoughtDef = DefDatabase<ThoughtDef>.GetNamed("Chitchat", false);
                    if (thoughtDef != null)
                    {
                        initiator.needs?.mood?.thoughts?.memories?.TryGainMemory(thoughtDef, recipient);
                    }
                    
                    var coreComp = initiator.TryGetComp<SynapseCorePawnComp>();
                    if (coreComp != null)
                    {
                        coreComp.memories.Add(new WeightedMemory
                        {
                            summary = $"{recipient.Name.ToStringShort} quietly listened without speaking.",
                            memoryType = "social",
                            tags = new List<string> { "non_response", recipient.ThingID },
                            absTick = Utils.SynapseDateHelper.GameTickToAbsTick(Find.TickManager.TicksGame),
                            gameTick = Find.TickManager.TicksGame,
                            weight = 0.2f,
                            baseWeight = 0.2f,
                            decayRate = 0.05f
                        });
                    }
                }
            }
        }

        private static void TriggerLlmDialogue(Pawn initiator, Pawn recipient, InteractionDef intDef)
        {
            var worldComp = Find.World?.GetComponent<SynapseChatWorldComponent>();
            if (worldComp == null) return;

            string idA = initiator.ThingID;
            string idB = recipient.ThingID;
            PawnConversation conversation = worldComp.pawnConversations.FirstOrDefault(c => 
                (c.pawnAId == idA && c.pawnBId == idB) || (c.pawnAId == idB && c.pawnBId == idA));

            int currentTick = Find.TickManager.TicksGame;
            
            // Core slider memory setting support
            float hours = 24f;
            if (RimSynapseMod.Instance?.Settings != null)
            {
                hours = RimSynapseMod.Instance.Settings.shortTermMemoryHours;
            }
            int maxAgeTicks = Mathf.RoundToInt(hours * 2500f);

            if (conversation == null || (currentTick - conversation.lastTick > maxAgeTicks))
            {
                if (conversation != null)
                {
                    worldComp.pawnConversations.Remove(conversation);
                }
                conversation = new PawnConversation(idA, idB, currentTick);
                worldComp.pawnConversations.Add(conversation);
            }
            else
            {
                conversation.lastTick = currentTick;
            }

            string initiatorMood = initiator.needs?.mood?.CurLevelPercentage.ToStringPercent() ?? "50%";
            string initiatorActivity = initiator.jobs?.curJob?.GetReport(initiator) ?? "standing around";
            string initiatorTraits = string.Join(", ", initiator.story?.traits?.allTraits?.Select(t => t.Label) ?? Enumerable.Empty<string>());
            
            var initCore = initiator.TryGetComp<SynapseCorePawnComp>();
            string initPsych = initCore != null && initCore.llmTraits != null ? string.Join(", ", initCore.llmTraits) : "none";

            string recipientMood = recipient.needs?.mood?.CurLevelPercentage.ToStringPercent() ?? "50%";
            string recipientActivity = recipient.jobs?.curJob?.GetReport(recipient) ?? "standing around";
            string recipientTraits = string.Join(", ", recipient.story?.traits?.allTraits?.Select(t => t.Label) ?? Enumerable.Empty<string>());
            
            var recipCore = recipient.TryGetComp<SynapseCorePawnComp>();
            string recipPsych = recipCore != null && recipCore.llmTraits != null ? string.Join(", ", recipCore.llmTraits) : "none";

            int initOpinionOfRecip = initiator.relations?.OpinionOf(recipient) ?? 0;
            int recipOpinionOfInit = recipient.relations?.OpinionOf(initiator) ?? 0;

            var apiMessages = new List<ChatMessage>
            {
                new ChatMessage
                {
                    role = "system",
                    content = $"You are role-playing as {initiator.Name.ToStringShort}, a colonist in RimWorld. You are having a conversation with {recipient.Name.ToStringShort}.\n" +
                              $"Respond in character as {initiator.Name.ToStringShort}. Keep your reply to exactly ONE short sentence, natural, conversational, and under 20 words.\n" +
                              $"Established context for {initiator.Name.ToStringShort}:\n" +
                              $"- Mood: {initiatorMood}\n" +
                              $"- Current Activity: {initiatorActivity}\n" +
                              $"- Personal Traits: {initiatorTraits}\n" +
                              $"- Psychological Profile: {initPsych}\n" +
                              $"- Opinion of {recipient.Name.ToStringShort}: {initOpinionOfRecip} (-100 to 100)\n\n" +
                              $"Established context for {recipient.Name.ToStringShort}:\n" +
                              $"- Mood: {recipientMood}\n" +
                              $"- Current Activity: {recipientActivity}\n" +
                              $"- Personal Traits: {recipientTraits}\n" +
                              $"- Psychological Profile: {recipPsych}\n" +
                              $"- Opinion of you: {recipOpinionOfInit} (-100 to 100)\n\n" +
                              $"Social interaction type: {intDef.LabelCap}.\n" +
                              $"Your response MUST be a JSON object with the following fields:\n" +
                              $"{{\n" +
                              $"  \"reply\": \"Your 1-sentence comment here.\"\n" +
                              $"}}"
                }
            };

            // Inject dialogue history
            int startIdx = Mathf.Max(0, conversation.messages.Count - 6);
            for (int i = startIdx; i < conversation.messages.Count; i++)
            {
                var msg = conversation.messages[i];
                apiMessages.Add(new ChatMessage
                {
                    role = msg.sender == initiator.ThingID ? "assistant" : "user",
                    content = msg.message
                });
            }

            SynapseClient.ChatAsync(
                RimSynapseChatMod.ModHandle,
                apiMessages,
                new ChatOptions { priority = 1, requestName = "Pawn-to-Pawn Chat Interaction" },
                result =>
                {
                    if (result.success && !string.IsNullOrEmpty(result.content))
                    {
                        string reply = "";
                        try
                        {
                            var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(result.content);
                            if (dict != null && dict.TryGetValue("reply", out string val))
                            {
                                reply = val;
                            }
                        }
                        catch
                        {
                            reply = result.content;
                        }

                        if (string.IsNullOrEmpty(reply))
                        {
                            reply = result.content;
                        }

                        // Thread-safe dispatch back to main game thread
                        SynapseGameComponent.Enqueue(() =>
                        {
                            if (!initiator.Spawned || initiator.Dead || !recipient.Spawned || recipient.Dead) return;

                            // Display comment in visual bubble
                            MoteMaker.ThrowText(initiator.DrawPos, initiator.Map, reply, 4f);

                            // Save message to short term history
                            conversation.messages.Add(new SynapseChatMessage(initiator.ThingID, reply, Find.TickManager.TicksGame));
                            conversation.lastTick = Find.TickManager.TicksGame;
                            if (conversation.messages.Count > 10)
                            {
                                conversation.messages.RemoveAt(0);
                            }

                            // Propagate memories to speaker, recipient, and closest bystander within earshot
                            PropagateContextMemories(initiator, recipient, reply);
                        });
                    }
                }
            );
        }

        private static void PropagateContextMemories(Pawn initiator, Pawn recipient, string reply)
        {
            int earshotRange = CalculateEarshotRange(initiator);

            // Feed to initiator
            var initCore = initiator.TryGetComp<SynapseCorePawnComp>();
            if (initCore != null)
            {
                initCore.memories.Add(new WeightedMemory
                {
                    summary = $"Said to {recipient.Name.ToStringShort}: \"{reply}\"",
                    memoryType = "social",
                    tags = new List<string> { "conversation", recipient.ThingID },
                    absTick = Utils.SynapseDateHelper.GameTickToAbsTick(Find.TickManager.TicksGame),
                    gameTick = Find.TickManager.TicksGame,
                    weight = 0.5f,
                    baseWeight = 0.5f,
                    decayRate = 0.05f
                });
            }

            // Feed to recipient
            var recipCore = recipient.TryGetComp<SynapseCorePawnComp>();
            if (recipCore != null)
            {
                recipCore.memories.Add(new WeightedMemory
                {
                    summary = $"{initiator.Name.ToStringShort} said to me: \"{reply}\"",
                    memoryType = "social",
                    tags = new List<string> { "conversation", initiator.ThingID },
                    absTick = Utils.SynapseDateHelper.GameTickToAbsTick(Find.TickManager.TicksGame),
                    gameTick = Find.TickManager.TicksGame,
                    weight = 0.5f,
                    baseWeight = 0.5f,
                    decayRate = 0.05f
                });
            }

            // Find closest other bystander within earshot range
            Pawn closestBystander = null;
            float closestDist = float.MaxValue;
            foreach (var other in initiator.Map.mapPawns.AllPawnsSpawned)
            {
                if (other == initiator || other == recipient || other.Dead || !other.RaceProps.Humanlike) continue;

                float dist = initiator.Position.DistanceTo(other.Position);
                if (dist <= earshotRange && dist < closestDist)
                {
                    closestDist = dist;
                    closestBystander = other;
                }
            }

            if (closestBystander != null)
            {
                var bystanderCore = closestBystander.TryGetComp<SynapseCorePawnComp>();
                if (bystanderCore != null)
                {
                    bystanderCore.memories.Add(new WeightedMemory
                    {
                        summary = $"Overheard {initiator.Name.ToStringShort} say to {recipient.Name.ToStringShort}: \"{reply}\"",
                        memoryType = "social",
                        tags = new List<string> { "overheard", initiator.ThingID, recipient.ThingID },
                        absTick = Utils.SynapseDateHelper.GameTickToAbsTick(Find.TickManager.TicksGame),
                        gameTick = Find.TickManager.TicksGame,
                        weight = 0.3f,
                        baseWeight = 0.3f,
                        decayRate = 0.05f
                    });
                }
            }
        }

        private static int CalculateEarshotRange(Pawn speaker)
        {
            int noiseCount = 0;
            var map = speaker.Map;
            if (map == null) return 8;

            int numCells = GenRadial.NumCellsInRadius(8f);
            IntVec3 speakerPos = speaker.Position;
            Room speakerRoom = speakerPos.GetRoom(map);

            for (int i = 0; i < numCells; i++)
            {
                IntVec3 cell = speakerPos + GenRadial.RadialPattern[i];
                if (!cell.InBounds(map)) continue;

                // Enclosed rooms check: ignore noise from outside speaker's room
                Room noiseRoom = cell.GetRoom(map);
                if (speakerRoom != noiseRoom)
                {
                    if (speakerRoom == null || noiseRoom == null || !speakerRoom.PsychologicallyOutdoors || !noiseRoom.PsychologicallyOutdoors)
                    {
                        continue;
                    }
                }

                var things = map.thingGrid.ThingsListAt(cell);
                for (int j = 0; j < things.Count; j++)
                {
                    Thing thing = things[j];
                    if (thing is Building b)
                    {
                        string name = b.def.defName.ToLower();
                        if (name.Contains("generator") || name.Contains("turbine") || name.Contains("engine") || name.Contains("mill"))
                        {
                            var power = b.GetComp<CompPowerTrader>();
                            if (power != null && !power.PowerOn) continue;
                            var breakdown = b.GetComp<CompBreakdownable>();
                            if (breakdown != null && breakdown.BrokenDown) continue;

                            noiseCount++;
                        }
                    }
                    else if (thing is Pawn otherPawn && otherPawn != speaker && otherPawn.Spawned && !otherPawn.Dead)
                    {
                        if (otherPawn.CurJob != null)
                        {
                            JobDef jobDef = otherPawn.CurJob.def;
                            if (jobDef == JobDefOf.Mine ||
                                jobDef == JobDefOf.CutPlant ||
                                jobDef == JobDefOf.Deconstruct ||
                                jobDef == JobDefOf.Repair ||
                                jobDef.defName.Contains("Attack") ||
                                jobDef.defName.Contains("Harvest"))
                            {
                                noiseCount++;
                            }
                        }
                    }
                }
            }

            return Mathf.Max(1, 8 - noiseCount);
        }
    }
}
