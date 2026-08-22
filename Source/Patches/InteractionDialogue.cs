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

            // Pre-staged event retelling (#35): with a chance, serve a ready-made retelling of a recent
            // event for this pair instead of generating live — consumed once (unique per pair+event).
            if ((RimSynapseConversationsMod.Settings?.preStageEventConversations ?? true) && !isSpedUp
                && Rand.Value < (RimSynapseConversationsMod.Settings?.eventPreStageFireChance ?? 0.30f))
            {
                var staged = worldComp.PopEventPreGenForPair(initiator, recipient);
                if (staged != null)
                {
                    ApplyStagedEventConversation(initiator, recipient, conversation, staged, currentTick);
                    return;
                }
            }

            // Load-adaptive shedding (#38): if the LLM can't keep up, skip the (unwatchable) generation
            // and just let the relationship evolve via the code-computed offsets (#46) — no LLM call.
            if (ShouldShedForLoad())
            {
                ApplyShedConversation(initiator, recipient, intDef, conversation, currentTick);
                return;
            }

            // Cache miss / caching disabled / game is sped up
            GenerateConversationAndApply(initiator, recipient, intDef, conversation, isSpedUp);

            if (useCache)
            {
                // Queue a pre-generation in the background to refill cache for next time
                QueuePreGeneration(initiator, recipient, intDef);
            }
        }

        /// <summary>Is the shared LLM pipeline behind enough that conversations — the lowest-value LLM
        /// consumer — should yield (Conversations#38)? A backlog in the queue or an active Core throttle
        /// both mean the model can't keep up; a bigger colony, a slower provider and higher game speed all
        /// surface here as a deeper queue, so we gate on real load rather than on speed.</summary>
        private static bool ShouldShedForLoad()
        {
            var s = RimSynapseConversationsMod.Settings;
            if (s == null || !s.adaptiveConversationShedding) return false;
            if (s.conversationQueueDepthCap > 0 && SynapseClient.TotalQueueDepth > s.conversationQueueDepthCap) return true;
            if (SynapseClient.ThrottleLevel < ShedThrottleFloor) return true;
            return false;
        }

        // Core's dynamic throttle runs 1.0 (full speed) → 0.0 (paused); below this it's slowed enough that
        // conversations should yield to higher-value work (storyteller, news).
        private const float ShedThrottleFloor = 0.5f;

        /// <summary>The cheap, no-LLM outcome for a conversation shed under load (Conversations#38): resolve a
        /// minimal beat, apply the code-computed social offsets so opinion/relationship still move, and record
        /// the interaction — but generate no dialogue text. No bubble (they only render at 1x anyway).</summary>
        private static void ApplyShedConversation(Pawn initiator, Pawn recipient, InteractionDef intDef, PawnConversation conversation, int currentTick)
        {
            bool isDeep = intDef == InteractionDefOf.DeepTalk;
            var beat = new Generation.ConversationBeat { isDeep = isDeep, tone = Generation.BeatTone.Casual };
            var off = Generation.SocialOffsetCalculator.Compute(initiator, recipient, beat);

            ApplyPsychologyOffsets(initiator, recipient, off.trust, off.familiarity);
            ApplyVanillaAffinityThought(initiator, recipient, off.affinity);
            conversation.lastTick = currentTick;

            float dist = (initiator.Spawned && recipient.Spawned) ? initiator.Position.DistanceTo(recipient.Position) : -1f;
            ConversationMetrics.Add(initiator, recipient, null, dist, dist, 0, 0, "shed");
        }

        /// <summary>Headless validation hook for the shed path (Conversations#38): run a shed conversation for
        /// this pair right now and return a one-line summary of the offsets applied, so a debug action can
        /// confirm relationships still move with no LLM call.</summary>
        public static string DebugForceShed(Pawn initiator, Pawn recipient, bool deep)
        {
            var worldComp = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            if (worldComp == null) return "no world component";
            string idA = initiator.ThingID, idB = recipient.ThingID;
            var conversation = worldComp.pawnConversations.FirstOrDefault(c =>
                (c.pawnAId == idA && c.pawnBId == idB) || (c.pawnAId == idB && c.pawnBId == idA));
            if (conversation == null)
            {
                conversation = new PawnConversation(idA, idB, Find.TickManager.TicksGame);
                worldComp.pawnConversations.Add(conversation);
            }

            int opBefore = recipient.relations?.OpinionOf(initiator) ?? 0;
            var beat = new Generation.ConversationBeat { isDeep = deep, tone = Generation.BeatTone.Casual };
            var off = Generation.SocialOffsetCalculator.Compute(initiator, recipient, beat);
            ApplyShedConversation(initiator, recipient, deep ? InteractionDefOf.DeepTalk : InteractionDefOf.Chitchat, conversation, Find.TickManager.TicksGame);
            int opAfter = recipient.relations?.OpinionOf(initiator) ?? 0;

            return $"shed {initiator.LabelShort}->{recipient.LabelShort} deep={deep} " +
                   $"offsets(trust={off.trust:F2} fam={off.familiarity:F2} aff={off.affinity:F2}) " +
                   $"opinion {opBefore}->{opAfter} | queueDepth={SynapseClient.TotalQueueDepth} throttle={SynapseClient.ThrottleLevel:F2} wouldShed={ShouldShedForLoad()}";
        }

        private class LlmStatementResponse
        {
            public string text { get; set; }
        }

        // One call yields a whole alternating back-and-forth (the LLM sizes it to the scenario); the
        // first line lands now and the rest drip-feed while the pawns stay together (Conversations#31).
        private const int MaxExchangeLines = 12;

        private static void PerformSequentialDialogueGeneration(Pawn initiator, Pawn recipient, InteractionDef intDef, Action<LlmConversationResponse, Generation.ConversationBeat, bool> onComplete)
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

            bool isDeepTalk = intDef == InteractionDefOf.DeepTalk;

            // Agent-first generation (Conversations#46): the AGENT picks one concrete beat — what it's about
            // and each speaker's stance — in code, so the model only has to phrase it (the WorldNews pattern).
            // The heavy per-pawn psychology dump that pushed small models into generic filler is gone: the
            // substance lives in the beat, not the prompt.
            Generation.ConversationBeat beat = Generation.ConversationBeatResolver.Resolve(initiator, recipient, isDeepTalk, avoidTopics);

            // Continuation: if they were just talking, hand the model the last few lines to pick up from.
            string continuation = null;
            if (isContinuation && conversation != null)
            {
                continuation = string.Join("\n", conversation.messages
                    .Skip(Math.Max(0, conversation.messages.Count - 4)).Take(4)
                    .Select(m => $"{(m.sender == idA ? initiator.Name.ToStringShort : recipient.Name.ToStringShort)}: {m.message}"));
            }

            var prompt = Generation.ThinDialoguePrompt.Build(initiator, recipient, beat, continuation);

            // PromptAsync (system + user), NOT a lone system message — the concrete beat has to be the USER
            // turn or the model returns an empty ack. This is the path WorldNews uses to produce real text.
            SynapseClient.PromptAsync(
                RimSynapseConversationsMod.ModHandle,
                prompt.system,
                prompt.user,
                result =>
                {
                    if (!result.success || string.IsNullOrEmpty(result.content))
                    {
                        SynapseLogger.Warn("conversations", $"[beat] generation failed (success={result.success}) for {initiator.Name.ToStringShort} & {recipient.Name.ToStringShort}: {Trunc(result.content, 200)}");
                        onComplete(null, beat, isContinuation);
                        return;
                    }

                    // Lines only now — offsets are computed in code (Conversations#46), not by the model.
                    var rawLines = ParseLinesLenient(result.content);
                    var cleanLines = rawLines?
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Select(l => CleanLine(l, initiator, recipient))
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Take(MaxExchangeLines)
                        .ToList();

                    if (cleanLines == null || cleanLines.Count == 0)
                    {
                        SynapseLogger.Warn("conversations", $"[beat] no lines parsed for {initiator.Name.ToStringShort} & {recipient.Name.ToStringShort}; raw: {Trunc(result.content, 400)}");
                        onComplete(null, beat, isContinuation);
                        return;
                    }

                    // Assign speakers by position: line 0 = initiator, then strictly alternating.
                    var dialogue = new List<LlmDialogueLine>();
                    for (int i = 0; i < cleanLines.Count; i++)
                    {
                        Pawn speaker = (i % 2 == 0) ? initiator : recipient;
                        dialogue.Add(new LlmDialogueLine { sender = speaker.Name.ToStringShort, text = cleanLines[i] });
                    }

                    var off = Generation.SocialOffsetCalculator.Compute(initiator, recipient, beat);
                    var parsed = new LlmConversationResponse
                    {
                        dialogue = dialogue,
                        trustOffset = off.trust,
                        familiarityOffset = off.familiarity,
                        affinityOffset = off.affinity
                    };
                    onComplete(parsed, beat, isContinuation);
                },
                new ChatOptions
                {
                    priority = 1,
                    requestName = isDeepTalk ? "Deep talk (beat)" : "Chitchat (beat)",
                    targetName = $"{initiator.Name.ToStringShort} & {recipient.Name.ToStringShort}"
                }
            );
        }

        private static void QueuePreGeneration(Pawn initiator, Pawn recipient, InteractionDef intDef)
        {
            if (initiator == null || recipient == null || !initiator.Spawned || !recipient.Spawned) return;
            if (ShouldShedForLoad()) return; // background pre-gen only adds load — hold off while behind (#38)

            string idA = initiator.ThingID;
            string idB = recipient.ThingID;

            var worldComp = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            if (worldComp == null || !worldComp.PairNeedsFill(idA, idB)) return;

            PerformSequentialDialogueGeneration(initiator, recipient, intDef, (parsed, beat, isContinuation) =>
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
                        topicDefName = beat?.topicKey ?? "Social",
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

        /// <summary>A human-readable label for the beat, used when tagging the social memories a conversation
        /// plants (Conversations#46). Not shown to the player.</summary>
        private static string BeatTopicName(Generation.ConversationBeat beat)
        {
            if (beat == null) return "Social";
            if (!string.IsNullOrEmpty(beat.topicKey) && beat.topicKey.StartsWith("event:")) return "Event";
            return beat.isDeep ? "Deep talk" : "Chat";
        }

        /// <summary>Pull the <c>lines</c> array out of the model's reply, tolerating the malformed JSON small
        /// models routinely emit (a stray extra bracket, a missing closing brace, trailing prose). We only
        /// need the array, so we don't require the whole object to be valid.</summary>
        internal static List<string> ParseLinesLenient(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;

            // 1) The clean path: a well-formed object.
            try
            {
                var ex = JsonConvert.DeserializeObject<LlmExchangeResponse>(ExtractJson(content));
                if (ex?.lines != null && ex.lines.Count > 0) return ex.lines;
            }
            catch { /* fall through to lenient extraction */ }

            // 2) Grab just the "lines" array, up to its FIRST closing bracket (so "]]" or trailing junk is
            //    ignored). Repair a truncated close by appending one.
            int li = content.IndexOf("\"lines\"", StringComparison.OrdinalIgnoreCase);
            if (li < 0) return null;
            int lb = content.IndexOf('[', li);
            if (lb < 0) return null;
            int rb = content.IndexOf(']', lb);
            string arr = rb > lb ? content.Substring(lb, rb - lb + 1) : content.Substring(lb) + "]";
            try
            {
                var list = JsonConvert.DeserializeObject<List<string>>(arr);
                if (list != null && list.Count > 0) return list;
            }
            catch { /* give up — caller falls back */ }
            return null;
        }

        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "(empty)";
            s = s.Replace("\n", " ").Replace("\r", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>Small local models often ignore "no names/labels" and prefix a line with the speaker's
        /// name or wrap it in quotes. Strip those so the bubble shows just the spoken words.</summary>
        internal static string CleanLine(string line, Pawn a, Pawn b)
        {
            string s = line.Trim();
            if (s.Length >= 2 && s.StartsWith("- ")) s = s.Substring(2).Trim();
            // Unwrap a fully-quoted line.
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '“') && (s[s.Length - 1] == '"' || s[s.Length - 1] == '”'))
                s = s.Substring(1, s.Length - 2).Trim();
            // Strip a leading "Name", "Name:", or "Name," for either speaker.
            foreach (var name in new[] { a?.Name?.ToStringShort, b?.Name?.ToStringShort })
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (s.StartsWith(name, StringComparison.OrdinalIgnoreCase) && s.Length > name.Length)
                {
                    char sep = s[name.Length];
                    if (sep == ':' || sep == ',' || sep == ' ' || sep == '-')
                        s = s.Substring(name.Length).TrimStart(':', ',', ' ', '-').Trim();
                }
            }
            return s;
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

            PerformSequentialDialogueGeneration(initiator, recipient, intDef, (parsed, beat, isContinuation) =>
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

                    string topicName = BeatTopicName(beat);
                    PropagateContextMemories(initiator, recipient, topicName, first.message);
                    conversation.PushRecentTopic(beat?.topicKey);

                    latencySw.Stop();
                    float endDist = (initiator.Spawned && recipient.Spawned)
                        ? initiator.Position.DistanceTo(recipient.Position) : startDist;
                    ConversationMetrics.Add(initiator, recipient, null, startDist, endDist,
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

        // ── Pre-staged event conversations (#35) ─────────────────────────
        // Concrete events don't go stale, so we pre-generate per-pair retellings of a pawn's recent
        // event and fire one at random per conversation start (unique per pair+event). Weather/current
        // conditions are never pre-staged; only lived events.
        private const int EventStageRecentTicks = 180000; // ~3 in-game days
        private const int EventStagePartnersPerOwner = 2;
        private const int EventStagePerPass = 2;

        /// <summary>Called from the world component's rare tick: pre-stage a few retellings of colonists'
        /// recent events to partners who don't have that event staged yet.</summary>
        public static void TryStageEventConversations(SynapseConversationsWorldComponent worldComp)
        {
            if (worldComp == null || !worldComp.CanStageMoreEvents) return;
            if (!(RimSynapseConversationsMod.Settings?.preStageEventConversations ?? true)) return;
            if (ShouldShedForLoad()) return; // pre-staging adds background load — hold off while behind (#38)
            var map = Find.CurrentMap;
            if (map == null) return;

            long nowAbs = Find.TickManager.TicksAbs;
            int staged = 0;
            var colonists = map.mapPawns.FreeColonists;
            foreach (var owner in colonists)
            {
                if (staged >= EventStagePerPass || !worldComp.CanStageMoreEvents) break;
                var core = owner.TryGetComp<SynapseCorePawnComp>();
                if (core?.memories == null) continue;

                var mem = core.memories
                    .Where(m => m != null && m.memoryType == "EventReflection" && !string.IsNullOrEmpty(m.summary))
                    .Where(m => m.isLongTerm || nowAbs - m.absTick <= EventStageRecentTicks)
                    .OrderByDescending(m => m.absTick)
                    .FirstOrDefault();
                if (mem == null) continue;
                string eventKey = mem.memId ?? mem.summary;

                int partnersStaged = 0;
                foreach (var partner in colonists)
                {
                    if (partner == owner || partner.Dead) continue;
                    if (partnersStaged >= EventStagePartnersPerOwner || staged >= EventStagePerPass || !worldComp.CanStageMoreEvents) break;
                    if (worldComp.PairHasStagedEvent(owner.ThingID, partner.ThingID, eventKey)) continue;
                    QueueEventRetelling(worldComp, owner, partner, mem.summary, eventKey);
                    partnersStaged++;
                    staged++;
                }
            }
        }

        /// <summary>Generate (one LLM call) a short retelling where <paramref name="owner"/> recounts an
        /// event to <paramref name="partner"/>, and store it as a pre-staged event conversation.</summary>
        private static void QueueEventRetelling(SynapseConversationsWorldComponent worldComp, Pawn owner, Pawn partner, string eventSummary, string eventKey)
        {
            // A retelling is an event beat with InitiatorTells framing — owner lived it, partner is hearing
            // it fresh — routed through the same thin system+user prompt and code-computed offsets as live
            // generation (Conversations#46). System+user, NOT a lone system message: a local model returns
            // an empty acknowledgement to a system-only chat instead of generating.
            var beat = new Generation.ConversationBeat
            {
                subject = eventSummary,
                initiatorStance = "recounting how it actually went",
                recipientStance = "hearing it for the first time and reacting",
                tone = Generation.BeatTone.Casual,
                framing = Generation.BeatFraming.InitiatorTells,
                isDeep = false,
                topicKey = "event:" + eventKey
            };
            var prompt = Generation.ThinDialoguePrompt.Build(owner, partner, beat, null);

            SynapseClient.PromptAsync(
                RimSynapseConversationsMod.ModHandle,
                prompt.system,
                prompt.user,
                result =>
                {
                    if (!result.success || string.IsNullOrEmpty(result.content)) return;
                    var lines = ParseLinesLenient(result.content)?
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Select(l => CleanLine(l, owner, partner))
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToList();
                    if (lines == null || lines.Count < 2) return;

                    var off = Generation.SocialOffsetCalculator.Compute(owner, partner, beat);
                    SynapseGameComponent.Enqueue(() =>
                    {
                        worldComp.AddEventPreGen(new PreGeneratedConversation
                        {
                            initiatorId = owner.ThingID,
                            recipientId = partner.ThingID,
                            initiatorStatement = lines[0],
                            recipientResponse = lines[1],
                            eventKey = eventKey,
                            eventSummary = eventSummary,
                            trustOffset = off.trust,
                            familiarityOffset = off.familiarity,
                            affinityOffset = off.affinity,
                            generatedAtTick = Find.TickManager.TicksGame,
                            generatedAtAbsTick = Find.TickManager.TicksAbs
                        });
                    });
                },
                new ChatOptions { priority = 6, requestName = "Event retelling (pre-stage)", targetName = $"{owner.Name.ToStringShort} -> {partner.Name.ToStringShort}" });
        }

        /// <summary>Apply a pre-staged event conversation instantly (like the pool path), consuming it.</summary>
        private static void ApplyStagedEventConversation(Pawn initiator, Pawn recipient, PawnConversation conversation, PreGeneratedConversation staged, int currentTick)
        {
            conversation.messages.Add(new SynapseConversationMessage(staged.initiatorId, staged.initiatorStatement, currentTick));
            conversation.messages.Add(new SynapseConversationMessage(staged.recipientId, staged.recipientResponse, currentTick));
            conversation.lastTick = currentTick;
            while (conversation.messages.Count > 50) conversation.messages.RemoveAt(0);

            if (Find.TickManager.CurTimeSpeed == TimeSpeed.Normal)
            {
                UI.SpeechBubbleManager.AddBubble(initiator, recipient, staged.initiatorStatement, 0, 4.5f);
                UI.SpeechBubbleManager.AddBubble(recipient, initiator, staged.recipientResponse, 270, 4.5f);
            }

            ApplyPsychologyOffsets(initiator, recipient, staged.trustOffset, staged.familiarityOffset);
            ApplyVanillaAffinityThought(initiator, recipient, staged.affinityOffset);
            PropagateContextMemories(initiator, recipient, "Event", staged.initiatorStatement);
            conversation.PushRecentTopic("event:" + staged.eventKey);

            float dist = (initiator.Spawned && recipient.Spawned) ? initiator.Position.DistanceTo(recipient.Position) : -1f;
            ConversationMetrics.Add(initiator, recipient, null, dist, dist, 0, 0, "event-pool");
        }

        // Event-driven topics (#34): how far back a lived event still counts as "recent".
        private const int EventTopicRecentTicks = 180000; // ~3 in-game days

        /// <summary>The deterministic candidate pick for a recent significant event the pawn actually
        /// experienced (EventReflection memory, involvement-gated by Core#88), unit-testable without any
        /// Rand gate. Recent memories only; deep talk takes the weightiest, chit-chat a recent one; honours
        /// the avoid set so the same ordeal isn't retold back-to-back. Retained for the test suite; live
        /// beat selection now runs through <see cref="Generation.ConversationBeatResolver"/>.</summary>
        public static WeightedMemory SelectEventMemoryCandidate(SynapseCorePawnComp core, bool deep, ICollection<string> avoid)
        {
            if (core?.memories == null || core.memories.Count == 0) return null;

            long nowAbs = Find.TickManager != null ? Find.TickManager.TicksAbs : 0L;
            var candidates = core.memories
                .Where(m => m != null && m.memoryType == "EventReflection" && !string.IsNullOrEmpty(m.summary))
                .Where(m => m.isLongTerm || nowAbs - m.absTick <= EventTopicRecentTicks)
                .Where(m => avoid == null || !avoid.Contains("event:" + (m.memId ?? m.summary)))
                .ToList();
            if (candidates.Count == 0) return null;

            if (deep)
                return candidates.OrderByDescending(m => m.salience > 0f ? m.salience : m.weight).First();
            return candidates.OrderByDescending(m => m.absTick).Take(5).RandomElement();
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

        internal static void ApplyPsychologyOffsets(Pawn initiator, Pawn recipient, float trustOffset, float familiarityOffset)
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

        internal static void ApplyVanillaAffinityThought(Pawn initiator, Pawn recipient, float affinityOffset)
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

        internal static void PropagateContextMemories(Pawn initiator, Pawn recipient, string topicName, string reply)
        {
            int earshotRange = CalculateEarshotRange(initiator);

            // Memory mechanics live in Core (Core #80): AddMemoryAbout keys the OTHER pawn via the
            // canonical subject id and routes through the indexed AddMemory, so a chit-chat about a
            // pawn can consolidate with later memories about them (e.g. their death). Conversations
            // just triggers it — no hand-rolled WeightedMemory, no ThingID-in-a-tag linkage.

            // Feed to initiator (a memory about the recipient)
            initiator.TryGetComp<SynapseCorePawnComp>()?.AddMemoryAbout(
                recipient,
                $"Said to {recipient.Name.ToStringShort} during a {topicName} conversation: \"{reply}\"",
                "social", 0.10f,
                tags: new List<string> { "conversation" });

            // Feed to recipient (a memory about the initiator)
            recipient.TryGetComp<SynapseCorePawnComp>()?.AddMemoryAbout(
                initiator,
                $"{initiator.Name.ToStringShort} said to me during a {topicName} conversation: \"{reply}\"",
                "social", 0.10f,
                tags: new List<string> { "conversation" });

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
                // Overheard: a memory about BOTH speakers, linked to each via the canonical ids (Core #80).
                closestBystander.TryGetComp<SynapseCorePawnComp>()?.AddMemoryAbout(
                    new[] { initiator, recipient },
                    $"Overheard {initiator.Name.ToStringShort} say to {recipient.Name.ToStringShort} during a {topicName} conversation: \"{reply}\"",
                    "social", 0.05f,
                    tags: new List<string> { "overheard" },
                    decayRate: 0.05f);
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

        internal static string ExtractJson(string content)
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
