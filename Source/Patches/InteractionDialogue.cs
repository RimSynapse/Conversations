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
            public string text { get; set; }
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

            // Check if game is sped up
            bool isSpedUp = Find.TickManager.CurTimeSpeed != TimeSpeed.Normal;

            // Pre-seeding is EXPERIMENTAL and off by default (#29): it can serve stale context, and the
            // recipient response must be dynamic regardless. When off, every conversation is generated
            // live so we can measure real latency and context sufficiency.
            bool useCache = (RimSynapseConversationsMod.Settings?.experimentalPreSeeding ?? false) && !isSpedUp;

            if (useCache)
            {
                var cached = worldComp.PopFreshPreGen(initiator, recipient);
                if (cached != null)
                {
                    // If cached as continuation but more than 1 hour passed since last actual interaction, discard it
                    if (cached.isContinuation && (currentTick - conversation.lastTick > 2500))
                    {
                        cached = null;
                    }

                    if (cached != null)
                    {
                        // Apply dialogue instantly
                        conversation.messages.Add(new SynapseConversationMessage(idA, cached.initiatorStatement, currentTick));
                        conversation.messages.Add(new SynapseConversationMessage(idB, cached.recipientResponse, currentTick));
                        conversation.lastTick = currentTick;

                        if (Find.TickManager.CurTimeSpeed == TimeSpeed.Normal)
                        {
                            UI.SpeechBubbleManager.AddBubble(initiator, recipient, cached.initiatorStatement, 0, 4.5f);
                            UI.SpeechBubbleManager.AddBubble(recipient, initiator, cached.recipientResponse, 270, 4.5f);
                        }

                        ApplyPsychologyOffsets(initiator, recipient, cached.trustOffset, cached.familiarityOffset);
                        ApplyVanillaAffinityThought(initiator, recipient, cached.affinityOffset);

                        ChatTopicDef cachedTopic = DefDatabase<ChatTopicDef>.GetNamed(cached.topicDefName, false);
                        string tName = cachedTopic != null ? cachedTopic.topicName : "Social";
                        PropagateContextMemories(initiator, recipient, tName, cached.initiatorStatement);
                        conversation.PushRecentTopic(cached.topicDefName);
                        float poolDist = initiator.Position.DistanceTo(recipient.Position);
                        ConversationMetrics.Add(initiator, recipient, cachedTopic, poolDist, poolDist, 0, 0, "pool");

                        // Queue next background pre-generation to refill the cache
                        QueuePreGeneration(initiator, recipient, intDef);
                        return;
                    }
                }
            }

            // Cache miss / caching disabled / game is sped up
            GenerateConversationAndApply(initiator, recipient, intDef, conversation, isSpedUp);

            if (useCache)
            {
                // Queue a pre-generation in the background to refill cache for next time
                QueuePreGeneration(initiator, recipient, intDef);
            }
        }

        private class LlmStatementResponse
        {
            public string text { get; set; }
        }

        private static void PerformSequentialDialogueGeneration(Pawn initiator, Pawn recipient, InteractionDef intDef, Action<LlmConversationResponse, ChatTopicDef, bool> onComplete)
        {
            var worldComp = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            if (worldComp == null) return;

            string idA = initiator.ThingID;
            string idB = recipient.ThingID;
            PawnConversation conversation = worldComp.pawnConversations.FirstOrDefault(c => 
                (c.pawnAId == idA && c.pawnBId == idB) || (c.pawnAId == idB && c.pawnBId == idA));

            int currentTick = Find.TickManager.TicksGame;
            bool isContinuation = false;
            
            if (conversation != null && (currentTick - conversation.lastTick < 2500) && conversation.messages.Count > 0)
            {
                // 20% chance of continuation if last chat was less than an hour ago
                if (Rand.Value < 0.20f)
                {
                    isContinuation = true;
                }
            }

            // Avoid repeating this pair's recent topics, and diversify their pre-gen pool.
            var avoidTopics = new HashSet<string>();
            if (conversation?.recentTopics != null) avoidTopics.UnionWith(conversation.recentTopics);
            if (worldComp != null) avoidTopics.UnionWith(worldComp.PoolTopicsForPair(idA, idB));
            ChatTopicDef topic = SelectRandomTopic(intDef, avoidTopics);

            // 1. Compile Initiator context
            float restLevel = initiator.needs?.rest?.CurLevel ?? 1f;
            string exhaustionState = restLevel < 0.3f ? "extremely exhausted/tired" : (restLevel < 0.6f ? "moderately tired" : "well-rested");

            string lastEaten = "unknown";
            if (initiator.needs?.mood?.thoughts?.memories?.Memories != null)
            {
                var foodThought = initiator.needs.mood.thoughts.memories.Memories
                    .FirstOrDefault(m => m.def.defName.StartsWith("Ate", StringComparison.OrdinalIgnoreCase));
                if (foodThought != null)
                {
                    lastEaten = foodThought.def.label ?? foodThought.def.defName;
                }
            }

            var topThoughts = new List<string>();
            if (initiator.needs?.mood?.thoughts?.memories?.Memories != null)
            {
                var activeMemories = initiator.needs.mood.thoughts.memories.Memories
                    .Where(m => m.def.stages != null && m.def.stages.Count > 0)
                    .OrderByDescending(m => Math.Abs(m.MoodOffset()))
                    .Take(3)
                    .Select(m => m.def.stages[m.CurStageIndex]?.label ?? m.def.label ?? m.def.defName)
                    .ToList();
                topThoughts.AddRange(activeMemories);
            }
            string thoughtsCsv = topThoughts.Count > 0 ? string.Join(", ", topThoughts) : "none";
            string initiatorMood = initiator.needs?.mood?.CurLevelPercentage.ToStringPercent() ?? "50%";
            string moodContext = $"{initiatorMood} (thoughts: {thoughtsCsv})";

            var initCore = initiator.TryGetComp<SynapseCorePawnComp>();
            string initPsych = initCore != null && initCore.llmTraits != null ? string.Join(", ", initCore.llmTraits) : "none";
            string initiatorTraits = string.Join(", ", initiator.story?.traits?.allTraits?.Select(t => t.Label) ?? Enumerable.Empty<string>());
            int initOpinionOfRecip = initiator.relations?.OpinionOf(recipient) ?? 0;
            string weather = initiator.Map?.weatherManager?.curWeather?.label ?? "clear";

            // Extract primary job
            string primaryJob = "standing around";
            string jobSummary = initCore?.GetRecentJobsSummary();
            if (!string.IsNullOrEmpty(jobSummary))
            {
                int commaIdx = jobSummary.IndexOf(',');
                primaryJob = commaIdx != -1 ? jobSummary.Substring(0, commaIdx).Trim() : jobSummary.Trim();
            }

            // Recent-event line via the 0.7.1 memory tiers: today's events for chit-chat, long-standing
            // burdens for deep talk (replaces the old "newest memory by tick"). Topic contextKeys add
            // targeted live data (owned room, worn apparel, grief/trauma memories, relationship, etc.).
            bool isDeepTalk = topic != null && topic.isDeepTalk;
            string memoryLine = ConversationContextResolver.BaseMemoryLine(initCore, isDeepTalk) ?? "none";
            string topicContext = topic != null
                ? ConversationContextResolver.ResolveAll(topic.contextKeys, initiator, recipient, initCore)
                : "";

            string systemPrompt = "";
            if (isContinuation)
            {
                // Build history context
                string historyText = string.Join("\n", conversation.messages.Skip(Math.Max(0, conversation.messages.Count - 5)).Take(5).Select(m => 
                    $"{(m.sender == idA ? initiator.Name.ToStringShort : recipient.Name.ToStringShort)}: {m.message}"));
                
                systemPrompt = $"You are simulating the RimWorld pawn {initiator.Name.ToStringShort}.\n" +
                    $"Personality: {initiatorTraits} (Psychology: {initPsych})\n" +
                    $"Current Mood: {moodContext}\n" +
                    $"Exhaustion: {exhaustionState}\n" +
                    $"Last Eaten: {lastEaten}\n" +
                    $"Opinion of {recipient.Name.ToStringShort}: {initOpinionOfRecip} (-100 to 100)\n\n" +
                    $"Recent Chat History:\n{historyText}\n\n" +
                    $"This conversation is a direct continuation of the chat above. Write exactly 1 brief statement (1-2 sentences, under 20 words) from {initiator.Name.ToStringShort} to continue the conversation naturally. " +
                    $"Focus on responding directly to what was said previously. Do NOT include names/labels or formatting. Return strictly valid JSON: {{ \"text\": \"your statement\" }}";
            }
            else
            {
                string topicPrompt = topic != null ? topic.prompts.RandomElement() : "small talk";
                string ctxBlock = string.IsNullOrEmpty(topicContext) ? "" : $"\nContext:\n{topicContext}";

                if (isDeepTalk)
                {
                    // Deep talk maximizes the context budget: personality summary, weighed memories, and
                    // the topic's targeted live data feed an intimate, from-the-heart exchange.
                    systemPrompt = $"You are simulating the RimWorld pawn {initiator.Name.ToStringShort} in a private, meaningful conversation with {recipient.Name.ToStringShort}.\n" +
                        $"Personality: {initiatorTraits} (Psychology: {initPsych})\n" +
                        $"Current Mood: {moodContext}\n" +
                        $"Weighing on them: {memoryLine}\n" +
                        $"Opinion of {recipient.Name.ToStringShort}: {initOpinionOfRecip} (-100 to 100){ctxBlock}\n\n" +
                        $"Open up about strictly this: {topicPrompt}.\n" +
                        $"Speak personally and from the heart, drawing on the memories and context above. Write 1-2 sentences (under 30 words). Do NOT mention weather, meals, or chores. " +
                        $"Do NOT include names/labels or formatting. Return strictly valid JSON: {{ \"text\": \"your statement\" }}";
                }
                else
                {
                    // Chit-chat stays lean and cheap.
                    systemPrompt = $"You are simulating the RimWorld pawn {initiator.Name.ToStringShort}.\n" +
                        $"Personality: {initiatorTraits} (Psychology: {initPsych})\n" +
                        $"Current Mood: {moodContext}\n" +
                        $"Exhaustion: {exhaustionState}\n" +
                        $"Last Eaten: {lastEaten}\n" +
                        $"Current Weather: {weather}\n" +
                        $"Most Common 24h Activity: {primaryJob}\n" +
                        $"Recent Event: {memoryLine}{ctxBlock}\n" +
                        $"Opinion of {recipient.Name.ToStringShort}: {initOpinionOfRecip} (-100 to 100)\n\n" +
                        $"Write exactly 1 brief statement (1-2 sentences, under 20 words) directed at {recipient.Name.ToStringShort}.\n" +
                        $"The topic is strictly: {topicPrompt}.\n" +
                        $"Use your mood, chores, weather, or energy level as flavor ONLY if the topic is general chitchat. If the topic is specific, focus purely on that topic and do NOT mention the weather, meals, or your chores. " +
                        $"Do NOT include names/labels or formatting. Return strictly valid JSON: {{ \"text\": \"your statement\" }}";
                }
            }

            var apiMessages = new List<ChatMessage> { new ChatMessage { role = "system", content = systemPrompt } };

            SynapseClient.ChatAsync(
                RimSynapseConversationsMod.ModHandle,
                apiMessages,
                new ChatOptions 
                { 
                    priority = 1, 
                    requestName = "Chitchat Statement A", 
                    targetName = $"{initiator.Name.ToStringShort} to {recipient.Name.ToStringShort}" 
                },
                resultA =>
                {
                    if (!resultA.success || string.IsNullOrEmpty(resultA.content))
                    {
                        onComplete(null, topic, isContinuation);
                        return;
                    }

                    string cleanA = ExtractJson(resultA.content);
                    string statementA = "";
                    try
                    {
                        var parsedA = JsonConvert.DeserializeObject<LlmStatementResponse>(cleanA);
                        statementA = parsedA?.text;
                    }
                    catch
                    {
                        statementA = cleanA;
                    }

                    if (string.IsNullOrEmpty(statementA))
                    {
                        onComplete(null, topic, isContinuation);
                        return;
                    }

                    // 2. Recipient response generation (B responding to A)
                    float B_restLevel = recipient.needs?.rest?.CurLevel ?? 1f;
                    string B_exhaustionState = B_restLevel < 0.3f ? "extremely exhausted/tired" : (B_restLevel < 0.6f ? "moderately tired" : "well-rested");

                    string B_lastEaten = "unknown";
                    if (recipient.needs?.mood?.thoughts?.memories?.Memories != null)
                    {
                        var foodThought = recipient.needs.mood.thoughts.memories.Memories
                            .FirstOrDefault(m => m.def.defName.StartsWith("Ate", StringComparison.OrdinalIgnoreCase));
                        if (foodThought != null)
                        {
                            B_lastEaten = foodThought.def.label ?? foodThought.def.defName;
                        }
                    }

                    var B_topThoughts = new List<string>();
                    if (recipient.needs?.mood?.thoughts?.memories?.Memories != null)
                    {
                        var activeMemories = recipient.needs.mood.thoughts.memories.Memories
                            .Where(m => m.def.stages != null && m.def.stages.Count > 0)
                            .OrderByDescending(m => Math.Abs(m.MoodOffset()))
                            .Take(3)
                            .Select(m => m.def.stages[m.CurStageIndex]?.label ?? m.def.label ?? m.def.defName)
                            .ToList();
                        B_topThoughts.AddRange(activeMemories);
                    }
                    string B_thoughtsCsv = B_topThoughts.Count > 0 ? string.Join(", ", B_topThoughts) : "none";
                    string recipientMood = recipient.needs?.mood?.CurLevelPercentage.ToStringPercent() ?? "50%";
                    string B_moodContext = $"{recipientMood} (thoughts: {B_thoughtsCsv})";

                    var recipCore = recipient.TryGetComp<SynapseCorePawnComp>();
                    string recipPsych = recipCore != null && recipCore.llmTraits != null ? string.Join(", ", recipCore.llmTraits) : "none";
                    string recipientTraits = string.Join(", ", recipient.story?.traits?.allTraits?.Select(t => t.Label) ?? Enumerable.Empty<string>());
                    int recipOpinionOfInit = recipient.relations?.OpinionOf(initiator) ?? 0;

                    string promptB = $"You are simulating the RimWorld pawn {recipient.Name.ToStringShort} responding to {initiator.Name.ToStringShort}.\n\n" +
                        $"Context prompt:\n" +
                        $"{initiator.Name.ToStringShort} : {statementA} : {recipOpinionOfInit} : {recipientTraits} (Psychology: {recipPsych}) : {B_moodContext} (Exhaustion: {B_exhaustionState}, Last Eaten: {B_lastEaten})\n\n" +
                        $"Based on this context, craft exactly 1 or 2 sentences to respond to {initiator.Name.ToStringShort}'s statement. " +
                        $"Also estimate the social impact of this interaction: set trustOffset (-2.0 to 2.0), familiarityOffset (1.0 to 3.0), and affinityOffset (-2.0 to 2.0).\n" +
                        $"Do NOT include names/labels or formatting. Return strictly valid JSON with this schema:\n" +
                        $"{{\n" +
                        $"  \"text\": \"your response statement\",\n" +
                        $"  \"trustOffset\": 0.0,\n" +
                        $"  \"familiarityOffset\": 0.0,\n" +
                        $"  \"affinityOffset\": 0.0\n" +
                        $"}}";

                    var apiMessagesB = new List<ChatMessage> { new ChatMessage { role = "system", content = promptB } };

                    SynapseClient.ChatAsync(
                        RimSynapseConversationsMod.ModHandle,
                        apiMessagesB,
                        new ChatOptions 
                        { 
                            priority = 1, 
                            requestName = "Chitchat Response B", 
                            targetName = $"{recipient.Name.ToStringShort} to {initiator.Name.ToStringShort}" 
                        },
                        resultB =>
                        {
                            if (!resultB.success || string.IsNullOrEmpty(resultB.content))
                            {
                                onComplete(null, topic, isContinuation);
                                return;
                            }

                            string cleanB = ExtractJson(resultB.content);
                            try
                            {
                                var parsedB = JsonConvert.DeserializeObject<LlmConversationResponse>(cleanB);
                                if (parsedB != null)
                                {
                                    // Package as exactly 2 lines of dialogue
                                    parsedB.dialogue = new List<LlmDialogueLine>
                                    {
                                        new LlmDialogueLine { sender = initiator.Name.ToStringShort, text = statementA },
                                        new LlmDialogueLine { sender = recipient.Name.ToStringShort, text = parsedB.text }
                                    };
                                    onComplete(parsedB, topic, isContinuation);
                                }
                                else
                                {
                                    onComplete(null, topic, isContinuation);
                                }
                            }
                            catch
                            {
                                onComplete(null, topic, isContinuation);
                            }
                        }
                    );
                }
            );
        }

        private static void QueuePreGeneration(Pawn initiator, Pawn recipient, InteractionDef intDef)
        {
            if (initiator == null || recipient == null || !initiator.Spawned || !recipient.Spawned) return;

            string idA = initiator.ThingID;
            string idB = recipient.ThingID;

            var worldComp = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            if (worldComp == null || !worldComp.PairNeedsFill(idA, idB)) return;

            PerformSequentialDialogueGeneration(initiator, recipient, intDef, (parsed, topic, isContinuation) =>
            {
                if (parsed != null && parsed.dialogue != null && parsed.dialogue.Count == 2)
                {
                    var preGen = new PreGeneratedConversation
                    {
                        initiatorId = idA,
                        recipientId = idB,
                        initiatorStatement = parsed.dialogue[0].text,
                        recipientResponse = parsed.dialogue[1].text,
                        topicDefName = topic?.defName ?? "Social",
                        isContinuation = isContinuation,
                        generatedAtTick = Find.TickManager.TicksGame,
                        generatedAtAbsTick = Find.TickManager.TicksAbs,
                        trustOffset = parsed.trustOffset,
                        familiarityOffset = parsed.familiarityOffset,
                        affinityOffset = parsed.affinityOffset
                    };
                    worldComp.AddToPool(preGen);
                }
            });
        }

        /// <summary>
        /// Proactively top up the pre-seed pool on idle cycles (called from the world component's rare
        /// tick): pairs that have been talking and still have room get one more low-priority pre-gen,
        /// biased toward deep talk since it's the expensive, stall-prone path. Bounded per pass.
        /// </summary>
        /// <summary>Force a live conversation between two pawns now (playtest / debug trigger, #29).</summary>
        public static void ForceConversation(Pawn initiator, Pawn recipient, InteractionDef intDef)
        {
            if (initiator == null || recipient == null || intDef == null) return;
            TriggerLlmDialogue(initiator, recipient, intDef);
        }

        public static void TryTopUpPreGenPool(SynapseConversationsWorldComponent worldComp)
        {
            if (worldComp == null || worldComp.PoolAtTotalCap) return;
            if (!(RimSynapseConversationsMod.Settings?.experimentalPreSeeding ?? false)) return;

            const int maxPerPass = 2;
            int queued = 0;
            foreach (var conv in worldComp.pawnConversations)
            {
                if (queued >= maxPerPass || worldComp.PoolAtTotalCap) break;
                if (conv == null || !worldComp.PairNeedsFill(conv.pawnAId, conv.pawnBId)) continue;

                var a = SynapseConversationsWorldComponent.PawnFromId(conv.pawnAId);
                var b = SynapseConversationsWorldComponent.PawnFromId(conv.pawnBId);
                if (a == null || b == null || a.Dead || b.Dead) continue;

                var intDef = Rand.Value < 0.5f ? InteractionDefOf.DeepTalk : InteractionDefOf.Chitchat;
                QueuePreGeneration(a, b, intDef);
                queued++;
            }
        }

        private static void GenerateConversationAndApply(Pawn initiator, Pawn recipient, InteractionDef intDef, PawnConversation conversation, bool isSpedUp)
        {
            // Playtest instrumentation (#29): stamp the request so we can measure how much in-game time
            // (and wall-clock) passes before the first message lands, and how far the pawns drift.
            int startTick = Find.TickManager.TicksGame;
            var latencySw = System.Diagnostics.Stopwatch.StartNew();
            float startDist = (initiator.Spawned && recipient.Spawned)
                ? initiator.Position.DistanceTo(recipient.Position) : -1f;

            PerformSequentialDialogueGeneration(initiator, recipient, intDef, (parsed, topic, isContinuation) =>
            {
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

                        if (parsed.dialogue.Count == 2 && Find.TickManager.CurTimeSpeed == TimeSpeed.Normal)
                        {
                            UI.SpeechBubbleManager.AddBubble(initiator, recipient, parsed.dialogue[0].text, 0, 4.5f);
                            UI.SpeechBubbleManager.AddBubble(recipient, initiator, parsed.dialogue[1].text, 270, 4.5f);
                        }

                        while (conversation.messages.Count > 50)
                        {
                            conversation.messages.RemoveAt(0);
                        }

                        ApplyPsychologyOffsets(initiator, recipient, parsed.trustOffset, parsed.familiarityOffset);
                        ApplyVanillaAffinityThought(initiator, recipient, parsed.affinityOffset);

                        string topicName = (topic != null) ? topic.topicName : "Social";
                        PropagateContextMemories(initiator, recipient, topicName, firstLine);
                        conversation.PushRecentTopic(topic?.defName);

                        latencySw.Stop();
                        float endDist = (initiator.Spawned && recipient.Spawned)
                            ? initiator.Position.DistanceTo(recipient.Position) : startDist;
                        ConversationMetrics.Add(initiator, recipient, topic, startDist, endDist,
                            Find.TickManager.TicksGame - startTick, latencySw.ElapsedMilliseconds, "live");
                    });
                }
                else
                {
                    TriggerFallback(initiator, recipient, intDef, conversation);
                }
            });
        }

        private static ChatTopicDef SelectRandomTopic(InteractionDef intDef, ICollection<string> avoid)
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

            // Anti-repetition: prefer topics this pair has NOT used recently (and that aren't already
            // sitting in their pre-gen pool), so consecutive conversations don't land on the same topic.
            // Only fall back to the full set if avoidance would leave nothing to pick.
            if (avoid != null && avoid.Count > 0)
            {
                var fresh = candidates.Where(t => !avoid.Contains(t.defName)).ToList();
                if (fresh.Count > 0) candidates = fresh;
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

        private static string ExtractJson(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;
            
            int firstBrace = content.IndexOf('{');
            int lastBrace = content.LastIndexOf('}');
            
            if (firstBrace != -1 && lastBrace != -1 && lastBrace > firstBrace)
            {
                return content.Substring(firstBrace, lastBrace - firstBrace + 1);
            }
            
            return content;
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

        public static void TriggerEnvironmentalLlmDialogue(Pawn initiator, Pawn recipient, string type, string description)
        {
            var worldComp = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            if (worldComp == null) return;

            string idA = initiator.ThingID;
            string idB = recipient.ThingID;
            PawnConversation conversation = worldComp.pawnConversations.FirstOrDefault(c => 
                (c.pawnAId == idA && c.pawnBId == idB) || (c.pawnAId == idB && c.pawnBId == idA));

            int currentTick = Find.TickManager.TicksGame;

            if (conversation == null)
            {
                conversation = new PawnConversation(idA, idB, currentTick);
                worldComp.pawnConversations.Add(conversation);
            }

            // 1. Compile Initiator context
            float restLevel = initiator.needs?.rest?.CurLevel ?? 1f;
            string exhaustionState = restLevel < 0.3f ? "extremely exhausted/tired" : (restLevel < 0.6f ? "moderately tired" : "well-rested");

            string lastEaten = "unknown";
            if (initiator.needs?.mood?.thoughts?.memories?.Memories != null)
            {
                var foodThought = initiator.needs.mood.thoughts.memories.Memories
                    .FirstOrDefault(m => m.def.defName.StartsWith("Ate", StringComparison.OrdinalIgnoreCase));
                if (foodThought != null)
                {
                    lastEaten = foodThought.def.label ?? foodThought.def.defName;
                }
            }

            var topThoughts = new List<string>();
            if (initiator.needs?.mood?.thoughts?.memories?.Memories != null)
            {
                var activeMemories = initiator.needs.mood.thoughts.memories.Memories
                    .Where(m => m.def.stages != null && m.def.stages.Count > 0)
                    .OrderByDescending(m => Math.Abs(m.MoodOffset()))
                    .Take(3)
                    .Select(m => m.def.stages[m.CurStageIndex]?.label ?? m.def.label ?? m.def.defName)
                    .ToList();
                topThoughts.AddRange(activeMemories);
            }
            string thoughtsCsv = topThoughts.Count > 0 ? string.Join(", ", topThoughts) : "none";
            string initiatorMood = initiator.needs?.mood?.CurLevelPercentage.ToStringPercent() ?? "50%";
            string moodContext = $"{initiatorMood} (thoughts: {thoughtsCsv})";

            var initCore = initiator.TryGetComp<SynapseCorePawnComp>();
            string initPsych = initCore != null && initCore.llmTraits != null ? string.Join(", ", initCore.llmTraits) : "none";
            string initiatorTraits = string.Join(", ", initiator.story?.traits?.allTraits?.Select(t => t.Label) ?? Enumerable.Empty<string>());
            int initOpinionOfRecip = initiator.relations?.OpinionOf(recipient) ?? 0;

            string systemPrompt = $"You are simulating the RimWorld pawn {initiator.Name.ToStringShort}.\n" +
                $"Personality: {initiatorTraits} (Psychology: {initPsych})\n" +
                $"Current Mood: {moodContext}\n" +
                $"Opinion of {recipient.Name.ToStringShort}: {initOpinionOfRecip} (-100 to 100)\n\n" +
                $"Write exactly 1 brief statement (1-2 sentences, under 20 words) directed at {recipient.Name.ToStringShort}.\n" +
                $"The topic is strictly: {description}.\n" +
                $"Do NOT include names/labels or formatting. Return strictly valid JSON: {{ \"text\": \"your statement\" }}";

            var apiMessages = new List<ChatMessage> { new ChatMessage { role = "system", content = systemPrompt } };

            SynapseClient.ChatAsync(
                RimSynapseConversationsMod.ModHandle,
                apiMessages,
                new ChatOptions 
                { 
                    priority = 1, 
                    requestName = $"Env Statement A ({type})", 
                    targetName = $"{initiator.Name.ToStringShort} to {recipient.Name.ToStringShort}" 
                },
                resultA =>
                {
                    if (!resultA.success || string.IsNullOrEmpty(resultA.content)) return;

                    string cleanA = ExtractJson(resultA.content);
                    string statementA = "";
                    try
                    {
                        var parsedA = JsonConvert.DeserializeObject<LlmStatementResponse>(cleanA);
                        statementA = parsedA?.text;
                    }
                    catch
                    {
                        statementA = cleanA;
                    }

                    if (string.IsNullOrEmpty(statementA)) return;

                    // 2. Recipient response generation
                    float B_restLevel = recipient.needs?.rest?.CurLevel ?? 1f;
                    string B_exhaustionState = B_restLevel < 0.3f ? "extremely exhausted/tired" : (B_restLevel < 0.6f ? "moderately tired" : "well-rested");

                    string B_lastEaten = "unknown";
                    if (recipient.needs?.mood?.thoughts?.memories?.Memories != null)
                    {
                        var foodThought = recipient.needs.mood.thoughts.memories.Memories
                            .FirstOrDefault(m => m.def.defName.StartsWith("Ate", StringComparison.OrdinalIgnoreCase));
                        if (foodThought != null)
                        {
                            B_lastEaten = foodThought.def.label ?? foodThought.def.defName;
                        }
                    }

                    var B_topThoughts = new List<string>();
                    if (recipient.needs?.mood?.thoughts?.memories?.Memories != null)
                    {
                        var activeMemories = recipient.needs.mood.thoughts.memories.Memories
                            .Where(m => m.def.stages != null && m.def.stages.Count > 0)
                            .OrderByDescending(m => Math.Abs(m.MoodOffset()))
                            .Take(3)
                            .Select(m => m.def.stages[m.CurStageIndex]?.label ?? m.def.label ?? m.def.defName)
                            .ToList();
                        B_topThoughts.AddRange(activeMemories);
                    }
                    string B_thoughtsCsv = B_topThoughts.Count > 0 ? string.Join(", ", B_topThoughts) : "none";
                    string recipientMood = recipient.needs?.mood?.CurLevelPercentage.ToStringPercent() ?? "50%";
                    string B_moodContext = $"{recipientMood} (thoughts: {B_thoughtsCsv})";

                    var recipCore = recipient.TryGetComp<SynapseCorePawnComp>();
                    string recipPsych = recipCore != null && recipCore.llmTraits != null ? string.Join(", ", recipCore.llmTraits) : "none";
                    string recipientTraits = string.Join(", ", recipient.story?.traits?.allTraits?.Select(t => t.Label) ?? Enumerable.Empty<string>());
                    int recipOpinionOfInit = recipient.relations?.OpinionOf(initiator) ?? 0;

                    string promptB = $"You are simulating the RimWorld pawn {recipient.Name.ToStringShort} responding to {initiator.Name.ToStringShort}.\n\n" +
                        $"Context prompt:\n" +
                        $"{initiator.Name.ToStringShort} : {statementA} : {recipOpinionOfInit} : {recipientTraits} (Psychology: {recipPsych}) : {B_moodContext} (Exhaustion: {B_exhaustionState}, Last Eaten: {B_lastEaten})\n\n" +
                        $"Based on this context, craft exactly 1 or 2 sentences to respond to {initiator.Name.ToStringShort}'s statement about the environmental trigger.\n" +
                        $"Also estimate the social impact of this interaction: set trustOffset (-1.0 to 1.0), familiarityOffset (1.0 to 2.0), and affinityOffset (-1.0 to 1.0).\n" +
                        $"Do NOT include names/labels or formatting. Return strictly valid JSON with this schema:\n" +
                        $"{{\n" +
                        $"  \"text\": \"your response statement\",\n" +
                        $"  \"trustOffset\": 0.0,\n" +
                        $"  \"familiarityOffset\": 0.0,\n" +
                        $"  \"affinityOffset\": 0.0\n" +
                        $"}}";

                    var apiMessagesB = new List<ChatMessage> { new ChatMessage { role = "system", content = promptB } };

                    SynapseClient.ChatAsync(
                        RimSynapseConversationsMod.ModHandle,
                        apiMessagesB,
                        new ChatOptions 
                        { 
                            priority = 1, 
                            requestName = $"Env Response B", 
                            targetName = $"{recipient.Name.ToStringShort} to {initiator.Name.ToStringShort}" 
                        },
                        resultB =>
                        {
                            if (!resultB.success || string.IsNullOrEmpty(resultB.content)) return;

                            string cleanB = ExtractJson(resultB.content);
                            try
                            {
                                var parsedB = JsonConvert.DeserializeObject<LlmConversationResponse>(cleanB);
                                if (parsedB != null)
                                {
                                    SynapseGameComponent.Enqueue(() =>
                                    {
                                        if (!initiator.Spawned || initiator.Dead || !recipient.Spawned || recipient.Dead) return;

                                        conversation.messages.Add(new SynapseConversationMessage(initiator.ThingID, statementA, Find.TickManager.TicksGame));
                                        conversation.messages.Add(new SynapseConversationMessage(recipient.ThingID, parsedB.text, Find.TickManager.TicksGame));
                                        conversation.lastTick = Find.TickManager.TicksGame;

                                        while (conversation.messages.Count > 50)
                                        {
                                            conversation.messages.RemoveAt(0);
                                        }

                                        if (Find.TickManager.CurTimeSpeed == TimeSpeed.Normal)
                                        {
                                            UI.SpeechBubbleManager.AddBubble(initiator, recipient, statementA, 0, 4.5f);
                                            UI.SpeechBubbleManager.AddBubble(recipient, initiator, parsedB.text, 270, 4.5f);
                                        }

                                        ApplyPsychologyOffsets(initiator, recipient, parsedB.trustOffset, parsedB.familiarityOffset);
                                        ApplyVanillaAffinityThought(initiator, recipient, parsedB.affinityOffset);
                                        PropagateContextMemories(initiator, recipient, "Environment", statementA);
                                    });
                                }
                            }
                            catch
                            {
                            }
                        }
                    );
                }
            );
        }
    }
}
