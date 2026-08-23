using System.Collections.Generic;
using System.Linq;
using Verse;
using LudeonTK;
using RimWorld;
using RimSynapse.Comps;

namespace RimSynapse.Conversations.UI
{
    /// <summary>
    /// Dev-only inspection for the conversation context/pool work (Conversations#28). Grouped under the
    /// shared "RimSynapse" debug menu; ToolMapForPawns so it is reachable headlessly via execute_game_tool.
    /// </summary>
    public static class DebugActions_Conversations
    {
        private static readonly string[] SampleKeys =
        {
            "ownedRoom", "apparel", "health", "bondedAnimal", "food",
            "memoriesToday", "memoriesLongTerm", "griefMemories", "traumaMemories",
            "recipientRelationship", "personalitySummary", "residency"
        };

        [DebugAction("RimSynapse", "Conversations: Dump context (Log)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void DumpConversationContext(Pawn p)
        {
            if (p == null) return;
            var core = p.TryGetComp<SynapseCorePawnComp>();
            if (core == null)
            {
                RimSynapse.SynapseLogger.Info("conversations", $"[RimSynapse] {p.LabelShort} has no SynapseCorePawnComp.");
                return;
            }

            // Nearest other humanlike colonist stands in as the conversation recipient.
            Pawn recipient = p.Map?.mapPawns?.FreeColonists?
                .Where(o => o != p && o.RaceProps.Humanlike)
                .OrderBy(o => o.Position.DistanceToSquared(p.Position))
                .FirstOrDefault();

            RimSynapse.SynapseLogger.Info("conversations", $"--- Conversation context for {p.LabelShort} (recipient: {recipient?.LabelShort ?? "none"}) ---");
            RimSynapse.SynapseLogger.Info("conversations", $"Today tier   : {ConversationContextResolver.BaseMemoryLine(core, false) ?? "none"}");
            RimSynapse.SynapseLogger.Info("conversations", $"Deep tier    : {ConversationContextResolver.BaseMemoryLine(core, true) ?? "none"}");
            foreach (var key in SampleKeys)
            {
                string text = ConversationContextResolver.Resolve(key, p, recipient, core);
                RimSynapse.SynapseLogger.Info("conversations", $"  [{key}] {text ?? "(none)"}");
            }

            var wc = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            if (wc != null && recipient != null)
            {
                RimSynapse.SynapseLogger.Info("conversations",
                    $"Pool for pair: {wc.PoolCountForPair(p.ThingID, recipient.ThingID)}/{SynapseConversationsWorldComponent.MaxPreGenPerPair}; pooled topics: {string.Join(", ", wc.PoolTopicsForPair(p.ThingID, recipient.ThingID))}");
            }
        }

        // No-arg so it runs headlessly via run_debug_action. Confirms RecentActivity's cleanup drops the
        // "(NN%)" completion annotation that Core's activity summary appends — so a subject reads
        // "the wandering they've been busy with", not "the wandering (100%) they've been busy with".
        [DebugAction("RimSynapse", "Conversations: activity subject strips (NN%)", allowedGameStates = AllowedGameStates.Playing)]
        private static void ProbeActivityPercentStrip()
        {
            (string input, string expected)[] cases =
            {
                ("wandering (100%)", "wandering"),
                ("hauling steel (42.5%)", "hauling steel"),
                ("wandering", "wandering"),
                ("talking (to Randy)", "talking (to Randy)"),
            };

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[RimSynapse] RecentActivity trailing-(NN%) strip:");
            bool allOk = true;
            foreach (var (input, expected) in cases)
            {
                string got = Generation.ConversationBeatResolver.StripTrailingPercent(input);
                bool ok = got == expected;
                allOk &= ok;
                sb.AppendLine($"  '{input}' -> '{got}'  (expected '{expected}')  {(ok ? "OK" : "FAIL")}");
            }
            sb.Append($"  VERDICT: {(allOk ? "PASS" : "FAIL")}");
            RimSynapse.SynapseLogger.Info("conversations", sb.ToString());
        }

        [DebugAction("RimSynapse", "Conversations: Force chit-chat (Tool)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ForceChitchat(Pawn p) => ForceWithNearest(p, InteractionDefOf.Chitchat);

        [DebugAction("RimSynapse", "Conversations: Force deep talk (Tool)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ForceDeepTalk(Pawn p) => ForceWithNearest(p, InteractionDefOf.DeepTalk);

        /// <summary>Playtest helper (#31): teleport the nearest colonist adjacent to the clicked pawn, then
        /// force a chit-chat, so the multi-line drip-feed can actually play out in range.</summary>
        [DebugAction("RimSynapse", "Conversations: Force nearby exchange (Tool)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ForceNearbyExchange(Pawn p)
        {
            if (p == null || p.Map == null) return;
            Pawn other = p.Map.mapPawns?.FreeColonists?
                .Where(o => o != p && o.RaceProps.Humanlike && o.Spawned)
                .OrderBy(o => o.Position.DistanceToSquared(p.Position))
                .FirstOrDefault();
            if (other == null)
            {
                RimSynapse.SynapseLogger.Info("conversations", $"[RimSynapse] No conversation partner for {p.LabelShort}.");
                return;
            }

            IntVec3 dest = p.Position;
            foreach (var adj in GenAdj.CardinalDirections)
            {
                var c = p.Position + adj;
                if (c.InBounds(p.Map) && c.Standable(p.Map)) { dest = c; break; }
            }
            other.Position = dest;
            other.Notify_Teleported(false, false);
            RimSynapse.SynapseLogger.Info("conversations",
                $"[RimSynapse] Teleported {other.LabelShort} next to {p.LabelShort} (dist {p.Position.DistanceTo(other.Position):F1}), forcing chit-chat.");
            Patches.Patch_Pawn_InteractionsTracker_TryInteractWith.ForceConversation(p, other, InteractionDefOf.Chitchat);
        }

        private static void ForceWithNearest(Pawn p, InteractionDef intDef)
        {
            if (p == null) return;
            Pawn other = p.Map?.mapPawns?.FreeColonists?
                .Where(o => o != p && o.RaceProps.Humanlike && o.Spawned)
                .OrderBy(o => o.Position.DistanceToSquared(p.Position))
                .FirstOrDefault();
            if (other == null)
            {
                RimSynapse.SynapseLogger.Info("conversations", $"[RimSynapse] No conversation partner near {p.LabelShort}.");
                return;
            }
            RimSynapse.SynapseLogger.Info("conversations",
                $"[RimSynapse] Forcing {intDef.defName} {p.LabelShort} -> {other.LabelShort} (dist {p.Position.DistanceTo(other.Position):F1} tiles).");
            Patches.Patch_Pawn_InteractionsTracker_TryInteractWith.ForceConversation(p, other, intDef);
        }

        /// <summary>Exercise the load-adaptive shed path (Conversations#38) headlessly: run a shed conversation
        /// for this pawn and the nearest colonist — no LLM call — and log the offsets applied plus the live
        /// backpressure readings, so we can confirm relationships still move and see whether real load would
        /// currently trip the shed gate.</summary>
        [DebugAction("RimSynapse", "Conversations: Force shed exchange (Tool)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ForceShedExchange(Pawn p)
        {
            if (p == null) return;
            Pawn other = p.Map?.mapPawns?.FreeColonists?
                .Where(o => o != p && o.RaceProps.Humanlike && o.Spawned)
                .OrderBy(o => o.Position.DistanceToSquared(p.Position))
                .FirstOrDefault();
            if (other == null)
            {
                RimSynapse.SynapseLogger.Info("conversations", $"[RimSynapse] No conversation partner near {p.LabelShort}.");
                return;
            }
            string summary = Patches.Patch_Pawn_InteractionsTracker_TryInteractWith.DebugForceShed(p, other, false);
            RimSynapse.SynapseLogger.Info("conversations", $"[RimSynapse] {summary}");
        }

        /// <summary>Log the most recent exchange this pawn is part of, speaker by speaker — the headless way
        /// to eyeball generated dialogue quality (Conversations#46 validation).</summary>
        [DebugAction("RimSynapse", "Conversations: Dump last exchange (Tool)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void DumpLastExchange(Pawn p)
        {
            if (p == null) return;
            var wc = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            if (wc == null) { RimSynapse.SynapseLogger.Info("conversations", "[RimSynapse] No conversations world component."); return; }

            string id = p.ThingID;
            var conv = wc.pawnConversations
                .Where(c => c.pawnAId == id || c.pawnBId == id)
                .OrderByDescending(c => c.lastTick)
                .FirstOrDefault();
            if (conv == null || conv.messages == null || conv.messages.Count == 0)
            {
                RimSynapse.SynapseLogger.Info("conversations", $"[RimSynapse] No recorded exchange for {p.LabelShort}.");
                return;
            }

            Pawn other = SynapseConversationsWorldComponent.PawnFromId(conv.pawnAId == id ? conv.pawnBId : conv.pawnAId);
            RimSynapse.SynapseLogger.Info("conversations", $"--- Last exchange: {p.LabelShort} & {other?.LabelShort ?? "?"} (recentTopics: {string.Join(", ", conv.recentTopics ?? new System.Collections.Generic.List<string>())}) ---");
            foreach (var m in conv.messages)
            {
                Pawn spk = SynapseConversationsWorldComponent.PawnFromId(m.sender);
                RimSynapse.SynapseLogger.Info("conversations", $"  {spk?.LabelShort ?? m.sender}: {m.message}");
            }
        }

        [DebugAction("RimSynapse", "Conversations: Dump metrics (Log)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void DumpMetrics()
        {
            RimSynapse.SynapseLogger.Info("conversations", ConversationMetrics.Summary());
        }

        /// <summary>
        /// 0.8 perf validation: force the environmental-trigger scan (darkness/freezer) for every
        /// colonist now, bypassing the per-pawn hash-interval gate, and log how many were evaluated.
        /// </summary>
        [DebugAction("RimSynapse", "Conversations: Force environmental scan (Log)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ForceEnvironmentalScan()
        {
            var mc = Find.CurrentMap?.GetComponent<SynapseConversationsMapComponent>();
            if (mc == null) { RimSynapse.SynapseLogger.Info("conversations", "[RimSynapse] No conversations map component."); return; }
            int n = mc.ForceEnvironmentalScan();
            RimSynapse.SynapseLogger.Info("conversations", $"[RimSynapse] Forced environmental scan evaluated {n} colonist(s).");
        }

        /// <summary>
        /// Exercises the read-only agent tools (Conversations#10) headlessly: runs get_chat_history
        /// and get_colonist_interests on the clicked pawn and logs the JSON each returns.
        /// </summary>
        [DebugAction("RimSynapse", "Conversations: Dump agent read-tools (Tool)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void DumpAgentReadTools(Pawn p)
        {
            if (p == null) return;
            string args = "{\"pawnName\": \"" + p.LabelShort + "\"}";
            RimSynapse.SynapseLogger.Info("conversations", $"--- Agent read-tools for {p.LabelShort} ---");
            RimSynapse.SynapseLogger.Info("conversations",
                "get_chat_history      : " + SynapseToolRegistry.ExecuteTool("get_chat_history", args, allowMutating: false));
            RimSynapse.SynapseLogger.Info("conversations",
                "get_colonist_interests: " + SynapseToolRegistry.ExecuteTool("get_colonist_interests", args, allowMutating: false));
        }

        [DebugAction("RimSynapse", "Conversations: Dump pre-gen pool (Log)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void DumpPreGenPool()
        {
            var wc = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            if (wc == null) { RimSynapse.SynapseLogger.Info("conversations", "[RimSynapse] No conversations world component."); return; }

            RimSynapse.SynapseLogger.Info("conversations", $"--- Pre-gen pool: {wc.preGenPool.Count}/{SynapseConversationsWorldComponent.MaxPreGenTotal} ---");
            foreach (var e in wc.preGenPool)
            {
                RimSynapse.SynapseLogger.Info("conversations",
                    $"  [{e.topicDefName}] {SynapseConversationsWorldComponent.PawnFromId(e.initiatorId)?.LabelShort ?? e.initiatorId} -> {SynapseConversationsWorldComponent.PawnFromId(e.recipientId)?.LabelShort ?? e.recipientId}: \"{e.initiatorStatement}\"");
            }
        }

        [DebugAction("RimSynapse", "Conversations: chit-chat->death memory linkage (Core #80)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ProbeMemoryLinkage()
        {
            // The #80 acceptance scenario on REAL producer paths: a chit-chat memory about pawn X
            // (via the real PropagateContextMemories) and a death-style memory about X (keyed the way
            // the producers now key it, through Core's AddMemoryAbout) must share a subjectPawnId so
            // consolidation can link them. No synthetic id-setting anywhere.
            var colonists = Find.CurrentMap?.mapPawns?.FreeColonists?.Where(p => p.TryGetComp<SynapseCorePawnComp>() != null).ToList();
            if (colonists == null || colonists.Count < 2)
            { RimSynapse.SynapseLogger.Info("conversations", "[RimSynapse] #80 probe: need two colonists with comps."); return; }

            Pawn a = colonists[0], x = colonists[1]; // a chats about x; later x "dies"
            var aComp = a.TryGetComp<SynapseCorePawnComp>();
            int before = aComp.memories.Count;

            // 1. REAL chit-chat producer.
            RimSynapse.Conversations.Patches.Patch_Pawn_InteractionsTracker_TryInteractWith.PropagateContextMemories(a, x, "Health", "How is your leg healing?");
            var chit = aComp.memories.Skip(before).FirstOrDefault(m => m.tags != null && m.tags.Contains("conversation"));

            // 2. Death-style memory about x, keyed the way the Psychology producer now keys it.
            var death = aComp.AddMemoryAbout(x, $"#80 probe: {x.LabelShort} died", "EventReflection", 0.6f);

            string canonical = SynapseCorePawnComp.MemoryPawnId(x);
            bool chitLinked = chit != null && chit.subjectPawnIds != null && chit.subjectPawnIds.Contains(canonical);
            bool deathLinked = death.subjectPawnIds.Contains(canonical);
            var linkedSet = aComp.GetMemoriesByPawnId(canonical);
            bool bothIndexed = linkedSet != null && chit != null && linkedSet.Contains(chit) && linkedSet.Contains(death);

            RimSynapse.SynapseLogger.Info("conversations",
                "[RimSynapse] #80 linkage probe:\n" +
                $"  chit-chat memory created by real producer: {(chit != null ? "yes" : "NO (bug)")}\n" +
                $"  canonical subject id for {x.LabelShort}: {canonical}\n" +
                $"  chit-chat carries it: {chitLinked}; death carries it: {deathLinked}\n" +
                $"  both indexed under one key (consolidation can link): {bothIndexed}\n" +
                $"  VERDICT: {(chitLinked && deathLinked && bothIndexed ? "PASS — the #76 scenario fires on real data" : "FAIL")}");

            // Clean up probe memories.
            if (chit != null) aComp.RemoveMemory(chit);
            aComp.RemoveMemory(death);
        }
    }
}
