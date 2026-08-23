using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimSynapse.Comps;
using RimSynapse.Models;
using RimSynapse.Conversations;
using RimSynapse.Conversations.Generation;
using RimSynapse.Conversations.Patches;
using RimAgentic.Testing;

namespace RimSynapse.Conversations.Tests
{
    /// <summary>
    /// Conversation depth work (Conversations#28): the context resolver's memory tiers, per-pair topic
    /// anti-repetition history, and the pre-seed pool's caps + variety. Deterministic — synthetic comps.
    /// The Core episode/voice-catalog cases and Psychology's voice-mapping case that used to share this
    /// file live in those repos' suites now.
    /// </summary>
    [SynapseTestSet]
    public static class ConversationCases
    {
        private static WeightedMemory Mem(string summary, float weight, string type, long absTick,
            bool longTerm = false, List<string> tags = null)
        {
            return new WeightedMemory
            {
                summary = summary, weight = weight, baseWeight = weight, memoryType = type,
                absTick = absTick, isLongTerm = longTerm, tags = tags ?? new List<string>()
            };
        }

        public static IEnumerable<SynapseTestCase> All()
        {
            // Topic anti-repetition: the ring buffer dedupes and caps, most-recent last.
            yield return new SynapseTestCase("Conversations_TopicHistoryAvoidsRepeats", () =>
            {
                var pc = new PawnConversation("A", "B", 0);
                foreach (var t in new[] { "T_A", "T_B", "T_C", "T_D", "T_E" }) pc.PushRecentTopic(t);
                Assert.Equal(4, pc.recentTopics.Count, "history caps at 4");
                Assert.False(pc.recentTopics.Contains("T_A"), "oldest is evicted");
                Assert.Equal("T_E", pc.recentTopics.Last(), "most recent is last");
                pc.PushRecentTopic("T_C"); // re-use moves it to the end without duplicating
                Assert.Equal(4, pc.recentTopics.Count, "re-using a topic does not grow the history");
                Assert.Equal("T_C", pc.recentTopics.Last(), "re-used topic becomes most recent");
                return $"history=[{string.Join(",", pc.recentTopics)}]";
            });

            // Context resolver: memory keys resolve against the 0.7.1 tiers.
            yield return new SynapseTestCase("Conversations_ContextResolvesMemoryTiers", () =>
            {
                long now = Find.TickManager != null ? Find.TickManager.TicksAbs : 100000L;
                var core = new SynapseCorePawnComp();
                core.AddMemory(Mem("chatted by the fire today", 0.2f, "social", now - 100));
                core.AddMemory(Mem("a dull thing a season ago", 0.2f, "social", now - 500000));
                var lt = Mem("the defining moment of their life", 0.9f, "EventReflection", now - 500000, longTerm: true);
                core.AddMemory(lt); lt.isLongTerm = true; lt.salience = 2f;
                core.AddMemory(Mem("their friend died in the raid", 0.9f, "EventReflection", now - 300, tags: new List<string> { "Death" }));

                string today = ConversationContextResolver.Resolve("memoriesToday", null, null, core);
                Assert.Contains(today ?? "", "chatted by the fire", "today tier surfaces today's memory");
                Assert.DoesNotContain(today ?? "", "season ago", "today tier excludes old memories");

                string longTerm = ConversationContextResolver.Resolve("memoriesLongTerm", null, null, core);
                Assert.Contains(longTerm ?? "", "defining moment", "long-term tier surfaces the consolidated memory");

                string grief = ConversationContextResolver.Resolve("griefMemories", null, null, core);
                Assert.Contains(grief ?? "", "friend died", "grief tier surfaces the Death-tagged memory");
                return "today / long-term / grief tiers all resolved";
            });

            // Pre-seed pool: per-pair cap and topic variety.
            yield return new SynapseTestCase("Conversations_PreGenPoolCapsAndVaries", () =>
            {
                var wc = new SynapseConversationsWorldComponent(Find.World);
                for (int i = 0; i < 5; i++)
                {
                    wc.AddToPool(new PreGeneratedConversation
                    {
                        initiatorId = "P1", recipientId = "P2", topicDefName = "Topic_" + i,
                        initiatorStatement = "hi " + i, recipientResponse = "hello " + i
                    });
                }
                Assert.Equal(SynapseConversationsWorldComponent.MaxPreGenPerPair, wc.PoolCountForPair("P1", "P2"),
                    "a single pair cannot exceed the per-pair cap");
                Assert.False(wc.PairNeedsFill("P1", "P2"), "a full pair reports no need to fill");
                Assert.Equal(SynapseConversationsWorldComponent.MaxPreGenPerPair, wc.PoolTopicsForPair("P1", "P2").Count,
                    "pooled topics are distinct (selection diversifies them)");
                return $"pair pool={wc.PoolCountForPair("P1", "P2")}, distinct topics={wc.PoolTopicsForPair("P1", "P2").Count}";
            });

            // Read-only agent tools (Conversations#10): get_chat_history filters to the named colonist,
            // is newest-first, honors maxMessages; get_colonist_interests returns valid JSON; unknown
            // pawn returns an error payload rather than throwing.
            yield return new SynapseTestCase("Conversations_HistoryAndInterests", () =>
            {
                Assert.True(SynapseToolRegistry.IsToolRegistered("get_chat_history"), "get_chat_history is registered");
                Assert.True(SynapseToolRegistry.IsToolRegistered("get_colonist_interests"), "get_colonist_interests is registered");

                Map map = Find.CurrentMap ?? Find.Maps.FirstOrDefault();
                Assert.True(map != null, "no map available");
                var pawn = map.mapPawns.FreeColonists.FirstOrDefault();
                Assert.True(pawn != null, "no colonist available");
                var other = map.mapPawns.FreeColonists.FirstOrDefault(p => p != pawn) ?? pawn;

                var wc = Find.World.GetComponent<SynapseConversationsWorldComponent>();
                Assert.True(wc != null, "no conversations world component");

                var conv = new PawnConversation(pawn.ThingID, other.ThingID, 200);
                conv.messages.Add(new SynapseConversationMessage(pawn.ThingID, "older hello", 100));
                conv.messages.Add(new SynapseConversationMessage(other.ThingID, "newer reply", 200));
                wc.pawnConversations.Add(conv);
                try
                {
                    string name = pawn.LabelShort;
                    string argsJson = "{\"pawnName\": \"" + name + "\"}";

                    string hist = SynapseToolRegistry.ExecuteTool("get_chat_history", argsJson, false);
                    Assert.Contains(hist, "older hello", "history includes the seeded initiator message");
                    Assert.Contains(hist, "newer reply", "history includes the seeded reply");
                    Assert.True(hist.IndexOf("newer reply") < hist.IndexOf("older hello"), "history is newest-first");

                    string capped = SynapseToolRegistry.ExecuteTool("get_chat_history",
                        "{\"pawnName\": \"" + name + "\", \"maxMessages\": 1}", false);
                    Assert.Contains(capped, "newer reply", "capped history keeps the newest message");
                    Assert.DoesNotContain(capped, "older hello", "maxMessages caps the older message out");

                    string interests = SynapseToolRegistry.ExecuteTool("get_colonist_interests", argsJson, false);
                    Assert.Contains(interests, "interests", "interests payload has an interests field");

                    string missing = SynapseToolRegistry.ExecuteTool("get_chat_history",
                        "{\"pawnName\": \"NoSuchPawn_zzz\"}", false);
                    Assert.Contains(missing, "error", "unknown pawn returns an error payload");

                    return $"history newest-first + capped + interests + error-path ok for {name}";
                }
                finally
                {
                    wc.pawnConversations.Remove(conv);
                }
            });

            // Universe action tools (Conversations#4): mutating-flag gate, apply-once + cooldown, clamp,
            // and error-paths for the mood/relationship/inspiration tools.
            yield return new SynapseTestCase("Conversations_UniverseActions", () =>
            {
                Assert.True(SynapseToolRegistry.IsToolRegistered("trigger_mood_booster"), "trigger_mood_booster registered");
                Assert.True(SynapseToolRegistry.IsToolRegistered("trigger_relationship_shift"), "trigger_relationship_shift registered");
                Assert.True(SynapseToolRegistry.IsToolRegistered("inspire_colonist"), "inspire_colonist registered");

                // Flagged mutating: a gated run (allowMutating:false) must refuse them before the handler.
                string gated = SynapseToolRegistry.ExecuteTool("trigger_mood_booster",
                    "{\"pawnName\":\"x\",\"effectType\":\"boost\",\"reason\":\"t\"}", false);
                Assert.Contains(gated, "not permitted to mutate", "mutating tool refused when gating disallows mutation");

                Map map = Find.CurrentMap ?? Find.Maps.FirstOrDefault();
                Assert.True(map != null, "no map available");
                var colonists = map.mapPawns.FreeColonists.ToList();
                Assert.True(colonists.Count >= 1, "need at least one colonist");
                var pawn = colonists[0];
                string name = pawn.LabelShort;

                System.Func<int> countKind = () =>
                    pawn.needs?.mood?.thoughts?.memories?.Memories?.Count(m => m.def.defName == "KindWordsMood") ?? 0;
                int before = countKind();

                string boost = SynapseToolRegistry.ExecuteTool("trigger_mood_booster",
                    "{\"pawnName\":\"" + name + "\",\"effectType\":\"boost\",\"reason\":\"good talk\"}", true);
                Assert.Contains(boost, "success", "valid boost reports success");
                int after = countKind();
                Assert.True(after > before, "boost added a KindWordsMood memory");

                // Cooldown: an immediate second call is a no-op error; the memory count does not change.
                string second = SynapseToolRegistry.ExecuteTool("trigger_mood_booster",
                    "{\"pawnName\":\"" + name + "\",\"effectType\":\"boost\",\"reason\":\"again\"}", true);
                Assert.Contains(second, "cooldown", "second boost within the window is a cooldown no-op");
                Assert.Equal(after, countKind(), "cooldown call added no further memory");

                // Out-of-range shiftAmount is clamped, not rejected-with-throw.
                if (colonists.Count >= 2)
                {
                    string shift = SynapseToolRegistry.ExecuteTool("trigger_relationship_shift",
                        "{\"speakerName\":\"" + name + "\",\"recipientName\":\"" + colonists[1].LabelShort + "\",\"shiftAmount\":999,\"reason\":\"clamp\"}", true);
                    Assert.DoesNotContain(shift, "Exception during tool execution", "out-of-range shiftAmount is clamped, not thrown");
                    Assert.Contains(shift, "success", "clamped relationship shift applies (RapportBuilt path)");
                }

                // Error paths return structured JSON without throwing.
                Assert.Contains(SynapseToolRegistry.ExecuteTool("inspire_colonist",
                    "{\"pawnName\":\"NoSuchPawn_zzz\",\"inspirationType\":\"frenzy\"}", true), "error", "unknown pawn -> error");
                Assert.Contains(SynapseToolRegistry.ExecuteTool("inspire_colonist",
                    "{\"pawnName\":\"" + name + "\",\"inspirationType\":\"not_a_type\"}", true), "error", "bad enum -> error");
                Assert.Contains(SynapseToolRegistry.ExecuteTool("trigger_mood_booster", "{}", true), "error", "empty args -> error");

                return $"gate-refused + boost(+{after - before}) + cooldown + clamp + error-paths ok for {name}";
            });

            // Event-driven topic selection (#34): recent EventReflection memories become topics;
            // deep talk takes the weightiest, chit-chat an EventReflection, avoid-set excludes, and a
            // pawn with no event memories yields none.
            yield return new SynapseTestCase("Conversations_EventTopicSelection", () =>
            {
                long now = Find.TickManager != null ? Find.TickManager.TicksAbs : 100000L;
                var core = new SynapseCorePawnComp();
                var crow = new WeightedMemory { summary = "clawed by a crow", memoryType = "EventReflection", weight = 0.4f, baseWeight = 0.4f, absTick = now - 100 };
                core.AddMemory(crow);
                var death = new WeightedMemory { summary = "watched a friend die", memoryType = "EventReflection", weight = 0.9f, baseWeight = 0.9f, absTick = now - 500000, isLongTerm = true };
                core.AddMemory(death); death.isLongTerm = true; death.salience = 2f;
                core.AddMemory(new WeightedMemory { summary = "idle chatter", memoryType = "social", weight = 0.2f, baseWeight = 0.2f, absTick = now - 50 });

                var deep = Patch_Pawn_InteractionsTracker_TryInteractWith.SelectEventMemoryCandidate(core, true, null);
                Assert.True(deep != null && deep.summary.Contains("friend die"), "deep talk picks the weightiest event");

                var chit = Patch_Pawn_InteractionsTracker_TryInteractWith.SelectEventMemoryCandidate(core, false, null);
                Assert.True(chit != null && chit.memoryType == "EventReflection", "chit-chat picks an EventReflection event");

                var avoid = new HashSet<string> { "event:" + (deep.memId ?? deep.summary) };
                var deep2 = Patch_Pawn_InteractionsTracker_TryInteractWith.SelectEventMemoryCandidate(core, true, avoid);
                Assert.True(deep2 != deep, "the avoid set excludes the just-told event");

                var core2 = new SynapseCorePawnComp();
                core2.AddMemory(new WeightedMemory { summary = "just chatting", memoryType = "social", weight = 0.2f, baseWeight = 0.2f, absTick = now });
                Assert.True(Patch_Pawn_InteractionsTracker_TryInteractWith.SelectEventMemoryCandidate(core2, false, null) == null,
                    "a pawn with no EventReflection memories yields no event topic");

                return $"deep=\"{deep.summary}\", chit=\"{chit.summary}\", avoid excluded";
            });

            // Pre-staged event conversations (#35): stage → unique-per-pair → event pop consumes →
            // generic pop ignores event pre-gens.
            yield return new SynapseTestCase("Conversations_EventPreStaging", () =>
            {
                Map map = Find.CurrentMap ?? Find.Maps.FirstOrDefault();
                Assert.True(map != null, "no map available");
                var cols = map.mapPawns.FreeColonists.ToList();
                Assert.True(cols.Count >= 2, "need two colonists");
                Pawn a = cols[0], b = cols[1];

                var wc = new SynapseConversationsWorldComponent(Find.World);
                wc.AddEventPreGen(new PreGeneratedConversation
                {
                    initiatorId = a.ThingID, recipientId = b.ThingID,
                    initiatorStatement = "a crow mauled me", recipientResponse = "brutal — you okay?",
                    eventKey = "ev1", eventSummary = "clawed by a crow"
                });
                Assert.True(wc.PairHasStagedEvent(a.ThingID, b.ThingID, "ev1"), "pair has the event staged");
                Assert.True(wc.PairHasStagedEvent(b.ThingID, a.ThingID, "ev1"), "staging is symmetric per pair");
                Assert.Equal(1, wc.EventPreGenCount, "one event pre-gen staged");

                // duplicate (same pair + event) is not staged twice
                wc.AddEventPreGen(new PreGeneratedConversation
                {
                    initiatorId = a.ThingID, recipientId = b.ThingID,
                    initiatorStatement = "x", recipientResponse = "y", eventKey = "ev1", eventSummary = "s"
                });
                Assert.Equal(1, wc.EventPreGenCount, "duplicate pair+event not staged twice");

                // the generic pool pop must NOT grab an event-anchored pre-gen
                Assert.True(wc.PopFreshPreGen(a, b) == null, "generic pop ignores event pre-gens");

                // event pop returns it and consumes it (unique per pair)
                var got = wc.PopEventPreGenForPair(a, b);
                Assert.True(got != null && got.eventKey == "ev1", "event pop returns the staged retelling");
                Assert.Equal(0, wc.EventPreGenCount, "event pre-gen consumed on pop");
                Assert.True(wc.PopEventPreGenForPair(a, b) == null, "a told event is not repeated to the same pair");
                return "stage + unique + event-pop-consumes + generic-pop-ignores ok";
            });

            // Activity-subject cleanup: Core's summary appends a completion percentage to each job
            // segment ("wandering (100%)"); the subject wants only the phrase, so RecentActivity strips
            // a trailing " (NN%)". Guards that it drops the annotation, keeps non-percent parens, and
            // is a no-op when there is nothing to strip.
            yield return new SynapseTestCase("Conversations_ActivityStripsTrailingPercent", () =>
            {
                Assert.Equal("wandering", ConversationBeatResolver.StripTrailingPercent("wandering (100%)"),
                    "a trailing (100%) is stripped");
                Assert.Equal("hauling steel", ConversationBeatResolver.StripTrailingPercent("hauling steel (42.5%)"),
                    "a fractional percentage is stripped");
                Assert.Equal("wandering", ConversationBeatResolver.StripTrailingPercent("wandering"),
                    "no annotation is a no-op");
                Assert.Equal("talking (to Randy)", ConversationBeatResolver.StripTrailingPercent("talking (to Randy)"),
                    "a non-percent parenthetical is left intact");
                return "strip + fractional + no-op + non-percent-paren ok";
            });
        }
    }
}
