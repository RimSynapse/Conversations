using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;
using RimWorld;
using RimSynapse.Models;
using RimSynapse.Comps;
using RimSynapse.Conversations;
using Newtonsoft.Json;

namespace RimSynapse.Conversations.Patches
{
    /// <summary>
    /// LLM dialogue generation, memory propagation, and earshot calculation
    /// for pawn-to-pawn social interactions.
    /// </summary>
    public static partial class Patch_Pawn_InteractionsTracker_TryInteractWith
    {
        private class LlmConversationResponse
        {
            public List<LlmDialogueLine> dialogue { get; set; }
            public float trustOffset { get; set; }
            public float familiarityOffset { get; set; }
            public float affinityOffset { get; set; }
        }

        private class LlmDialogueLine
        {
            public string sender { get; set; }
            public string text { get; set; }
        }

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

            // Select a random topic from XML ChatTopicDef databases
            ChatTopicDef topic = SelectRandomTopic(intDef);
            string topicPrompt = "";
            if (topic != null)
            {
                string promptSeed = topic.prompts.RandomElement();
                topicPrompt = $"The topic of this conversation is: {topic.topicName}. You should discuss: {promptSeed}.";
            }
            else
            {
                topicPrompt = $"The topic of this conversation is a random social interaction: {intDef.LabelCap}.";
            }

            string colonyFacilities = GetColonyFacilitiesDescription(initiator.Map ?? recipient.Map);
            string initiatorRecent = GetRecentMemoriesDescription(initiator, initCore);
            string recipientRecent = GetRecentMemoriesDescription(recipient, recipCore);
            string initiatorLocs = initCore != null ? initCore.GetRecentLocationsSummary() : "outdoors (100%)";
            string recipientLocs = recipCore != null ? recipCore.GetRecentLocationsSummary() : "outdoors (100%)";
            string initiatorJobs = initCore != null ? initCore.GetRecentJobsSummary() : "idle (100%)";
            string recipientJobs = recipCore != null ? recipCore.GetRecentJobsSummary() : "idle (100%)";
            string initiatorLieContext = GetLyingOrDelusionalContext(initiator, initiatorJobs);
            string recipientLieContext = GetLyingOrDelusionalContext(recipient, recipientJobs);

            string systemPrompt = $"You are an AI simulating a social dialogue between two RimWorld pawns: {initiator.Name.ToStringShort} and {recipient.Name.ToStringShort}.\n" +
                $"Your task is to generate a natural, in-character, alternating conversation sequence of exactly 5 lines (messages) starting with {initiator.Name.ToStringShort}.\n\n" +
                $"Established context for {initiator.Name.ToStringShort}:\n" +
                $"- Mood: {initiatorMood}\n" +
                $"- Current Activity: {initiatorActivity}\n" +
                $"- Actual Recent Actions: {initiatorRecent}\n" +
                $"- Locations Spent Time In (Last 24h): {initiatorLocs}\n" +
                $"- Work Activity Distribution (Last 24h): {initiatorJobs}\n" +
                $"- Lying or Delusional (imagines easier life): {initiatorLieContext}\n" +
                $"- Personal Traits: {initiatorTraits}\n" +
                $"- Psychological Profile: {initPsych}\n" +
                $"- Opinion of {recipient.Name.ToStringShort}: {initOpinionOfRecip} (-100 to 100)\n\n" +
                $"Established context for {recipient.Name.ToStringShort}:\n" +
                $"- Mood: {recipientMood}\n" +
                $"- Current Activity: {recipientActivity}\n" +
                $"- Actual Recent Actions: {recipientRecent}\n" +
                $"- Locations Spent Time In (Last 24h): {recipientLocs}\n" +
                $"- Work Activity Distribution (Last 24h): {recipientJobs}\n" +
                $"- Lying or Delusional (imagines easier life): {recipientLieContext}\n" +
                $"- Personal Traits: {recipientTraits}\n" +
                $"- Psychological Profile: {recipPsych}\n" +
                $"- Opinion of {initiator.Name.ToStringShort}: {recipOpinionOfInit} (-100 to 100)\n\n" +
                $"Colony Infrastructure & Facilities:\n" +
                $"- Available Benches/Rooms: {colonyFacilities}\n\n" +
                $"{topicPrompt}\n\n" +
                $"Dialogue Requirements:\n" +
                $"- Generate exactly 5 alternating dialogue turns: Turn 1 ({initiator.Name.ToStringShort}), Turn 2 ({recipient.Name.ToStringShort}), Turn 3 ({initiator.Name.ToStringShort}), Turn 4 ({recipient.Name.ToStringShort}), Turn 5 ({initiator.Name.ToStringShort}).\n" +
                $"- Keep each dialogue line brief, conversational, and under 25 words.\n" +
                $"- The tone must reflect their traits, mood, opinion, and the topic.\n" +
                $"- CRITICAL: The dialogue MUST ground their discussion of chores, tasks, and locations in reality. Do NOT mention or claim they spent time in locations, rooms, or facilities that are not reflected in their Locations Spent Time In, Work Activity Distribution, or the Colony Infrastructure lists. If they discuss what they've been doing, it must align with their actual work history and recent actions. Pawns should reference their recent location and work distribution in a natural, colloquial way matching their traits (e.g., if they spent most of their time sowing fields, they might say 'I slaved all day in the fields', or if outdoors, comment on the weather or hard labor). However, if 'Lying or Delusional' is TRUE for a pawn, they will make up a face-saving or relaxing lie/delusion matching that instruction (such as claiming they were kicking back watching animals) rather than reporting their actual work.\n\n" +
                $"You MUST return a JSON object (strictly valid JSON, no markdown formatting or extra text) with the following schema:\n" +
                $"{{\n" +
                $"  \"dialogue\": [\n" +
                $"    {{ \"sender\": \"{initiator.Name.ToStringShort}\", \"text\": \"First line of dialogue by initiator...\" }},\n" +
                $"    {{ \"sender\": \"{recipient.Name.ToStringShort}\", \"text\": \"Response line of dialogue by recipient...\" }},\n" +
                $"    {{ \"sender\": \"{initiator.Name.ToStringShort}\", \"text\": \"Third line of dialogue by initiator...\" }},\n" +
                $"    {{ \"sender\": \"{recipient.Name.ToStringShort}\", \"text\": \"Fourth line of dialogue by recipient...\" }},\n" +
                $"    {{ \"sender\": \"{initiator.Name.ToStringShort}\", \"text\": \"Fifth line of dialogue by initiator...\" }}\n" +
                $"  ],\n" +
                $"  \"trustOffset\": 0.0,\n" +
                $"  \"familiarityOffset\": 0.0,\n" +
                $"  \"affinityOffset\": 0.0\n" +
                $"}}";

            var apiMessages = new List<ChatMessage>
            {
                new ChatMessage
                {
                    role = "system",
                    content = systemPrompt
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
                new ChatOptions 
                { 
                    priority = 1, 
                    requestName = "Pawn-to-Pawn Chat Interaction",
                    targetName = $"{initiator.Name.ToStringShort} and {recipient.Name.ToStringShort}"
                },
                result =>
                {
                    if (result.success && !string.IsNullOrEmpty(result.content))
                    {
                        LlmConversationResponse parsed = null;
                        try
                        {
                            parsed = JsonConvert.DeserializeObject<LlmConversationResponse>(result.content);
                        }
                        catch
                        {
                            try
                            {
                                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(result.content);
                                if (dict != null && dict.TryGetValue("reply", out string singleReply))
                                {
                                    parsed = new LlmConversationResponse
                                    {
                                        dialogue = new List<LlmDialogueLine>
                                        {
                                            new LlmDialogueLine { sender = initiator.Name.ToStringShort, text = singleReply }
                                        },
                                        trustOffset = 1f,
                                        familiarityOffset = 2f,
                                        affinityOffset = 1f
                                    };
                                }
                            }
                            catch
                            {
                                parsed = new LlmConversationResponse
                                {
                                    dialogue = new List<LlmDialogueLine>
                                    {
                                        new LlmDialogueLine { sender = initiator.Name.ToStringShort, text = result.content }
                                    },
                                    trustOffset = 1f,
                                    familiarityOffset = 2f,
                                    affinityOffset = 1f
                                };
                            }
                        }

                        if (parsed != null && parsed.dialogue != null && parsed.dialogue.Count > 0)
                        {
                            SynapseGameComponent.Enqueue(() =>
                            {
                                if (!initiator.Spawned || initiator.Dead || !recipient.Spawned || recipient.Dead) return;

                                string firstLine = "";
                                foreach (var line in parsed.dialogue)
                                {
                                    if (string.IsNullOrEmpty(line.text)) continue;
                                    if (string.IsNullOrEmpty(firstLine)) firstLine = line.text;

                                    string senderId = line.sender.Equals(recipient.Name.ToStringShort, StringComparison.OrdinalIgnoreCase)
                                        ? recipient.ThingID
                                        : initiator.ThingID;

                                    conversation.messages.Add(new SynapseConversationMessage(senderId, line.text, Find.TickManager.TicksGame));
                                }

                                conversation.lastTick = Find.TickManager.TicksGame;
                                while (conversation.messages.Count > 50)
                                {
                                    conversation.messages.RemoveAt(0);
                                }

                                // Apply psychology adjustments via reflection
                                ApplyPsychologyOffsets(initiator, recipient, parsed.trustOffset, parsed.familiarityOffset);

                                // Apply vanilla affinity thoughts
                                ApplyVanillaAffinityThought(initiator, recipient, parsed.affinityOffset);

                                // Propagate memories based on topic and first dialogue line
                                string topicName = (topic != null) ? topic.topicName : "Social";
                                PropagateContextMemories(initiator, recipient, topicName, firstLine);
                            });
                        }
                        else
                        {
                            TriggerFallback(initiator, recipient, intDef, conversation);
                        }
                    }
                    else
                    {
                        TriggerFallback(initiator, recipient, intDef, conversation);
                    }
                }
            );
        }

        private static ChatTopicDef SelectRandomTopic(InteractionDef intDef)
        {
            var allTopics = DefDatabase<ChatTopicDef>.AllDefsListForReading;
            if (allTopics == null || allTopics.Count == 0) return null;

            var enabledTopics = allTopics.Where(t => 
                (RimSynapseConversationsMod.Settings?.disabledTopicDefNames == null || 
                 !RimSynapseConversationsMod.Settings.disabledTopicDefNames.Contains(t.defName))
            ).ToList();

            if (enabledTopics.Count == 0)
            {
                enabledTopics = allTopics;
            }

            List<ChatTopicDef> candidates;
            if (intDef == InteractionDefOf.DeepTalk)
            {
                candidates = enabledTopics.Where(t => t.isDeepTalk).ToList();
            }
            else
            {
                candidates = enabledTopics.Where(t => t.isChitchat).ToList();
            }

            if (candidates.Count == 0)
            {
                candidates = enabledTopics;
            }

            return candidates.RandomElement();
        }

        private static void TriggerFallback(Pawn initiator, Pawn recipient, InteractionDef intDef, PawnConversation conversation)
        {
            SynapseGameComponent.Enqueue(() =>
            {
                if (!initiator.Spawned || initiator.Dead || !recipient.Spawned || recipient.Dead) return;

                var lastLog = Find.PlayLog.AllEntries
                    .OfType<PlayLogEntry_Interaction>()
                    .FirstOrDefault(e => 
                    {
                        var init = Traverse.Create(e).Field("initiator").GetValue<Pawn>();
                        var recip = Traverse.Create(e).Field("recipient").GetValue<Pawn>();
                        return init == initiator && recip == recipient;
                    });

                string fallbackReply = lastLog != null 
                    ? lastLog.ToGameStringFromPOV(initiator) 
                    : $"{initiator.Name.ToStringShort} initiated a conversation about {intDef.label}.";

                conversation.messages.Add(new SynapseConversationMessage(initiator.ThingID, fallbackReply, Find.TickManager.TicksGame));
                conversation.lastTick = Find.TickManager.TicksGame;
                while (conversation.messages.Count > 50)
                {
                    conversation.messages.RemoveAt(0);
                }
            });
        }

        private static void ApplyPsychologyOffsets(Pawn initiator, Pawn recipient, float trustOffset, float familiarityOffset)
        {
            try
            {
                AdjustRelationship(initiator, recipient.GetUniqueLoadID(), trustOffset, familiarityOffset);
                AdjustRelationship(recipient, initiator.GetUniqueLoadID(), trustOffset, familiarityOffset);
            }
            catch (Exception ex)
            {
                SynapseLogger.Error($"Error applying psychology offsets: {ex}", "conversations");
            }
        }

        private static void AdjustRelationship(Pawn pawn, string otherId, float trustOffset, float familiarityOffset)
        {
            var comp = pawn.AllComps.FirstOrDefault(c => c.GetType().FullName == "RimSynapse.Psychology.Comps.SynapsePawnComp");
            if (comp == null) return;

            var socialNetworkField = comp.GetType().GetField("socialNetwork");
            if (socialNetworkField == null) return;

            var dict = socialNetworkField.GetValue(comp) as System.Collections.IDictionary;
            if (dict == null) return;

            if (!dict.Contains(otherId))
            {
                var recordType = socialNetworkField.FieldType.GetGenericArguments()[1];
                var newRecord = Activator.CreateInstance(recordType);
                dict[otherId] = newRecord;
            }

            var record = dict[otherId];
            if (record != null)
            {
                var trustField = record.GetType().GetField("trust");
                var familiarityField = record.GetType().GetField("familiarity");

                if (trustField != null)
                {
                    float currentTrust = (float)trustField.GetValue(record);
                    float newTrust = Mathf.Clamp(currentTrust + trustOffset, -100f, 100f);
                    trustField.SetValue(record, newTrust);
                }

                if (familiarityField != null)
                {
                    float currentFam = (float)familiarityField.GetValue(record);
                    float newFam = Mathf.Clamp(currentFam + familiarityOffset, 0f, 100f);
                    familiarityField.SetValue(record, newFam);
                }
            }
        }

        private static void ApplyVanillaAffinityThought(Pawn initiator, Pawn recipient, float affinityOffset)
        {
            try
            {
                if (recipient.needs?.mood?.thoughts?.memories == null) return;

                if (affinityOffset >= 2f)
                {
                    var chitchatDef = DefDatabase<ThoughtDef>.GetNamed("Chitchat", false);
                    if (chitchatDef != null)
                    {
                        recipient.needs.mood.thoughts.memories.TryGainMemory(chitchatDef, initiator);
                    }
                }
                else if (affinityOffset <= -2f)
                {
                    var slightDef = DefDatabase<ThoughtDef>.GetNamed("Slight", false);
                    if (slightDef != null)
                    {
                        recipient.needs.mood.thoughts.memories.TryGainMemory(slightDef, initiator);
                    }
                }
            }
            catch (Exception ex)
            {
                SynapseLogger.Error($"Error applying vanilla affinity thought: {ex}", "conversations");
            }
        }

        private static void PropagateContextMemories(Pawn initiator, Pawn recipient, string topicName, string reply)
        {
            int earshotRange = CalculateEarshotRange(initiator);

            // Feed to initiator
            var initCore = initiator.TryGetComp<SynapseCorePawnComp>();
            if (initCore != null)
            {
                initCore.memories.Add(new WeightedMemory
                {
                    summary = $"Said to {recipient.Name.ToStringShort} during a {topicName} conversation: \"{reply}\"",
                    memoryType = "social",
                    tags = new List<string> { "conversation", recipient.ThingID },
                    absTick = Utils.SynapseDateHelper.GameTickToAbsTick(Find.TickManager.TicksGame),
                    gameTick = Find.TickManager.TicksGame,
                    weight = 0.10f,
                    baseWeight = 0.10f,
                    decayRate = 0.10f
                });
            }

            // Feed to recipient
            var recipCore = recipient.TryGetComp<SynapseCorePawnComp>();
            if (recipCore != null)
            {
                recipCore.memories.Add(new WeightedMemory
                {
                    summary = $"{initiator.Name.ToStringShort} said to me during a {topicName} conversation: \"{reply}\"",
                    memoryType = "social",
                    tags = new List<string> { "conversation", initiator.ThingID },
                    absTick = Utils.SynapseDateHelper.GameTickToAbsTick(Find.TickManager.TicksGame),
                    gameTick = Find.TickManager.TicksGame,
                    weight = 0.10f,
                    baseWeight = 0.10f,
                    decayRate = 0.10f
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
                        summary = $"Overheard {initiator.Name.ToStringShort} say to {recipient.Name.ToStringShort} during a {topicName} conversation: \"{reply}\"",
                        memoryType = "social",
                        tags = new List<string> { "overheard", initiator.ThingID, recipient.ThingID },
                        absTick = Utils.SynapseDateHelper.GameTickToAbsTick(Find.TickManager.TicksGame),
                        gameTick = Find.TickManager.TicksGame,
                        weight = 0.05f,
                        baseWeight = 0.05f,
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

        private static string GetColonyFacilitiesDescription(Map map)
        {
            if (map == null) return "None";

            var list = new List<string>();
            bool hasResearch = false;
            bool hasStove = false;
            bool hasCrafting = false;
            bool hasHospital = false;

            var allBuildings = map.listerBuildings?.allBuildingsColonist;
            if (allBuildings != null)
            {
                foreach (var b in allBuildings)
                {
                    if (b?.def?.defName == null) continue;
                    string defName = b.def.defName.ToLower();

                    if (defName.Contains("research") || defName.Contains("laboratory") || defName.Contains("labbench"))
                    {
                        hasResearch = true;
                    }
                    else if (defName.Contains("stove") || defName.Contains("cooker"))
                    {
                        hasStove = true;
                    }
                    else if (defName.Contains("table") || defName.Contains("bench") || defName.Contains("spot"))
                    {
                        hasCrafting = true;
                    }

                    var room = b.Position.GetRoom(map);
                    if (room?.Role?.defName == "Hospital")
                    {
                        hasHospital = true;
                    }
                }
            }

            if (hasResearch) list.Add("Research Lab / Bench");
            if (hasStove) list.Add("Cooking Stove");
            if (hasCrafting) list.Add("Crafting/Workshop Tables");
            if (hasHospital) list.Add("Medical Hospital");

            if (list.Count == 0) return "Basic crashlanded camp with no advanced facilities built yet.";
            return string.Join(", ", list);
        }

        private static string GetRecentMemoriesDescription(Pawn pawn, SynapseCorePawnComp coreComp)
        {
            if (coreComp == null || coreComp.memories == null || coreComp.memories.Count == 0) 
                return "No recorded recent history.";

            int currentTick = Find.TickManager.TicksGame;
            var recent = coreComp.memories
                .Where(m => (currentTick - m.gameTick) < 120000 && !string.IsNullOrEmpty(m.summary))
                .Select(m => m.summary)
                .Take(5)
                .ToList();

            if (recent.Count == 0) 
                return "No recorded recent history.";

            return string.Join("; ", recent);
        }

        private static string GetLyingOrDelusionalContext(Pawn pawn, string actualWork)
        {
            float mood = pawn.needs?.mood?.CurLevelPercentage ?? 0.5f;
            float lieChance = 0.05f; // 5% base chance

            // Increase chance if mood is low (mentally unstable/stressed)
            if (mood < 0.35f) lieChance += 0.25f; // +25%
            else if (mood < 0.50f) lieChance += 0.10f; // +10%

            // Increase chance based on traits
            if (pawn.story?.traits != null)
            {
                foreach (var trait in pawn.story.traits.allTraits)
                {
                    string traitLabel = trait.Label.ToLower();
                    if (traitLabel.Contains("liar") || traitLabel.Contains("sociopath") || traitLabel.Contains("psychopath"))
                    {
                        lieChance += 0.40f;
                    }
                    else if (traitLabel.Contains("greedy") || traitLabel.Contains("jealous") || traitLabel.Contains("sloth"))
                    {
                        lieChance += 0.15f;
                    }
                }
            }

            // RNG check
            int seed = pawn.thingIDNumber ^ Find.TickManager.TicksGame;
            var rand = new System.Random(seed);
            if (rand.NextDouble() < lieChance)
            {
                // Generate a delusional alternative explanation
                if (actualWork.Contains("sow") || actualWork.Contains("harvest") || actualWork.Contains("farm") || actualWork.Contains("grow"))
                {
                    return "TRUE (This pawn is stressed/unstable and is lying or imagining their life is easier. They will lie and claim they spent their time kicking back, relaxing, and watching the animals do the work for them, rather than slaving in the fields).";
                }
                if (actualWork.Contains("mine") || actualWork.Contains("drill"))
                {
                    return "TRUE (This pawn is stressed/unstable and is lying or imagining their life is easier. They will lie and claim they found a secret stash of treasure or were just stargazing, rather than mining hard rock).";
                }
                if (actualWork.Contains("clean") || actualWork.Contains("haul"))
                {
                    return "TRUE (This pawn is stressed/unstable and is lying. They will lie and claim someone else did all their chores for them while they took a long nap).";
                }
                
                return "TRUE (This pawn is stressed/unstable and is lying. They will pretend they had a luxurious, relaxing day and spent their time leisure-making, rather than doing hard labor).";
            }

            return "FALSE (Must speak truthfully about their recent work and locations).";
        }
    }
}
