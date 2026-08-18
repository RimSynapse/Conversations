using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld.Planet;
using Verse;
using RimSynapse.Comps;

namespace RimSynapse.Conversations
{
    /// <summary>
    /// Stores the active storyteller conversation history and the pre-seed conversation pool in the save.
    /// </summary>
    public class SynapseConversationsWorldComponent : WorldComponent
    {
        // Player↔storyteller chat state moved to Core's SynapseCoreWorldComponent (Core #99).
        // This component now owns pawn-to-pawn dialogue state only.
        public List<PawnConversation> pawnConversations = new List<PawnConversation>();

        // ── Pre-seed pool (Conversations#28) ─────────────────────────────
        public List<PreGeneratedConversation> preGenPool = new List<PreGeneratedConversation>();

        public const int MaxPreGenPerPair = 3;
        public const int MaxPreGenTotal = 40;
        private const int PreGenTtlTicks = 60000;      // 1 in-game day for chit-chat
        private const int PreGenDeepTtlTicks = 10000;  // ~4 hours for deep talk (leans on volatile memory)
        private const int PoolMaintainInterval = 2000; // rare-tick prune + top-up cadence

        // Memory tags that make a pre-gen stale: a conversation written before one of these landed is wrong.
        private static readonly string[] SignificantTags = { "Death", "Died", "Grief", "Betrayal", "TraitShift" };

        public SynapseConversationsWorldComponent(World world) : base(world) { }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref pawnConversations, "pawnConversations", LookMode.Deep);
            Scribe_Collections.Look(ref preGenPool, "preGenPool", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (pawnConversations == null) pawnConversations = new List<PawnConversation>();
                if (preGenPool == null) preGenPool = new List<PreGeneratedConversation>();
            }
        }

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            int tick = Find.TickManager.TicksGame;
            if (tick % PoolMaintainInterval != 0) return;

            PrunePool(Find.TickManager.TicksAbs, tick);
            // Top-up is owned by the generator (patch class); it queues low-priority fills on idle cycles.
            Patches.Patch_Pawn_InteractionsTracker_TryInteractWith.TryTopUpPreGenPool(this);
            // Pre-stage retellings of recent events for pairs that don't have them yet (#35).
            Patches.Patch_Pawn_InteractionsTracker_TryInteractWith.TryStageEventConversations(this);
        }

        private static string PairKey(string a, string b)
            => string.CompareOrdinal(a, b) < 0 ? a + "_" + b : b + "_" + a;

        public int PoolCountForPair(string idA, string idB)
        {
            string k = PairKey(idA, idB);
            int n = 0;
            foreach (var p in preGenPool)
                if (PairKey(p.initiatorId, p.recipientId) == k) n++;
            return n;
        }

        public bool PoolAtTotalCap => preGenPool.Count >= MaxPreGenTotal;

        /// <summary>Topic defNames already pooled for this pair — fed into topic selection so the
        /// pooled options stay varied.</summary>
        public HashSet<string> PoolTopicsForPair(string idA, string idB)
        {
            string k = PairKey(idA, idB);
            var set = new HashSet<string>();
            foreach (var p in preGenPool)
                if (PairKey(p.initiatorId, p.recipientId) == k && !string.IsNullOrEmpty(p.topicDefName))
                    set.Add(p.topicDefName);
            return set;
        }

        public bool PairNeedsFill(string idA, string idB)
            => !PoolAtTotalCap && PoolCountForPair(idA, idB) < MaxPreGenPerPair;

        public void AddToPool(PreGeneratedConversation conv)
        {
            if (conv == null || string.IsNullOrEmpty(conv.initiatorId) || string.IsNullOrEmpty(conv.recipientId)) return;
            if (PoolAtTotalCap) return;
            if (PoolCountForPair(conv.initiatorId, conv.recipientId) >= MaxPreGenPerPair) return;
            preGenPool.Add(conv);
        }

        /// <summary>Take a fresh pre-gen for this pair, discarding any expired/stale entries encountered.</summary>
        public PreGeneratedConversation PopFreshPreGen(Pawn a, Pawn b)
        {
            if (a == null || b == null) return null;
            string k = PairKey(a.ThingID, b.ThingID);
            long nowAbs = Find.TickManager.TicksAbs;
            int nowTick = Find.TickManager.TicksGame;
            for (int i = preGenPool.Count - 1; i >= 0; i--)
            {
                var p = preGenPool[i];
                if (!string.IsNullOrEmpty(p.eventKey)) continue;   // event pre-gens fire only via the event path (#35)
                if (PairKey(p.initiatorId, p.recipientId) != k) continue;
                if (IsExpiredOrStale(p, a, b, nowAbs, nowTick)) { preGenPool.RemoveAt(i); continue; }
                preGenPool.RemoveAt(i);
                return p;
            }
            return null;
        }

        // ── Event pre-staging (#35) ──────────────────────────────────────
        // Concrete events don't go stale, so we pre-stage per-pair retellings of recent episodes and
        // fire one at random per conversation start. Kept in the same pool (TTL/prune apply) but under a
        // separate total bound and popped only via the event path.
        public const int MaxEventPreGensTotal = 24;

        public int EventPreGenCount => preGenPool.Count(p => !string.IsNullOrEmpty(p.eventKey));
        public bool CanStageMoreEvents => EventPreGenCount < MaxEventPreGensTotal;

        /// <summary>A pair tells any given event only once — has this pair already got it staged?</summary>
        public bool PairHasStagedEvent(string idA, string idB, string eventKey)
        {
            if (string.IsNullOrEmpty(eventKey)) return false;
            string k = PairKey(idA, idB);
            foreach (var p in preGenPool)
                if (p.eventKey == eventKey && PairKey(p.initiatorId, p.recipientId) == k) return true;
            return false;
        }

        public void AddEventPreGen(PreGeneratedConversation conv)
        {
            if (conv == null || string.IsNullOrEmpty(conv.eventKey)) return;
            if (!CanStageMoreEvents) return;
            if (PairHasStagedEvent(conv.initiatorId, conv.recipientId, conv.eventKey)) return;
            preGenPool.Add(conv);
        }

        /// <summary>Take one staged event retelling for this pair (any event), consuming it.</summary>
        public PreGeneratedConversation PopEventPreGenForPair(Pawn a, Pawn b)
        {
            if (a == null || b == null) return null;
            string k = PairKey(a.ThingID, b.ThingID);
            for (int i = preGenPool.Count - 1; i >= 0; i--)
            {
                var p = preGenPool[i];
                if (string.IsNullOrEmpty(p.eventKey)) continue;
                if (PairKey(p.initiatorId, p.recipientId) != k) continue;
                preGenPool.RemoveAt(i);
                return p;
            }
            return null;
        }

        /// <summary>Drop expired (TTL) and stale (significant-event) pre-gens, plus any whose pawns are gone.</summary>
        public void PrunePool(long nowAbs, int nowTick)
        {
            for (int i = preGenPool.Count - 1; i >= 0; i--)
            {
                var p = preGenPool[i];
                Pawn a = PawnFromId(p.initiatorId);
                Pawn b = PawnFromId(p.recipientId);
                if (a == null || b == null || IsExpiredOrStale(p, a, b, nowAbs, nowTick))
                    preGenPool.RemoveAt(i);
            }
        }

        private static bool IsExpiredOrStale(PreGeneratedConversation p, Pawn a, Pawn b, long nowAbs, int nowTick)
        {
            int ttl = TopicIsDeep(p.topicDefName) ? PreGenDeepTtlTicks : PreGenTtlTicks;
            if (nowTick - p.generatedAtTick > ttl) return true;
            // Significant-event invalidation: either participant gained a Death/Grief/Betrayal/TraitShift
            // memory after this pre-gen was written.
            return HasSignificantMemorySince(a, p.generatedAtAbsTick) || HasSignificantMemorySince(b, p.generatedAtAbsTick);
        }

        public static bool HasSignificantMemorySince(Pawn pawn, long sinceAbs)
        {
            var core = pawn?.TryGetComp<SynapseCorePawnComp>();
            if (core?.memories == null) return false;
            foreach (var m in core.memories)
            {
                if (m.absTick <= sinceAbs || m.tags == null) continue;
                for (int i = 0; i < m.tags.Count; i++)
                {
                    string t = m.tags[i];
                    for (int j = 0; j < SignificantTags.Length; j++)
                        if (t == SignificantTags[j]) return true;
                }
            }
            return false;
        }

        private static bool TopicIsDeep(string topicDefName)
        {
            if (string.IsNullOrEmpty(topicDefName)) return false;
            var d = DefDatabase<ChatTopicDef>.GetNamedSilentFail(topicDefName);
            return d != null && d.isDeepTalk;
        }

        public static Pawn PawnFromId(string thingId)
        {
            if (string.IsNullOrEmpty(thingId) || Find.Maps == null) return null;
            foreach (var map in Find.Maps)
            {
                var pawns = map.mapPawns?.AllPawnsSpawned;
                if (pawns == null) continue;
                for (int i = 0; i < pawns.Count; i++)
                    if (pawns[i].ThingID == thingId) return pawns[i];
            }
            return null;
        }
    }
}
