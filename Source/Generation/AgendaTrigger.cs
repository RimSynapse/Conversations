using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using RimSynapse.Conversations.Comps;
using RimSynapse.Conversations.Patches;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>
    /// The trigger (#60 Phase 1 step 4) — the psychology/proximity scan that replaced the vanilla
    /// <c>TryInteractWith</c> hook. A pawn talks when a point on their agenda has a pregenerated exchange
    /// for a listener who is in range, both are in a talking mood, and the pair is off cooldown. Serving is
    /// a cache hit: no LLM call at interaction time. A miss stays silent and nudges pregeneration (the §9
    /// decision: silent is cheaper and truer to "pregenerated"). The register comes from the point, never
    /// from a vanilla InteractionDef. Warden (#42) and outsider barks (#52) keep their own triggers.
    /// </summary>
    public static class AgendaTrigger
    {
        public const int ScanInterval = 300;        // ~5s at 1x
        public const float ServeRadius = 8f;        // matches the drip-feed playback range
        public const int MaxServesPerScan = 1;      // ambient, not a chorus

        /// <summary>One scan of a map: serve up to <see cref="MaxServesPerScan"/> pooled point conversations.
        /// Returns the number served. Per-map pair cooldowns and a rotation cursor are owned by the caller.</summary>
        public static int Scan(Map map, SynapseConversationsMapComponent mc, int now, Dictionary<string, int> lastServedByPair, ref int rotation)
        {
            var wc = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            if (wc == null || map == null) return 0;

            var pooled = wc.PooledPoints().ToList();
            var byId = IndexSpawned(map);
            int served = 0;

            if (pooled.Count > 0)
            {
                int start = rotation;
                for (int k = 0; k < pooled.Count && served < MaxServesPerScan; k++)
                {
                    var c = pooled[(k + start) % pooled.Count];
                    if (!byId.TryGetValue(c.initiatorId, out var speaker) || !byId.TryGetValue(c.recipientId, out var listener)) continue;
                    if (!PairEligible(speaker, listener, now, lastServedByPair, mc, out _)) continue;

                    var agenda = speaker.TryGetComp<SynapseConversationAgendaComp>();
                    var point = agenda?.Find(c.pointId);
                    if (point == null || point.Told(listener.ThingID)) continue; // pool prune will collect it

                    var conv = wc.PopPooledPoint(c.initiatorId, c.recipientId, c.pointId);
                    if (conv == null) continue;
                    Serve(speaker, listener, point, agenda, conv, wc, mc, now);
                    lastServedByPair[PairKey(speaker.ThingID, listener.ThingID)] = now;
                    served++;
                }
            }
            rotation++;

            // Nothing servable: give pregeneration one nudge for a rotating conversant pawn so the pool
            // converges on who is actually near whom, instead of waiting for the hourly sweep.
            if (served == 0) Nudge(map, wc, now, rotation);
            return served;
        }

        private static void Nudge(Map map, SynapseConversationsWorldComponent wc, int now, int rotation)
        {
            var all = map.mapPawns.AllPawnsSpawned;
            if (all.Count == 0) return;
            int start = rotation % all.Count;
            for (int k = 0; k < all.Count; k++)
            {
                var p = all[(k + start) % all.Count];
                if (p == null || p.Dead || p.RaceProps == null || !p.RaceProps.Humanlike) continue;
                var agenda = p.TryGetComp<SynapseConversationAgendaComp>();
                if (agenda == null || agenda.IsEmpty) continue;
                if (!RimSynapse.SynapseCoreProviders.MayConverse(p) && LinkBank.RoleOf(p) == LinkRole.None) continue;
                if (AgendaPregeneration.TryPregenerateFor(p, now, all, wc, 1) > 0) return;
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        // Gates
        // ═════════════════════════════════════════════════════════════════════════════════════════

        public static bool PairEligible(Pawn speaker, Pawn listener, int now, Dictionary<string, int> lastServedByPair,
            SynapseConversationsMapComponent mc, out string why)
        {
            if (!InTalkingMood(speaker, out why)) { why = "speaker " + why; return false; }
            if (!InTalkingMood(listener, out why)) { why = "listener " + why; return false; }
            if (speaker.Map != listener.Map) { why = "different maps"; return false; }
            float d = speaker.Position.DistanceTo(listener.Position);
            if (d > ServeRadius) { why = $"out of range ({d:F1})"; return false; }
            if (mc != null && (mc.IsInPlayback(speaker) || mc.IsInPlayback(listener))) { why = "mid-exchange"; return false; }
            int cooldown = Mathf.Max(0, Mathf.RoundToInt((RimSynapseConversationsMod.Settings?.dialogueCooldownHours ?? 1f) * 2500f));
            if (cooldown > 0 && lastServedByPair != null
                && lastServedByPair.TryGetValue(PairKey(speaker.ThingID, listener.ThingID), out int last) && now - last < cooldown)
            { why = $"pair cooldown ({(cooldown - (now - last)) / 2500f:F1}h left)"; return false; }
            why = null;
            return true;
        }

        /// <summary>The "talking mood" gate (§4 point 3): awake, upright, not drafted or broken, able to
        /// speak, not in a job that shouldn't be interrupted, not in combat. Mood itself is NOT a gate — a
        /// low mood is exactly when a deep-talk point wants out.</summary>
        public static bool InTalkingMood(Pawn p, out string why)
        {
            why = null;
            if (p == null || !p.Spawned || p.Dead) { why = "not present"; return false; }
            if (p.Downed) { why = "downed"; return false; }
            if (!p.Awake()) { why = "asleep"; return false; }
            if (p.Drafted) { why = "drafted"; return false; }
            if (p.InMentalState) { why = "mental state"; return false; }
            if (p.health?.capacities != null && !p.health.capacities.CapableOf(PawnCapacityDefOf.Talking)) { why = "cannot talk"; return false; }
            if (p.mindState?.enemyTarget != null) { why = "in combat"; return false; }
            var job = p.CurJob;
            if (job?.def != null && !job.def.casualInterruptible) { why = "busy: " + job.def.defName; return false; }
            return true;
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        // Serve → consume
        // ═════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Play a pooled point conversation exactly as the live path applied a generated one (first
        /// line now, the rest drip-fed while in range; offsets; memories; history; metrics), then consume the
        /// point for this listener. No gating here — the debug force path calls this directly.</summary>
        public static void Serve(Pawn speaker, Pawn listener, TalkingPoint point, SynapseConversationAgendaComp agenda,
            PreGeneratedConversation conv, SynapseConversationsWorldComponent wc, SynapseConversationsMapComponent mc, int now)
        {
            if (speaker == null || listener == null || conv == null || wc == null) return;
            string idA = speaker.ThingID, idB = listener.ThingID;

            var lines = new List<SynapseConversationMessage>();
            if (conv.lines != null && conv.lines.Count > 0)
            {
                foreach (var l in conv.lines)
                    if (l != null && !string.IsNullOrWhiteSpace(l.text))
                        lines.Add(new SynapseConversationMessage(l.speakerId == idB ? idB : idA, l.text, 0));
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(conv.initiatorStatement)) lines.Add(new SynapseConversationMessage(idA, conv.initiatorStatement, 0));
                if (!string.IsNullOrWhiteSpace(conv.recipientResponse)) lines.Add(new SynapseConversationMessage(idB, conv.recipientResponse, 0));
            }
            if (lines.Count == 0) return;

            var conversation = SynapseConversationsWorldComponent.GetOrStartConversation(wc, speaker, listener, now);

            var first = lines[0];
            first.gameTick = now;
            conversation.messages.Add(first);
            conversation.lastTick = now;
            while (conversation.messages.Count > 50) conversation.messages.RemoveAt(0);

            if (Find.TickManager.CurTimeSpeed == TimeSpeed.Normal)
            {
                bool fromSpeaker = first.sender == idA;
                UI.SpeechBubbleManager.AddBubble(fromSpeaker ? speaker : listener, fromSpeaker ? listener : speaker, first.message, fromSpeaker ? 0 : 270, 4.5f);
            }

            Patch_Pawn_InteractionsTracker_TryInteractWith.ApplyPsychologyOffsets(speaker, listener, conv.trustOffset, conv.familiarityOffset);
            Patch_Pawn_InteractionsTracker_TryInteractWith.ApplyVanillaAffinityThought(speaker, listener, conv.affinityOffset);
            string label = (point?.register ?? (conv.isDeep ? PointRegister.DeepTalk : PointRegister.ChitChat)) == PointRegister.DeepTalk ? "deep-talk" : "chit-chat";
            Patch_Pawn_InteractionsTracker_TryInteractWith.PropagateContextMemories(speaker, listener, label, first.message);
            conversation.PushRecentTopic(conv.topicDefName);

            float dist = speaker.Spawned && listener.Spawned ? speaker.Position.DistanceTo(listener.Position) : -1f;
            ConversationMetrics.Add(speaker, listener, conv.topicDefName, conv.isDeep, dist, dist, 0, 0, "agenda");

            if (lines.Count > 1 && mc != null)
                mc.EnqueuePlayback(speaker, listener, conversation, lines.GetRange(1, lines.Count - 1));

            // Consume (§3): told THIS listener; a point whose whole audience has heard it is spent.
            // Propagate (§5): the listener gains a derived Heard point — the rumor mill.
            // Count (§6): who tells whom, the raw material for cliques.
            if (point != null)
            {
                point.MarkTold(idB);
                if (point.FullyTold && agenda != null) agenda.Prune(p => p.id == point.id);
                AgendaPropagation.OnServed(speaker, listener, point, now);
            }
            wc.RecordExchange(idA, idB);
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════════════════════════

        public static string PairKey(string a, string b)
            => string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;

        private static Dictionary<string, Pawn> IndexSpawned(Map map)
        {
            var all = map.mapPawns.AllPawnsSpawned;
            var d = new Dictionary<string, Pawn>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p != null && !p.Dead && p.RaceProps != null && p.RaceProps.Humanlike) d[p.ThingID] = p;
            }
            return d;
        }
    }
}
