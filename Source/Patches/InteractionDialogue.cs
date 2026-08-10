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

        /// <summary>Single-call multi-line exchange (Conversations#31): a whole back-and-forth plus the
        /// social offsets in ONE LLM round-trip. Lines alternate speakers starting with the initiator;
        /// the first lands immediately and the rest drip-feed while the pawns stay in range.</summary>
        private class LlmExchangeResponse
        {
            public List<string> lines { get; set; }
            public float trustOffset { get; set; }
            public float familiarityOffset { get; set; }
            public float affinityOffset { get; set; }
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

        // One call yields a whole alternating back-and-forth (the LLM sizes it to the scenario); the
        // first line lands now and the rest drip-feed while the pawns stay together (Conversations#31).
        private const int MaxExchangeLines = 12;

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
                // 20% chance of continuation if the last chat was recent
                if (Rand.Value < 0.20f) isContinuation = true;
            }

            // Avoid repeating this pair's recent topics, and diversify their pre-gen pool.
            var avoidTopics = new HashSet<string>();
            if (conversation?.recentTopics != null) avoidTopics.UnionWith(conversation.recentTopics);
            avoidTopics.UnionWith(worldComp.PoolTopicsForPair(idA, idB));
            ChatTopicDef topic = SelectRandomTopic(intDef, avoidTopics);
            bool isDeepTalk = topic != null && topic.isDeepTalk;

            // Single-call generation (Conversations#31): one LLM round-trip emits the WHOLE alternating
            // exchange (~TargetExchangeLines lines) plus the social offsets, instead of two sequential
            // calls per line. Downstream shows the first line immediately and drip-feeds the rest while
            // the pawns stay in range. The reply is still generated live (never pre-canned), satisfying
            // #29's "response must always be dynamic".
            var initCore = initiator.TryGetComp<SynapseCorePawnComp>();

            // Chit-chat lets ambient flavor (weather/meals/chores) in only for general small talk; deep
            // talk and specific topics stay focused (mirrors the previous per-call instructions).
            string initCtx = BuildParticipantContext(initiator, recipient, includeAmbient: !isDeepTalk, isDeepTalk: isDeepTalk);
            string recipCtx = BuildParticipantContext(recipient, initiator, includeAmbient: !isDeepTalk, isDeepTalk: isDeepTalk);

            string topicPrompt = topic != null ? topic.prompts.RandomElement() : "small talk";
            string topicContext = topic != null
                ? ConversationContextResolver.ResolveAll(topic.contextKeys, initiator, recipient, initCore)
                : "";
            string ctxBlock = string.IsNullOrEmpty(topicContext) ? "" : $"\nTopic context:\n{topicContext}";

            string historyBlock = "";
            if (isContinuation)
            {
                string historyText = string.Join("\n", conversation.messages
                    .Skip(Math.Max(0, conversation.messages.Count - 5)).Take(5)
                    .Select(m => $"{(m.sender == idA ? initiator.Name.ToStringShort : recipient.Name.ToStringShort)}: {m.message}"));
                historyBlock = $"\nRecent chat history (continue naturally from this, do not repeat it):\n{historyText}\n";
            }

            // The LLM decides how many lines the exchange naturally needs from its pace/depth: a quick
            // comment in passing might be 2-3 lines; a deep heart-to-heart runs longer. Length is NOT
            // fixed by us (Conversations#31) — we only cap it (MaxExchangeLines) as a safety bound.
            string scenario = isDeepTalk
                ? $"a longer, private heart-to-heart where {initiator.Name.ToStringShort} and {recipient.Name.ToStringShort} really open up and go back and forth several times"
                : $"a quick, casual conversation in passing between {initiator.Name.ToStringShort} and {recipient.Name.ToStringShort} — a few lines traded, then it wraps up naturally";
            string styleRule = isDeepTalk
                ? "Each line is 1-2 sentences (under 30 words), personal and from the heart, drawing on the context above. Do NOT mention weather, meals, or chores."
                : "Each line is 1 brief statement (1-2 sentences, under 20 words). Use mood, chores, weather or energy as flavor ONLY if the topic is general chitchat; for a specific topic focus on it and do NOT mention weather, meals, or chores.";

            // Quality guidance (Conversations#31 playtest): a small local model defaults to flat,
            // interchangeable filler. Push for distinct voices, concreteness, and a one-shot exemplar.
            string voiceRule =
                $"Write it like real people talking, not a status report. {initiator.Name.ToStringShort} and {recipient.Name.ToStringShort} must sound like DIFFERENT people — let their traits, mood and opinion of each other shape word choice, humour and bluntness. " +
                "Be concrete: react to what the other actually just said, use specifics, let them tease, disagree, or trail off. " +
                "BANNED as lazy filler — never write lines like \"we just have to\", \"it is what it is\", \"manage what we have\", \"it's fine\", \"that's just how it is\", or generic resignation. Give them an actual point of view.";
            string exampleBlock = isDeepTalk
                ? "Style example (match the specificity and distinct voices, NOT the content):\n" +
                  "[\"I still count the ones we buried. I don't know how you sleep.\", \"I don't, mostly. I just stopped letting you see it.\", \"...you could have. I'd have sat up with you.\"]\n\n"
                : "Style example (match the specificity and distinct voices, NOT the content):\n" +
                  "[\"You reorganised my whole workbench again, didn't you.\", \"It was chaos. You'll thank me when you can find a screwdriver for once.\", \"I knew where everything was! ...fine, where'd you put the pliers.\"]\n\n";

            string systemPrompt =
                $"You are writing {scenario}.\n" +
                $"{initCtx}\n{recipCtx}\n{historyBlock}" +
                $"Topic (strictly): {topicPrompt}.{ctxBlock}\n\n" +
                $"{voiceRule}\n{exampleBlock}" +
                $"Decide how many lines the exchange naturally needs and end it when it feels done — do NOT pad it. " +
                $"Alternate speakers, STARTING with {initiator.Name.ToStringShort} (odd lines are {initiator.Name.ToStringShort}, even lines are {recipient.Name.ToStringShort}). {styleRule} " +
                $"Let it read like a real conversation that builds and closes naturally (at most {MaxExchangeLines} lines). Do NOT include names, labels, or quotation marks inside the line text. " +
                $"Also estimate the overall social impact on {recipient.Name.ToStringShort}: trustOffset (-2.0 to 2.0), familiarityOffset (1.0 to 3.0), affinityOffset (-2.0 to 2.0).\n" +
                $"Return strictly valid JSON with this schema:\n" +
                $"{{\n" +
                $"  \"lines\": [\"{initiator.Name.ToStringShort}'s first line\", \"{recipient.Name.ToStringShort}'s reply\", \"...as many as it needs...\"],\n" +
                $"  \"trustOffset\": 0.0,\n" +
                $"  \"familiarityOffset\": 0.0,\n" +
                $"  \"affinityOffset\": 0.0\n" +
                $"}}";

            var apiMessages = new List<ChatMessage> { new ChatMessage { role = "system", content = systemPrompt } };

            SynapseClient.ChatAsync(
                RimSynapseConversationsMod.ModHandle,
                apiMessages,
                new ChatOptions
                {
                    priority = 1,
                    requestName = isDeepTalk ? "Deep talk exchange" : "Chitchat exchange",
                    targetName = $"{initiator.Name.ToStringShort} & {recipient.Name.ToStringShort}"
                },
                result =>
                {
                    if (!result.success || string.IsNullOrEmpty(result.content))
                    {
                        onComplete(null, topic, isContinuation);
                        return;
                    }

                    string clean = ExtractJson(result.content);
                    LlmExchangeResponse ex = null;
                    try { ex = JsonConvert.DeserializeObject<LlmExchangeResponse>(clean); }
                    catch { ex = null; }

                    var cleanLines = ex?.lines?
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Select(l => l.Trim())
                        .Take(MaxExchangeLines)
                        .ToList();

                    if (cleanLines == null || cleanLines.Count == 0)
                    {
                        onComplete(null, topic, isContinuation);
                        return;
                    }

                    // Assign speakers by position: line 0 = initiator, then strictly alternating.
                    var dialogue = new List<LlmDialogueLine>();
                    for (int i = 0; i < cleanLines.Count; i++)
                    {
                        Pawn speaker = (i % 2 == 0) ? initiator : recipient;
                        dialogue.Add(new LlmDialogueLine { sender = speaker.Name.ToStringShort, text = cleanLines[i] });
                    }

                    var parsed = new LlmConversationResponse
                    {
                        dialogue = dialogue,
                        trustOffset = ex.trustOffset,
                        familiarityOffset = ex.familiarityOffset,
                        affinityOffset = ex.affinityOffset
                    };
                    onComplete(parsed, topic, isContinuation);
                }
            );
        }

        /// <summary>Compact one participant's context for the single-call exchange prompt. Ambient flavor
        /// (last meal, weather, recent activity) is included only when <paramref name="includeAmbient"/> is
        /// set (general chit-chat), keeping specific and deep-talk prompts focused.</summary>
        private static string BuildParticipantContext(Pawn p, Pawn other, bool includeAmbient, bool isDeepTalk)
        {
            var core = p.TryGetComp<SynapseCorePawnComp>();
            string psych = core?.llmTraits != null ? string.Join(", ", core.llmTraits) : "none";
            string traits = string.Join(", ", p.story?.traits?.allTraits?.Select(t => t.Label) ?? Enumerable.Empty<string>());
            int opinion = p.relations?.OpinionOf(other) ?? 0;

            float rest = p.needs?.rest?.CurLevel ?? 1f;
            string exhaustion = rest < 0.3f ? "extremely exhausted/tired" : (rest < 0.6f ? "moderately tired" : "well-rested");
            string mood = p.needs?.mood?.CurLevelPercentage.ToStringPercent() ?? "50%";

            var mems = p.needs?.mood?.thoughts?.memories?.Memories;
            var topThoughts = new List<string>();
            if (mems != null)
            {
                topThoughts.AddRange(mems
                    .Where(m => m.def.stages != null && m.def.stages.Count > 0)
                    .OrderByDescending(m => Math.Abs(m.MoodOffset()))
                    .Take(3)
                    .Select(m => m.def.stages[m.CurStageIndex]?.label ?? m.def.label ?? m.def.defName));
            }
            string thoughtsCsv = topThoughts.Count > 0 ? string.Join(", ", topThoughts) : "none";
            string memoryLine = ConversationContextResolver.BaseMemoryLine(core, isDeepTalk) ?? "none";

            // Voice (#33): Psychology-authored speaking style. When present it leads the block so the
            // model anchors on how this pawn talks; falls back to traits/psychology when absent.
            string voiceLine = !string.IsNullOrEmpty(core?.voiceProfile)
                ? $"speaks like this — {core.voiceProfile} "
                : "";

            string block = $"{p.Name.ToStringShort} — {voiceLine}personality: {traits} (Psychology: {psych}); " +
                $"mood: {mood} (thoughts: {thoughtsCsv}); exhaustion: {exhaustion}; " +
                $"opinion of {other.Name.ToStringShort}: {opinion} (-100 to 100)";

            if (includeAmbient)
            {
                string lastEaten = "unknown";
                var foodThought = mems?.FirstOrDefault(m => m.def.defName.StartsWith("Ate", StringComparison.OrdinalIgnoreCase));
                if (foodThought != null) lastEaten = foodThought.def.label ?? foodThought.def.defName;
                string weather = p.Map?.weatherManager?.curWeather?.label ?? "clear";
                string primaryJob = "standing around";
                string jobSummary = core?.GetRecentJobsSummary();
                if (!string.IsNullOrEmpty(jobSummary))
                {
                    int commaIdx = jobSummary.IndexOf(',');
                    primaryJob = commaIdx != -1 ? jobSummary.Substring(0, commaIdx).Trim() : jobSummary.Trim();
                }
                block += $"; last eaten: {lastEaten}; weather: {weather}; recent activity: {primaryJob}";
            }

            block += $"; weighing on them: {memoryLine}";
            return block;
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
                // Multi-line exchanges (#31) reduce to a 2-line pre-gen: store the first exchange pair.
                if (parsed != null && parsed.dialogue != null && parsed.dialogue.Count >= 2)
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

        /// <summary>Force a live conversation between two pawns now (playtest / debug trigger, #29).</summary>
        public static void ForceConversation(Pawn initiator, Pawn recipient, InteractionDef intDef)
        {
            if (initiator == null || recipient == null || intDef == null) return;
            TriggerLlmDialogue(initiator, recipient, intDef);
        }

        /// <summary>
        /// Proactively top up the pre-seed pool on idle cycles (called from the world component's rare
        /// tick): pairs that have been talking and still have room get one more low-priority pre-gen,
        /// biased toward deep talk since it's the expensive, stall-prone path. Bounded per pass.
        /// </summary>
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

                var chosen = Rand.Value < 0.5f ? InteractionDefOf.DeepTalk : InteractionDefOf.Chitchat;
                QueuePreGeneration(a, b, chosen);
                queued++;
            }
        }

        private static void GenerateConversationAndApply(Pawn initiator, Pawn recipient, InteractionDef intDef, PawnConversation conversation, bool isSpedUp)
        {
            // Playtest instrumentation (#29): stamp the request so we can measure how much in-game time
            // (and wall-clock) passes before the FIRST line lands, and how far the pawns drift.
            int startTick = Find.TickManager.TicksGame;
            var latencySw = System.Diagnostics.Stopwatch.StartNew();
            float startDist = (initiator.Spawned && recipient.Spawned)
                ? initiator.Position.DistanceTo(recipient.Position) : -1f;

            PerformSequentialDialogueGeneration(initiator, recipient, intDef, (parsed, topic, isContinuation) =>
            {
                if (parsed == null || parsed.dialogue == null || parsed.dialogue.Count == 0)
                {
                    TriggerFallback(initiator, recipient, intDef, conversation);
                    return;
                }

                SynapseGameComponent.Enqueue(() =>
                {
                    if (!initiator.Spawned || initiator.Dead || !recipient.Spawned || recipient.Dead) return;

                    // Resolve each generated line to its speaker's ThingID (alternating from the initiator).
                    var lines = new List<SynapseConversationMessage>();
                    foreach (var line in parsed.dialogue)
                    {
                        if (string.IsNullOrEmpty(line.text)) continue;
                        string senderId = line.sender != null && line.sender.Equals(recipient.Name.ToStringShort, StringComparison.OrdinalIgnoreCase)
                            ? recipient.ThingID
                            : initiator.ThingID;
                        lines.Add(new SynapseConversationMessage(senderId, line.text, 0));
                    }
                    if (lines.Count == 0) return;

                    // First line lands immediately; the rest drip-feed while the pair stays in range (#31).
                    int nowTick = Find.TickManager.TicksGame;
                    var first = lines[0];
                    first.gameTick = nowTick;
                    conversation.messages.Add(first);
                    conversation.lastTick = nowTick;
                    while (conversation.messages.Count > 50) conversation.messages.RemoveAt(0);

                    if (Find.TickManager.CurTimeSpeed == TimeSpeed.Normal)
                    {
                        bool fromInitiator = first.sender == initiator.ThingID;
                        Pawn speaker = fromInitiator ? initiator : recipient;
                        Pawn listener = fromInitiator ? recipient : initiator;
                        UI.SpeechBubbleManager.AddBubble(speaker, listener, first.message, fromInitiator ? 0 : 270, 4.5f);
                    }

                    ApplyPsychologyOffsets(initiator, recipient, parsed.trustOffset, parsed.familiarityOffset);
                    ApplyVanillaAffinityThought(initiator, recipient, parsed.affinityOffset);

                    string topicName = (topic != null) ? topic.topicName : "Social";
                    PropagateContextMemories(initiator, recipient, topicName, first.message);
                    conversation.PushRecentTopic(topic?.defName);

                    latencySw.Stop();
                    float endDist = (initiator.Spawned && recipient.Spawned)
                        ? initiator.Position.DistanceTo(recipient.Position) : startDist;
                    ConversationMetrics.Add(initiator, recipient, topic, startDist, endDist,
                        Find.TickManager.TicksGame - startTick, latencySw.ElapsedMilliseconds, "live");

                    // Hand the remaining lines to the map's drip-feed player.
                    if (lines.Count > 1 && initiator.Map != null)
                    {
                        var mapComp = initiator.Map.GetComponent<SynapseConversationsMapComponent>();
                        mapComp?.EnqueuePlayback(initiator, recipient, conversation, lines.GetRange(1, lines.Count - 1));
                    }
                });
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

            string initVoice = !string.IsNullOrEmpty(initCore?.voiceProfile) ? $"Speaks like this: {initCore.voiceProfile}\n" : "";
            string systemPrompt = $"You are simulating the RimWorld pawn {initiator.Name.ToStringShort}.\n" +
                initVoice +
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

                    string recipVoice = !string.IsNullOrEmpty(recipCore?.voiceProfile) ? $"{recipient.Name.ToStringShort} speaks like this: {recipCore.voiceProfile}\n" : "";
                    string promptB = $"You are simulating the RimWorld pawn {recipient.Name.ToStringShort} responding to {initiator.Name.ToStringShort}.\n\n" +
                        recipVoice +
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
