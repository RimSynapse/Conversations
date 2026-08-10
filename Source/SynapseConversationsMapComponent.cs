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
        private Dictionary<string, bool> wasInDarkness = new Dictionary<string, bool>();
        private Dictionary<string, bool> wasInFreezer = new Dictionary<string, bool>();
        private Dictionary<string, int> lastInteractionTick = new Dictionary<string, int>();

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

                // End the exchange if a participant is gone or they've drifted out of conversational range.
                if (pb.initiator == null || pb.recipient == null ||
                    !pb.initiator.Spawned || !pb.recipient.Spawned || pb.initiator.Dead || pb.recipient.Dead ||
                    pb.initiator.Position.DistanceTo(pb.recipient.Position) > MaxConversationRangeTiles)
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

            // Environmental-trigger scan. Previously gated on a single "TicksGame % 250 == 0"
            // that evaluated every eligible colonist on the same tick — a synchronized batch
            // spike (0.8 perf pass). Now each colonist is still checked once per 250 ticks but
            // hash-staggered by pawn id, and we iterate FreeColonistsSpawned (player humanlikes
            // only) instead of every spawned pawn (animals/enemies included) filtered down.
            var colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                var pawn = colonists[i];
                if (pawn == null || pawn.Downed) continue;
                if (!pawn.IsHashIntervalTick(250)) continue;

                EvaluatePawnEnvironment(pawn);
            }
        }

        /// <summary>
        /// One colonist's environmental-trigger check (darkness/freezer transitions). Split out of
        /// the tick loop so the "Force Environmental Scan" debug action can exercise it headlessly.
        /// </summary>
        private void EvaluatePawnEnvironment(Pawn pawn)
        {
            // Only check triggers if they are particularly talkative/outgoing
            if (!IsTalkative(pawn)) return;

            string pId = pawn.ThingID;

            // 1. Darkness transition check (moving from lit area to darkness < 0.2f glow)
            bool inDarkness = map.glowGrid.GroundGlowAt(pawn.Position) < 0.2f;
            wasInDarkness.TryGetValue(pId, out bool wasDark);
            if (inDarkness && !wasDark)
            {
                TryTriggerEnvironmentalConversation(pawn, "darkness", "entering darkness and commenting on the dim lighting or shadows");
            }
            wasInDarkness[pId] = inDarkness;

            // 2. Freezer transition check (moving from moderate area into temperature <= 0f)
            bool inFreezer = pawn.Position.GetTemperature(map) <= 0f;
            wasInFreezer.TryGetValue(pId, out bool wasCold);
            if (inFreezer && !wasCold)
            {
                TryTriggerEnvironmentalConversation(pawn, "freezer", "walking into the freezing cold freezer and complaining about the chilling temperature or frozen food");
            }
            wasInFreezer[pId] = inFreezer;
        }

        /// <summary>
        /// Debug/validation hook: run the environmental-trigger scan for every spawned colonist
        /// right now, bypassing the per-pawn hash-interval gate. Returns the number evaluated.
        /// </summary>
        public int ForceEnvironmentalScan()
        {
            if (Current.ProgramState != ProgramState.Playing) return 0;
            int n = 0;
            var colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                var pawn = colonists[i];
                if (pawn == null || pawn.Downed) continue;
                EvaluatePawnEnvironment(pawn);
                n++;
            }
            return n;
        }

        private bool IsTalkative(Pawn pawn)
        {
            // Social skill >= 8
            if (pawn.skills != null && pawn.skills.GetSkill(SkillDefOf.Social).Level >= 8) return true;

            // Outgoing vanilla traits (e.g. Kind)
            if (pawn.story?.traits != null)
            {
                if (pawn.story.traits.HasTrait(TraitDefOf.Kind)) return true;
            }

            // High extraversion check in Psychology comp if present. Conversations cannot
            // reference Psychology's assembly, so this stays reflection-based — but the type and
            // FieldInfo are resolved ONCE and cached (previously re-reflected per pawn per scan).
            EnsureExtraversionField();
            if (_extraversionField != null)
            {
                var comps = pawn.AllComps;
                for (int j = 0; j < comps.Count; j++)
                {
                    if (_synapsePawnCompType.IsInstanceOfType(comps[j]))
                    {
                        float val = (float)_extraversionField.GetValue(comps[j]);
                        return val > 0.6f;
                    }
                }
            }

            return false;
        }

        // Cached reflection handles for Psychology's SynapsePawnComp.extraversion (soft, optional
        // cross-mod read). Resolved once; if Psychology is absent both stay null and the check
        // is skipped.
        private static System.Type _synapsePawnCompType;
        private static System.Reflection.FieldInfo _extraversionField;
        private static bool _extraversionResolved;

        private static void EnsureExtraversionField()
        {
            if (_extraversionResolved) return;
            _extraversionResolved = true;
            _synapsePawnCompType = GenTypes.GetTypeInAnyAssembly("RimSynapse.Psychology.Comps.SynapsePawnComp");
            if (_synapsePawnCompType != null)
            {
                _extraversionField = _synapsePawnCompType.GetField("extraversion");
            }
        }

        private void TryTriggerEnvironmentalConversation(Pawn initiator, string type, string description)
        {
            int currentTick = Find.TickManager.TicksGame;
            string key = initiator.ThingID;

            // Environmental comments are on a 10,000 tick cooldown (approx 4 hours) per initiator
            if (lastInteractionTick.TryGetValue(key, out int lastTick) && (currentTick - lastTick < 10000))
            {
                return;
            }

            // Find a nearby listener to talk to within 5 cells
            Pawn recipient = null;
            foreach (var other in map.mapPawns.AllPawnsSpawned)
            {
                if (other == initiator || !other.RaceProps.Humanlike || other.Dead || other.Downed || other.Faction != Faction.OfPlayer) continue;
                if (initiator.Position.DistanceTo(other.Position) <= 5f)
                {
                    recipient = other;
                    break;
                }
            }

            if (recipient != null)
            {
                lastInteractionTick[key] = currentTick;
                Patch_Pawn_InteractionsTracker_TryInteractWith.TriggerEnvironmentalLlmDialogue(initiator, recipient, type, description);
            }
        }
    }
}
