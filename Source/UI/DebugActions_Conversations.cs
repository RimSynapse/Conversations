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

        [DebugAction("RimSynapse", "Conversations: Dump metrics (Log)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void DumpMetrics()
        {
            RimSynapse.SynapseLogger.Info("conversations", ConversationMetrics.Summary());
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
    }
}
