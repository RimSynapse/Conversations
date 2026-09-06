using System.Collections.Generic;
using System.Linq;
using Verse;
using LudeonTK;
using RimSynapse.Comps;
using RimSynapse.Conversations.Comps;

namespace RimSynapse.Conversations.UI
{
    /// <summary>
    /// Debug surface for the talking-point agenda (#60 Phase 1 §8). Step 1 ships the two actions that
    /// prove the data model: "Dump agenda" (the comp is present and its list is readable) and "Add sample
    /// point" (add / cap / dedupe / scribe round-trip, with no formation logic yet). Both are
    /// ToolMapForPawns so they are reachable headlessly via run_debug_action + pawnName.
    /// </summary>
    public static class DebugActions_Agenda
    {
        private const string Cat = "conversations";

        [DebugAction("RimSynapse", "Conversations: Dump agenda (Log)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void DumpAgenda(Pawn p)
        {
            if (p == null) return;
            var agenda = p.TryGetComp<SynapseConversationAgendaComp>();
            if (agenda == null)
            {
                RimSynapse.SynapseLogger.Info(Cat, $"[RimSynapse] {p.LabelShort} has NO SynapseConversationAgendaComp — injection failed for {p.def.defName}.");
                return;
            }
            int now = Find.TickManager.TicksGame;
            RimSynapse.SynapseLogger.Info(Cat, $"--- Agenda for {p.LabelShort} ({agenda.Count}/{SynapseConversationAgendaComp.MaxPoints}) ---");
            if (agenda.IsEmpty) { RimSynapse.SynapseLogger.Info(Cat, "  (empty)"); return; }
            foreach (var pt in agenda.points)
                RimSynapse.SynapseLogger.Info(Cat, $"  {pt}  age={pt.AgeTicks(now) / 2500f:F1}h");
        }

        /// <summary>Seed a point from the pawn's newest Core memory (or a synthetic "day" point when they
        /// have none). Provisional register/salience mapping — the real formation rules and the register
        /// threshold (§9) are step 2; this exists so the container can be exercised now.</summary>
        [DebugAction("RimSynapse", "Conversations: Add sample point (newest memory)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void AddSamplePoint(Pawn p)
        {
            if (p == null) return;
            var agenda = p.TryGetComp<SynapseConversationAgendaComp>();
            if (agenda == null)
            {
                RimSynapse.SynapseLogger.Info(Cat, $"[RimSynapse] {p.LabelShort} has no SynapseConversationAgendaComp.");
                return;
            }

            var core = p.TryGetComp<SynapseCorePawnComp>();
            var mem = core?.memories?
                .Where(m => m != null && !string.IsNullOrEmpty(m.summary) && !agenda.HasSubject(m.memId))
                .OrderByDescending(m => m.absTick).FirstOrDefault();

            var point = new TalkingPoint { formedTick = Find.TickManager.TicksGame };
            if (mem != null)
            {
                mem.EnsureMemId();
                point.subjectMemId = mem.memId;
                point.subjectSummary = mem.summary;
                point.salience = mem.salience > 0f ? mem.salience : mem.weight;
                point.register = (mem.isLongTerm || mem.weight >= 0.6f) ? PointRegister.DeepTalk : PointRegister.ChitChat;
                point.secret = mem.tags != null && mem.tags.Any(t => t == "Trauma" || t == "Betrayal");
            }
            else
            {
                point.subjectMemId = "synthetic:day:" + Find.TickManager.TicksGame;
                point.subjectSummary = "how the day has been going";
                point.salience = 0.2f;
            }

            int before = agenda.Count;
            bool kept = agenda.Add(point);
            RimSynapse.SynapseLogger.Info(Cat,
                $"[RimSynapse] {p.LabelShort}: {(kept ? "ADDED" : "REJECTED (cap/dup)")} {point}  agenda {before}->{agenda.Count}");
        }

        /// <summary>Run a full formation pass for the pawn right now (decay + every rule), bypassing the sweep
        /// cadence, then dump the agenda. This is the step-2 proof-of-function (#60 §8 "Force-form point").</summary>
        [DebugAction("RimSynapse", "Conversations: Force-form points (Log)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ForceFormPoints(Pawn p)
        {
            if (p == null || p.Map == null) return;
            string trace = RimSynapse.Conversations.Generation.AgendaFormation.FormFor(p, Find.TickManager.TicksGame, p.Map.mapPawns.AllPawnsSpawned);
            RimSynapse.SynapseLogger.Info(Cat, $"[RimSynapse] Formation for {p.LabelShort} (role {LinkBank.RoleOf(p)}, conversant {RimSynapse.SynapseCoreProviders.MayConverse(p)}): {trace}");
            DumpAgenda(p);
        }

        /// <summary>Re-seed colony rumors on any pawn (clears the once-per-visit flag first) so rule 6 is
        /// inspectable without waiting for a visitor.</summary>
        [DebugAction("RimSynapse", "Conversations: Seed colony rumors (Log)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void SeedColonyRumors(Pawn p)
        {
            var agenda = p?.TryGetComp<SynapseConversationAgendaComp>();
            if (agenda == null) return;
            agenda.rumorsSeeded = false;
            int n = RimSynapse.Conversations.Generation.AgendaFormation.SeedColonyRumors(p, agenda, Find.TickManager.TicksGame);
            var wc = Find.World?.GetComponent<RimSynapse.SynapseCoreWorldComponent>();
            RimSynapse.SynapseLogger.Info(Cat, $"[RimSynapse] Seeded {n} colony rumor(s) on {p.LabelShort} from {wc?.shortTermEvents?.Count ?? 0} ledger event(s).");
            DumpAgenda(p);
        }

        /// <summary>Match the pawn's strongest point to a listener and queue ONE pregeneration now, ignoring
        /// the sweep budget (still honours the pool bound and the in-flight guard). The result arrives async —
        /// check "Dump point pool" a few seconds later. Step-3 proof-of-function (#60).</summary>
        [DebugAction("RimSynapse", "Conversations: Pregenerate top point (Log)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void PregenerateTopPoint(Pawn p)
        {
            if (p == null || p.Map == null) return;
            var agenda = p.TryGetComp<SynapseConversationAgendaComp>();
            var wc = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            if (agenda == null || wc == null) return;
            if (agenda.IsEmpty) { RimSynapse.SynapseLogger.Info(Cat, $"[RimSynapse] {p.LabelShort} has an empty agenda — force-form first."); return; }

            var all = p.Map.mapPawns.AllPawnsSpawned;
            foreach (var point in agenda.points)
            {
                var listener = RimSynapse.Conversations.Generation.AgendaPregeneration.Match(p, point, all);
                if (listener == null) { RimSynapse.SynapseLogger.Info(Cat, $"  no listener in range for {point}"); continue; }
                if (wc.HasPooledPoint(p.ThingID, listener.ThingID, point.id)) { RimSynapse.SynapseLogger.Info(Cat, $"  already pooled for {listener.LabelShort}: {point}"); continue; }
                var beat = RimSynapse.Conversations.Generation.AgendaBeatBuilder.FromPoint(p, listener, point);
                bool queued = RimSynapse.Conversations.Generation.AgendaPregeneration.QueuePointGeneration(p, listener, point, wc, Find.TickManager.TicksGame);
                RimSynapse.SynapseLogger.Info(Cat, $"[RimSynapse] {(queued ? "QUEUED" : "already in flight")} {p.LabelShort} -> {listener.LabelShort}: subject=\"{beat.subject}\" | {p.LabelShort}: {beat.initiatorStance} | {listener.LabelShort}: {beat.recipientStance} | tone={beat.tone} framing={beat.framing}");
                return;
            }
        }

        [DebugAction("RimSynapse", "Conversations: Dump point pool (Log)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void DumpPointPool()
        {
            var wc = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            if (wc == null) return;
            RimSynapse.SynapseLogger.Info(Cat, $"--- Point pool: {wc.PointPreGenCount}/{SynapseConversationsWorldComponent.MaxPointPreGensTotal} pooled, {wc.PendingPointGenCount} in flight ---");
            foreach (var c in wc.PooledPoints())
            {
                string a = SynapseConversationsWorldComponent.PawnFromId(c.initiatorId)?.LabelShort ?? c.initiatorId;
                string b = SynapseConversationsWorldComponent.PawnFromId(c.recipientId)?.LabelShort ?? c.recipientId;
                RimSynapse.SynapseLogger.Info(Cat, $"  {a} -> {b} [{c.pointId}] {(c.isDeep ? "deep" : "chit")} {c.lines?.Count ?? 2} lines, age {(Find.TickManager.TicksGame - c.generatedAtTick) / 2500f:F1}h, topic {c.topicDefName}");
                if (c.lines != null)
                    foreach (var l in c.lines)
                        RimSynapse.SynapseLogger.Info(Cat, $"      {(SynapseConversationsWorldComponent.PawnFromId(l.speakerId)?.LabelShort ?? l.speakerId)}: {l.text}");
            }
        }

        /// <summary>Serve the first pooled point conversation this pawn is the speaker of, bypassing the
        /// range / mood / cooldown gates (the listener only has to be spawned). Step-4 proof-of-function:
        /// the pooled lines play, offsets apply, the point is consumed for that listener (#60 §8).</summary>
        [DebugAction("RimSynapse", "Conversations: Force serve pooled point (Log)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ForceServePooledPoint(Pawn p)
        {
            if (p == null || p.Map == null) return;
            var wc = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            var mc = p.Map.GetComponent<SynapseConversationsMapComponent>();
            var agenda = p.TryGetComp<SynapseConversationAgendaComp>();
            if (wc == null || agenda == null) return;

            foreach (var c in wc.PooledPoints().Where(x => x.initiatorId == p.ThingID).ToList())
            {
                var listener = SynapseConversationsWorldComponent.PawnFromId(c.recipientId);
                if (listener == null || !listener.Spawned) continue;
                var point = agenda.Find(c.pointId);
                var conv = wc.PopPooledPoint(c.initiatorId, c.recipientId, c.pointId);
                if (conv == null) continue;
                RimSynapse.SynapseLogger.Info(Cat, $"[RimSynapse] Force-serving {p.LabelShort} -> {listener.LabelShort}: {(point != null ? point.ToString() : "(point already gone — serving orphan lines)")}");
                RimSynapse.Conversations.Generation.AgendaTrigger.Serve(p, listener, point, agenda, conv, wc, mc, Find.TickManager.TicksGame);
                foreach (var l in conv.lines ?? new List<PooledLine>())
                    RimSynapse.SynapseLogger.Info(Cat, $"      {(SynapseConversationsWorldComponent.PawnFromId(l.speakerId)?.LabelShort ?? l.speakerId)}: {l.text}");
                RimSynapse.SynapseLogger.Info(Cat, $"  told={point?.Told(listener.ThingID)} fullyTold={point?.FullyTold} agenda now {agenda.Count}");
                return;
            }
            RimSynapse.SynapseLogger.Info(Cat, $"[RimSynapse] Nothing pooled with {p.LabelShort} as speaker — run \"Pregenerate top point\" and wait for the call to land.");
        }

        /// <summary>Why the trigger would or wouldn't fire for this pawn right now: the talking-mood gate and,
        /// for each pooled entry they speak in, the pair gate result.</summary>
        [DebugAction("RimSynapse", "Conversations: Dump trigger gates (Log)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void DumpTriggerGates(Pawn p)
        {
            if (p == null || p.Map == null) return;
            var wc = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            var mc = p.Map.GetComponent<SynapseConversationsMapComponent>();
            bool mood = RimSynapse.Conversations.Generation.AgendaTrigger.InTalkingMood(p, out string why);
            RimSynapse.SynapseLogger.Info(Cat, $"--- Trigger gates for {p.LabelShort}: talking mood = {mood}{(why != null ? " (" + why + ")" : "")} ---");
            if (wc == null) return;
            int n = 0;
            foreach (var c in wc.PooledPoints().Where(x => x.initiatorId == p.ThingID))
            {
                n++;
                var listener = SynapseConversationsWorldComponent.PawnFromId(c.recipientId);
                if (listener == null) { RimSynapse.SynapseLogger.Info(Cat, $"  -> {c.recipientId}: listener not found"); continue; }
                bool ok = RimSynapse.Conversations.Generation.AgendaTrigger.PairEligible(p, listener, Find.TickManager.TicksGame, null, mc, out string pw);
                RimSynapse.SynapseLogger.Info(Cat, $"  -> {listener.LabelShort} [{c.pointId}]: {(ok ? "WOULD FIRE (cooldown not checked here)" : "blocked: " + pw)}");
            }
            if (n == 0) RimSynapse.SynapseLogger.Info(Cat, "  (no pooled conversations with this pawn as speaker)");
        }
    }
}
