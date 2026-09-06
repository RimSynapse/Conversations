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
    }
}
