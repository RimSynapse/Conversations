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

        /// <summary>Single-call multi-line exchange (Conversations#31): a whole back-and-forth in ONE LLM
        /// round-trip. Lines only — social offsets are code-computed (#46). The first line lands immediately
        /// and the rest drip-feed while the pawns stay in range.</summary>
        private class LlmExchangeResponse
        {
            public List<string> lines { get; set; }
        }

        /// <summary>Is the shared LLM pipeline behind enough that conversations — the lowest-value LLM
        /// consumer — should yield (Conversations#38)? A backlog in the queue or an active Core throttle
        /// both mean the model can't keep up; a bigger colony, a slower provider and higher game speed all
        /// surface here as a deeper queue, so we gate on real load rather than on speed.</summary>
        internal static bool ShouldShedForLoad()
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

        internal const int MaxExchangeLines = 12;

        private static void PerformSequentialDialogueGeneration(Pawn initiator, Pawn recipient, InteractionDef intDef, Action<LlmConversationResponse, Generation.ConversationBeat, bool> onComplete, Generation.ConversationBeat presetBeat = null)
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
            // A caller (e.g. the environmental trigger) may supply the concrete beat; otherwise the agent
            // resolves one from live pawn state. Either way the model only phrases it (Conversations#46/#44).
            Generation.ConversationBeat beat = presetBeat
                ?? Generation.ConversationBeatResolver.Resolve(initiator, recipient, isDeepTalk, avoidTopics);

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

                    // Per-line speaker (#40): each line carries who says it. Resolve the tagged speaker to a
                    // roster pawn by name (fuzzy), falling back to positional alternation when the model omits
                    // or misspells it — so a small model that ignores the schema still yields a valid exchange.
                    var roster = new[] { initiator, recipient };
                    var speakerLines = ParseSpeakerLines(result.content);

                    var dialogue = new List<LlmDialogueLine>();
                    if (speakerLines != null)
                    {
                        for (int i = 0; i < speakerLines.Count && dialogue.Count < MaxExchangeLines; i++)
                        {
                            string text = CleanLine(speakerLines[i].text, initiator, recipient);
                            if (string.IsNullOrWhiteSpace(text)) continue;
                            Pawn speaker = ResolveSpeaker(speakerLines[i].speaker, roster, dialogue.Count);
                            dialogue.Add(new LlmDialogueLine { sender = speaker.Name.ToStringShort, text = text });
                        }
                    }

                    if (dialogue.Count == 0)
                    {
                        SynapseLogger.Warn("conversations", $"[beat] no lines parsed for {initiator.Name.ToStringShort} & {recipient.Name.ToStringShort}; raw: {Trunc(result.content, 400)}");
                        onComplete(null, beat, isContinuation);
                        return;
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

        /// <summary>DEBUG-ONLY live generation (#29). Since #60 step 4 this is the only path that generates at
        /// interaction time — the game trigger serves pregenerated agenda points (AgendaTrigger). Kept so a
        /// playtester can force an exchange without waiting for pregeneration.</summary>
        public static void ForceConversation(Pawn initiator, Pawn recipient, InteractionDef intDef)
        {
            if (initiator == null || recipient == null || intDef == null) return;
            var worldComp = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            if (worldComp == null) return;
            var conversation = SynapseConversationsWorldComponent.GetOrStartConversation(worldComp, initiator, recipient, Find.TickManager.TicksGame);
            GenerateConversationAndApply(initiator, recipient, intDef, conversation, Find.TickManager.CurTimeSpeed != TimeSpeed.Normal);
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

        internal class SpeakerLine { public string speaker; public string text; }

        /// <summary>Parse the per-line-speaker schema (#40): {"lines":[{"speaker","text"}, ...]}. Tolerates
        /// bare-string lines (old positional schema — speaker null, resolved by position) so a model that
        /// ignores the schema still yields a usable exchange.</summary>
        internal static List<SpeakerLine> ParseSpeakerLines(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            var outLines = new List<SpeakerLine>();
            try
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(ExtractJson(content));
                if (jo["lines"] is Newtonsoft.Json.Linq.JArray arr)
                {
                    foreach (var el in arr)
                    {
                        if (el is Newtonsoft.Json.Linq.JObject o)
                        {
                            string tx = o.Value<string>("text");
                            if (!string.IsNullOrWhiteSpace(tx)) outLines.Add(new SpeakerLine { speaker = o.Value<string>("speaker"), text = tx });
                        }
                        else
                        {
                            string tx = el?.ToString();
                            if (!string.IsNullOrWhiteSpace(tx)) outLines.Add(new SpeakerLine { speaker = null, text = tx });
                        }
                    }
                    if (outLines.Count > 0) return outLines;
                }
            }
            catch { /* fall through to the positional-string parser */ }

            var strings = ParseLinesLenient(content);
            if (strings == null) return null;
            foreach (var s in strings)
                if (!string.IsNullOrWhiteSpace(s)) outLines.Add(new SpeakerLine { speaker = null, text = s });
            return outLines.Count > 0 ? outLines : null;
        }

        /// <summary>Map a tagged speaker name to one of the roster pawns (exact short-name, then fuzzy
        /// contains for titles/last-names a small model may add), or fall back to positional alternation.</summary>
        internal static Pawn ResolveSpeaker(string name, Pawn[] roster, int positionalIndex)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                string n = name.Trim();
                for (int i = 0; i < roster.Length; i++)
                    if (string.Equals(roster[i].Name.ToStringShort, n, StringComparison.OrdinalIgnoreCase)) return roster[i];
                for (int i = 0; i < roster.Length; i++)
                {
                    string sn = roster[i].Name.ToStringShort;
                    if (n.IndexOf(sn, StringComparison.OrdinalIgnoreCase) >= 0 || sn.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                        return roster[i];
                }
            }
            return roster[positionalIndex % roster.Length];
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

        private static void GenerateConversationAndApply(Pawn initiator, Pawn recipient, InteractionDef intDef, PawnConversation conversation, bool isSpedUp, Generation.ConversationBeat presetBeat = null)
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
                    // Generation failed — leave no line. (Gutted the old TriggerFallback, which fabricated a
                    // generic vanilla-log / "initiated a conversation about X" line: non-psychology filler that
                    // polluted history. A failed conversation simply produces nothing, #61.)
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
                    ConversationMetrics.Add(initiator, recipient, beat?.topicKey, beat?.isDeep ?? false, startDist, endDist,
                        Find.TickManager.TicksGame - startTick, latencySw.ElapsedMilliseconds, "live");

                    // Hand the remaining lines to the map's drip-feed player.
                    if (lines.Count > 1 && initiator.Map != null)
                    {
                        var mapComp = initiator.Map.GetComponent<SynapseConversationsMapComponent>();
                        mapComp?.EnqueuePlayback(initiator, recipient, conversation, lines.GetRange(1, lines.Count - 1));
                    }
                });
            }, presetBeat);
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
            // Psychology is a hard dependency now (#60) — direct typed access, no reflection.
            var comp = pawn.TryGetComp<RimSynapse.Psychology.Comps.SynapsePawnComp>();
            if (comp?.socialNetwork == null) return;

            if (!comp.socialNetwork.TryGetValue(otherId, out var record) || record == null)
            {
                record = new RimSynapse.Psychology.Models.SocialRecord();
                comp.socialNetwork[otherId] = record;
            }
            record.trust = Mathf.Clamp(record.trust + trustOffset, -100f, 100f);
            record.familiarity = Mathf.Clamp(record.familiarity + familiarityOffset, 0f, 100f);
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

    }
}
