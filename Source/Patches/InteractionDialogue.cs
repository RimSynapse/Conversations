using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;
using RimSynapse.Models;
using RimSynapse.Comps;
using Newtonsoft.Json;

namespace RimSynapse.Conversations.Patches
{
    /// <summary>
    /// LLM dialogue generation, memory propagation, and earshot calculation
    /// for pawn-to-pawn social interactions.
    /// </summary>
    public static partial class Patch_Pawn_InteractionsTracker_TryInteractWith
    {
        private static void TriggerLlmDialogue(Pawn initiator, Pawn recipient, InteractionDef intDef)
        {
            var worldComp = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
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
                RimSynapseConversationsMod.ModHandle,
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
                            conversation.messages.Add(new SynapseConversationMessage(initiator.ThingID, reply, Find.TickManager.TicksGame));
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
