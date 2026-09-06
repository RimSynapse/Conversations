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

        // ── Pre-generation pool (#60): POINT conversations only, keyed (speaker, listener, pointId). The
        // pair-keyed pre-seed pool (#28) and event pre-staging (#35) folded into point-driven pregeneration.
        public List<PreGeneratedConversation> preGenPool = new List<PreGeneratedConversation>();

        // ── Outsider flavor bank (#52 layer 3) ───────────────────────────
        // LLM-generated barks for outsiders (raiders/traders/visitors), keyed by "role|factionDefName" and
        // cached persistently so a faction's flavor is generated once and reused. Merged on top of the always-
        // present authored baseline (OutsiderChatterDef); a pawn is never without a line even if generation
        // never runs. The requested-set is in-memory only, so a failed generation is retried next session.
        public List<OutsiderFlavorBank> outsiderFlavor = new List<OutsiderFlavorBank>();
        [System.NonSerialized] private HashSet<string> flavorRequested = new HashSet<string>();

        public List<string> GetOutsiderFlavor(string key)
        {
            for (int i = 0; i < outsiderFlavor.Count; i++)
                if (outsiderFlavor[i]?.key == key) return outsiderFlavor[i].lines;
            return null;
        }

        public bool FlavorRequestedOrCached(string key)
            => flavorRequested.Contains(key) || GetOutsiderFlavor(key) != null;

        public void MarkFlavorRequested(string key) => flavorRequested.Add(key);

        public void StoreOutsiderFlavor(string key, List<string> lines)
        {
            if (string.IsNullOrEmpty(key) || lines == null || lines.Count == 0) return;
            for (int i = 0; i < outsiderFlavor.Count; i++)
                if (outsiderFlavor[i]?.key == key) { outsiderFlavor[i].lines = lines; return; }
            outsiderFlavor.Add(new OutsiderFlavorBank { key = key, lines = lines });
        }

        private const int PreGenTtlTicks = 60000;      // 1 in-game day for chit-chat
        private const int PreGenDeepTtlTicks = 10000;  // ~4 hours for deep talk (leans on volatile memory)
        private const int PoolMaintainInterval = 2000; // rare-tick prune + top-up cadence

        // Memory tags that make a pre-gen stale: a conversation written before one of these landed is wrong.
        private static readonly string[] SignificantTags = { "Death", "Died", "Grief", "Betrayal", "TraitShift" };

        public SynapseConversationsWorldComponent(World world) : base(world) { }

        // ── Exchange counts (#60 §6 cliques, v1) ─────────────────────────
        // Who tells whom, per ORDERED pair ("speaker>listener"). High mutual exchange is a clique; v1 only
        // records the counts (detection is a follow-on), scribed so they accumulate over a colony's life.
        public Dictionary<string, int> exchangeCounts = new Dictionary<string, int>();

        public void RecordExchange(string speakerId, string listenerId)
        {
            if (string.IsNullOrEmpty(speakerId) || string.IsNullOrEmpty(listenerId)) return;
            string k = speakerId + ">" + listenerId;
            exchangeCounts.TryGetValue(k, out int n);
            exchangeCounts[k] = n + 1;
        }

        /// <summary>How often <paramref name="speakerId"/> has told <paramref name="listenerId"/> something.</summary>
        public int TellCount(string speakerId, string listenerId)
            => exchangeCounts.TryGetValue(speakerId + ">" + listenerId, out int n) ? n : 0;

        /// <summary>Both directions — the mutual-exchange strength a clique read would use.</summary>
        public int ExchangeCount(string a, string b) => TellCount(a, b) + TellCount(b, a);

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref pawnConversations, "pawnConversations", LookMode.Deep);
            Scribe_Collections.Look(ref preGenPool, "preGenPool", LookMode.Deep);
            Scribe_Collections.Look(ref outsiderFlavor, "outsiderFlavor", LookMode.Deep);
            Scribe_Collections.Look(ref exchangeCounts, "exchangeCounts", LookMode.Value, LookMode.Value);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (pawnConversations == null) pawnConversations = new List<PawnConversation>();
                if (preGenPool == null) preGenPool = new List<PreGeneratedConversation>();
                if (outsiderFlavor == null) outsiderFlavor = new List<OutsiderFlavorBank>();
                if (exchangeCounts == null) exchangeCounts = new Dictionary<string, int>();
            }
        }

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            int tick = Find.TickManager.TicksGame;
            if (tick % PoolMaintainInterval != 0) return;

            PrunePool(Find.TickManager.TicksAbs, tick);
            // Actively prune conversation history past the retention window (#40 history overhaul) so the
            // save stays lean and the transcript is bounded — instead of a pair's record living forever
            // unless they happen to talk again.
            PruneConversationHistory(tick);
        }

        /// <summary>Debug/validation: prune now and report the before/after counts (#40 / #59).</summary>
        public string DebugPruneHistoryNow()
        {
            int convBefore = pawnConversations.Count;
            int msgBefore = pawnConversations.Sum(c => c?.messages?.Count ?? 0);
            PruneConversationHistory(Find.TickManager.TicksGame);
            int msgAfter = pawnConversations.Sum(c => c?.messages?.Count ?? 0);
            float hours = RimSynapseConversationsMod.Settings?.conversationHistoryHours ?? 24f;
            return $"retention {hours:F0}h — conversations {convBefore}->{pawnConversations.Count}, messages {msgBefore}->{msgAfter}";
        }

        /// <summary>Drop messages older than the retention window (default 24h, capped 72h) and any
        /// conversation left empty. Runs on load too (via the tick cadence), so an upgraded save's long
        /// backlog is trimmed back to the window.</summary>
        private void PruneConversationHistory(int nowTick)
        {
            float hours = RimSynapseConversationsMod.Settings?.conversationHistoryHours ?? 24f;
            int cutoff = nowTick - (int)(hours * 2500f);
            for (int i = pawnConversations.Count - 1; i >= 0; i--)
            {
                var c = pawnConversations[i];
                if (c?.messages == null) { pawnConversations.RemoveAt(i); continue; }
                c.messages.RemoveAll(m => m == null || m.gameTick < cutoff);
                if (c.messages.Count == 0) pawnConversations.RemoveAt(i);
            }
        }

        /// <summary>The pair's conversation record, restarted when the last exchange is older than the
        /// retention window (#40) — the thread is stale, a new one begins.</summary>
        public static PawnConversation GetOrStartConversation(SynapseConversationsWorldComponent wc, Pawn a, Pawn b, int nowTick)
        {
            string idA = a.ThingID, idB = b.ThingID;
            var conv = wc.pawnConversations.FirstOrDefault(c => (c.pawnAId == idA && c.pawnBId == idB) || (c.pawnAId == idB && c.pawnBId == idA));
            float hours = RimSynapseConversationsMod.Settings?.conversationHistoryHours ?? 24f;
            int maxAge = (int)(hours * 2500f);
            if (conv != null && nowTick - conv.lastTick <= maxAge) return conv;
            if (conv != null) wc.pawnConversations.Remove(conv);
            conv = new PawnConversation(idA, idB, nowTick);
            wc.pawnConversations.Add(conv);
            return conv;
        }

        private static string PairKey(string a, string b)
            => string.CompareOrdinal(a, b) < 0 ? a + "_" + b : b + "_" + a;

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

        // ── Point-keyed pre-gens (#60 step 3) ────────────────────────────
        // A pooled conversation for a talking point is keyed (speaker, listener, pointId) — pair state for
        // one thing the speaker wants to say to one listener. Same pool (TTL / staleness apply) under its
        // own total bound; popped only by the agenda serve path (step 4). In-flight requests are guarded
        // so a point is never generated twice for the same listener; the guard self-heals on a timeout
        // because Core's client can drop a request without calling back.
        public const int MaxPointPreGensTotal = 32;
        public const int PendingTimeoutTicks = 7500;
        private readonly Dictionary<string, int> pendingPointGen = new Dictionary<string, int>(); // not scribed — in-flight dies with the session

        public int PointPreGenCount => preGenPool.Count(p => !string.IsNullOrEmpty(p.pointId));
        public bool CanPoolMorePoints => PointPreGenCount < MaxPointPreGensTotal;
        public int PendingPointGenCount => pendingPointGen.Count;

        public bool HasPooledPoint(string speakerId, string listenerId, string pointId)
        {
            if (string.IsNullOrEmpty(pointId)) return false;
            foreach (var p in preGenPool)
                if (p.pointId == pointId && p.initiatorId == speakerId && p.recipientId == listenerId) return true;
            return false;
        }

        public void AddPointPreGen(PreGeneratedConversation conv)
        {
            if (conv == null || string.IsNullOrEmpty(conv.pointId) || string.IsNullOrEmpty(conv.initiatorId) || string.IsNullOrEmpty(conv.recipientId)) return;
            if (!CanPoolMorePoints) return;
            if (HasPooledPoint(conv.initiatorId, conv.recipientId, conv.pointId)) return;
            preGenPool.Add(conv);
        }

        /// <summary>Take the pooled conversation for this exact (speaker, listener, point), consuming it.</summary>
        public PreGeneratedConversation PopPooledPoint(string speakerId, string listenerId, string pointId)
        {
            if (string.IsNullOrEmpty(pointId)) return null;
            for (int i = preGenPool.Count - 1; i >= 0; i--)
            {
                var p = preGenPool[i];
                if (p.pointId != pointId || p.initiatorId != speakerId || p.recipientId != listenerId) continue;
                preGenPool.RemoveAt(i);
                return p;
            }
            return null;
        }

        public IEnumerable<PreGeneratedConversation> PooledPoints()
            => preGenPool.Where(p => !string.IsNullOrEmpty(p.pointId));

        /// <summary>Claim an in-flight slot; false if one is already pending and younger than the timeout.</summary>
        public bool TryMarkPending(string key, int nowTick)
        {
            if (pendingPointGen.TryGetValue(key, out int since) && nowTick - since < PendingTimeoutTicks) return false;
            pendingPointGen[key] = nowTick;
            return true;
        }

        public void ClearPending(string key) => pendingPointGen.Remove(key);

        /// <summary>A point pre-gen is dead once its point left the speaker's agenda (decayed / consumed) or
        /// the listener has since been told.</summary>
        private static bool PointIsGone(PreGeneratedConversation p, Pawn speaker)
        {
            var agenda = speaker?.TryGetComp<RimSynapse.Conversations.Comps.SynapseConversationAgendaComp>();
            var point = agenda?.Find(p.pointId);
            return point == null || point.Told(p.recipientId);
        }

        /// <summary>Drop expired (TTL) and stale (significant-event) pre-gens, plus any whose pawns are gone
        /// or whose talking point no longer exists.</summary>
        public void PrunePool(long nowAbs, int nowTick)
        {
            for (int i = preGenPool.Count - 1; i >= 0; i--)
            {
                var p = preGenPool[i];
                Pawn a = PawnFromId(p.initiatorId);
                Pawn b = PawnFromId(p.recipientId);
                // Legacy (pre-#60) pair/event entries have no pointId: nothing serves them any more, drop on sight.
                if (a == null || b == null || string.IsNullOrEmpty(p.pointId) || IsExpiredOrStale(p, a, b, nowAbs, nowTick) || PointIsGone(p, a))
                    preGenPool.RemoveAt(i);
            }
        }

        private static bool IsExpiredOrStale(PreGeneratedConversation p, Pawn a, Pawn b, long nowAbs, int nowTick)
        {
            int ttl = p.isDeep ? PreGenDeepTtlTicks : PreGenTtlTicks;
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
