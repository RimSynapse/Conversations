using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;
using RimSynapse.Conversations.Patches;

namespace RimSynapse.Conversations
{
    public class SynapseConversationsMapComponent : MapComponent
    {

        // Drip-feed playback (Conversations#31): a conversation is generated whole in one LLM call; the
        // first line lands immediately and the remaining lines are played out here, one every
        // LineIntervalTicks, but ONLY while the two pawns stay within MaxConversationRangeTiles — if they
        // drift apart the conversation ends naturally. Not persisted (ephemeral in-flight state).
        private const int LineIntervalTicks = 240;                 // ~4s at 1x, matches bubble duration
        private const float MaxConversationRangeTiles = 8f;
        private readonly List<ConversationPlayback> activePlaybacks = new List<ConversationPlayback>();

        public SynapseConversationsMapComponent(Map map) : base(map) {}

        private class ConversationPlayback
        {
            public Pawn initiator;
            public Pawn recipient;
            public PawnConversation conversation;
            public Queue<SynapseConversationMessage> remaining;
            public int nextTick;
        }

        /// <summary>Queue the remaining (already speaker-resolved) lines of a conversation for drip-feed.</summary>
        public void EnqueuePlayback(Pawn initiator, Pawn recipient, PawnConversation conversation, List<SynapseConversationMessage> remaining)
        {
            if (initiator == null || recipient == null || remaining == null || remaining.Count == 0) return;
            activePlaybacks.Add(new ConversationPlayback
            {
                initiator = initiator,
                recipient = recipient,
                conversation = conversation,
                remaining = new Queue<SynapseConversationMessage>(remaining),
                nextTick = Find.TickManager.TicksGame + LineIntervalTicks
            });
        }

        private void ProcessConversationPlaybacks()
        {
            if (activePlaybacks.Count == 0) return;
            int now = Find.TickManager.TicksGame;

            for (int i = activePlaybacks.Count - 1; i >= 0; i--)
            {
                var pb = activePlaybacks[i];

                // End the exchange if a participant is gone or has stopped being co-present (#40): once a
                // pawn leaves the room (or, outdoors, drifts out of range) the conversation ends there rather
                // than "carrying" as they walk off — the legibility fix for lines trailing a transiting pawn.
                if (pb.initiator == null || pb.recipient == null ||
                    !pb.initiator.Spawned || !pb.recipient.Spawned || pb.initiator.Dead || pb.recipient.Dead ||
                    !Generation.ConversationPresence.CoPresent(pb.initiator, pb.recipient))
                {
                    activePlaybacks.RemoveAt(i);
                    continue;
                }

                if (now < pb.nextTick) continue;

                var line = pb.remaining.Dequeue();
                line.gameTick = now;
                pb.conversation.messages.Add(line);
                pb.conversation.lastTick = now;
                while (pb.conversation.messages.Count > 50) pb.conversation.messages.RemoveAt(0);

                if (Find.TickManager.CurTimeSpeed == TimeSpeed.Normal)
                {
                    bool fromInitiator = line.sender == pb.initiator.ThingID;
                    Pawn speaker = fromInitiator ? pb.initiator : pb.recipient;
                    Pawn listener = fromInitiator ? pb.recipient : pb.initiator;
                    UI.SpeechBubbleManager.AddBubble(speaker, listener, line.message, fromInitiator ? 0 : 270, 4.5f);
                }

                if (pb.remaining.Count == 0) activePlaybacks.RemoveAt(i);
                else pb.nextTick = now + LineIntervalTicks;
            }
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (Current.ProgramState != ProgramState.Playing) return;

            // Drip-feed runs on its own line cadence, independent of the trigger scan below.
            ProcessConversationPlaybacks();

            // The trigger (#60 step 4): serve pregenerated agenda conversations to pairs in range — replaces the
            // vanilla TryInteractWith hook and the old environmental (darkness/freezer) driver.
            ProcessAgendaTrigger();

            // Outsiders (raiders, traders, visitors) get cheap authored barks (#52), on their own slow cadence.
            ProcessOutsiderBarks();

            // Talking-point formation (#60 Phase 1 §2) — the psychology hook, on its own cadence.
            ProcessAgendaFormation();
        }

        // ── Agenda formation (#60) ───────────────────────────────────────
        // One sweep every FormationSweepInterval ticks over the spawned pawns, forming for a quarter of them
        // (staggered by thingIDNumber) — so each pawn forms roughly once per in-game hour and no tick ever
        // evaluates everyone at once. Residents run the memory/bond/activity rules; visitors, traders and
        // guests get link openers and colony rumors; raiders and prisoners are skipped (bark / warden paths).
        private const int FormationSweepInterval = 625;
        private int lastFormationSweepTick = -1;
        private int formationCohort;

        private void ProcessAgendaFormation()
        {
            int now = Find.TickManager.TicksGame;
            if (lastFormationSweepTick >= 0 && now - lastFormationSweepTick < FormationSweepInterval) return;
            lastFormationSweepTick = now;
            formationCohort = (formationCohort + 1) & 3;

            // Pregeneration (#60 step 3) rides the same sweep: after a pawn forms, their strongest points get
            // matched to a nearby listener and ONE background call each, within a small per-sweep budget so
            // the LLM queue never sees a burst.
            var wc = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            int pregenBudget = MaxPointPregenPerSweep;

            var all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                var pawn = all[i];
                if (pawn == null || pawn.Dead || pawn.RaceProps == null || !pawn.RaceProps.Humanlike) continue;
                if ((pawn.thingIDNumber & 3) != formationCohort) continue;
                if (RimSynapse.Conversations.LinkBank.RoleOf(pawn) == RimSynapse.Conversations.LinkRole.None
                    && !RimSynapse.SynapseCoreProviders.MayConverse(pawn)) continue;
                RimSynapse.Conversations.Generation.AgendaFormation.FormFor(pawn, now, all);
                if (pregenBudget > 0)
                    pregenBudget -= RimSynapse.Conversations.Generation.AgendaPregeneration.TryPregenerateFor(pawn, now, all, wc, pregenBudget);
            }
        }

        private const int MaxPointPregenPerSweep = 2;

        // ── Outsider barks (#52) ─────────────────────────────────────────
        // Pawns the colony has no relationship with never enter the LLM path; instead one of them occasionally
        // mutters an authored line. Cost control: the AllPawnsSpawned sweep runs ONCE per scan interval (not
        // per tick, and only at 1x where bubbles render), and produces at most ONE bark — ambient, not a
        // chorus. A per-pawn cooldown keeps the same raider from repeating. Reservoir pick avoids allocating.
        private int lastOutsiderScanTick = -1;
        private readonly Dictionary<int, int> lastOutsiderBarkTick = new Dictionary<int, int>();
        private const int OutsiderScanInterval = 360;    // ~6s at 1x: how often a bark is considered
        private const int OutsiderBarkCooldown = 5000;   // per pawn: ~2 in-game hours between its barks

        private void ProcessOutsiderBarks()
        {
            if (Find.TickManager.CurTimeSpeed != TimeSpeed.Normal) return; // bubbles only draw at 1x
            int now = Find.TickManager.TicksGame;
            if (lastOutsiderScanTick >= 0 && now - lastOutsiderScanTick < OutsiderScanInterval) return;
            lastOutsiderScanTick = now;

            Pawn pick = null;
            int eligible = 0;
            var all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                var pawn = all[i];
                if (Generation.OutsiderLineBank.ResolveRole(pawn) == null) continue;
                if (lastOutsiderBarkTick.TryGetValue(pawn.thingIDNumber, out int last) && now - last < OutsiderBarkCooldown) continue;
                eligible++;
                if (Rand.Range(0, eligible) == 0) pick = pawn; // reservoir sample → uniform, zero-alloc
            }
            if (pick == null) return;

            string line = Generation.OutsiderLineBank.PickLine(pick);
            if (string.IsNullOrEmpty(line)) return;
            lastOutsiderBarkTick[pick.thingIDNumber] = now;
            UI.SpeechBubbleManager.AddBubble(pick, null, line, 0, 4.5f);
        }

        // Run the environmental check over ONE cached colony list, hash-staggered. Iterating the three
        // colony lists (colonists + prisoners + slaves, #41) in place keeps the per-tick cost the same
        // shape as the 0.8 pass (#32): cached-list references, no copy, no per-tick allocation — just three
        // lists instead of one. Quest lodgers and residents still take part as recipients and through the
        // vanilla interaction-driven path; they don't drive the ambient scan. Returns the count evaluated.
        // ── Agenda trigger (#60 step 4) ─────────────────────────────────
        // The psychology/proximity scan that replaced the vanilla TryInteractWith hook: every ScanInterval
        // ticks, serve at most one pooled point conversation on this map whose speaker and listener are in
        // range, in a talking mood, and off the pair cooldown. No LLM call happens here — a miss just lets
        // pregeneration catch up (and nudges it).
        private int lastAgendaScanTick = -1;
        private int agendaRotation;
        private readonly Dictionary<string, int> lastServedTickByPair = new Dictionary<string, int>();

        private void ProcessAgendaTrigger()
        {
            int now = Find.TickManager.TicksGame;
            if (lastAgendaScanTick >= 0 && now - lastAgendaScanTick < Generation.AgendaTrigger.ScanInterval) return;
            lastAgendaScanTick = now;
            Generation.AgendaTrigger.Scan(map, this, now, lastServedTickByPair, ref agendaRotation);
        }

        /// <summary>Is this pawn mid-exchange (lines still dripping)? The trigger won't start another on them.</summary>
        public bool IsInPlayback(Pawn p)
        {
            for (int i = 0; i < activePlaybacks.Count; i++)
                if (activePlaybacks[i].initiator == p || activePlaybacks[i].recipient == p) return true;
            return false;
        }
    }
}
