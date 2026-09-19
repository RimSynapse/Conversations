using System.Collections.Generic;
using Verse;
using RimSynapse.Conversations.Comps;
using RimSynapse.Conversations.Patches;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>
    /// Pregeneration (#60 Phase 1 step 3): point → beat → ONE background LLM call → a pooled conversation
    /// keyed (speaker, listener, pointId), so the trigger (step 4) serves it as a cache hit with no
    /// interaction-time call. Matching (§3) is pure C#: nearest pawn in range who is admitted by the
    /// point's audience, hasn't been told, and doesn't already hold the subject.
    /// </summary>
    public static class AgendaPregeneration
    {
        /// <summary>"Likelihood of meeting" for v1 = who is near right now.</summary>
        public const float MatchRadius = 20f;

        public static string PoolKey(string speakerId, string listenerId, string pointId)
            => speakerId + "|" + listenerId + "|" + pointId;

        /// <summary>Pick the listener for a point: closest candidate that the point may be told to.</summary>
        public static Pawn Match(Pawn speaker, TalkingPoint point, IReadOnlyList<Pawn> candidates, float radius = MatchRadius)
        {
            if (speaker == null || point == null || candidates == null || !speaker.Spawned) return null;
            float r2 = radius * radius;
            Pawn best = null; float bestD = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == null || c == speaker || c.Dead || !c.Spawned || c.Map != speaker.Map) continue;
                if (c.RaceProps == null || !c.RaceProps.Humanlike) continue;
                float d = (c.Position - speaker.Position).LengthHorizontalSquared;
                if (d > r2 || d >= bestD) continue;
                if (!RimSynapse.SynapseCoreProviders.MayConverse(c) && LinkBank.RoleOf(c) == LinkRole.None) continue;
                if (!point.Accepts(c.ThingID) || point.Told(c.ThingID)) continue;
                var theirs = c.TryGetComp<SynapseConversationAgendaComp>();
                if (theirs != null && theirs.HasSubject(point.subjectMemId)) continue; // they were there / already heard it
                best = c; bestD = d;
            }
            return best;
        }

        /// <summary>
        /// For a pawn's strongest points, match a listener and queue a pregeneration if none is pooled or
        /// in flight. Bounded by <paramref name="budget"/> (the sweep's per-pass allowance) and the pool's
        /// point bound. Returns how many were queued.
        /// </summary>
        public static int TryPregenerateFor(Pawn speaker, int nowTick, IReadOnlyList<Pawn> candidates,
            SynapseConversationsWorldComponent wc, int budget)
        {
            if (budget <= 0 || wc == null || speaker == null || !wc.CanPoolMorePoints) return 0;
            if (Patch_Pawn_InteractionsTracker_TryInteractWith.ShouldShedForLoad()) return 0; // background work only adds load (#38)
            var agenda = speaker.TryGetComp<SynapseConversationAgendaComp>();
            if (agenda == null || agenda.IsEmpty) return 0;

            int queued = 0;
            for (int i = 0; i < agenda.points.Count && queued < budget; i++)
            {
                var point = agenda.points[i];
                var listener = Match(speaker, point, candidates);
                if (listener == null) continue;
                if (wc.HasPooledPoint(speaker.ThingID, listener.ThingID, point.id)) continue;
                if (QueuePointGeneration(speaker, listener, point, wc, nowTick)) queued++;
            }
            return queued;
        }

        /// <summary>One LLM call for (speaker, listener, point). Returns false if already in flight.</summary>
        public static bool QueuePointGeneration(Pawn speaker, Pawn listener, TalkingPoint point,
            SynapseConversationsWorldComponent wc, int nowTick)
        {
            string key = PoolKey(speaker.ThingID, listener.ThingID, point.id);
            if (!wc.TryMarkPending(key, nowTick)) return false;

            var beat = AgendaBeatBuilder.FromPoint(speaker, listener, point);
            var prompt = ThinDialoguePrompt.Build(speaker, listener, beat, null);
            string pointId = point.id;
            bool deep = point.register == PointRegister.DeepTalk;
            string speakerName = speaker.Name.ToStringShort, listenerName = listener.Name.ToStringShort;

            RimSynapse.SynapseClient.PromptAsync(
                RimSynapseConversationsMod.ModHandle,
                prompt.system,
                prompt.user,
                result =>
                {
                    if (!result.success || string.IsNullOrEmpty(result.content))
                    {
                        RimSynapse.SynapseGameComponent.Enqueue(() => wc.ClearPending(key));
                        RimSynapse.SynapseLogger.Warn("conversations", $"[#60 pre-gen] failed (success={result.success}) for {speakerName} -> {listenerName} point {pointId}");
                        return;
                    }

                    // Same per-line-speaker parse as the live path (#40) — no second parser to drift.
                    var roster = new[] { speaker, listener };
                    var parsed = Patch_Pawn_InteractionsTracker_TryInteractWith.ParseSpeakerLines(result.content);
                    var lines = new List<PooledLine>();
                    if (parsed != null)
                    {
                        for (int i = 0; i < parsed.Count && lines.Count < Patch_Pawn_InteractionsTracker_TryInteractWith.MaxExchangeLines; i++)
                        {
                            string text = Patch_Pawn_InteractionsTracker_TryInteractWith.CleanLine(parsed[i].text, speaker, listener);
                            if (string.IsNullOrWhiteSpace(text)) continue;
                            Pawn who = Patch_Pawn_InteractionsTracker_TryInteractWith.ResolveSpeaker(parsed[i].speaker, roster, lines.Count);
                            lines.Add(new PooledLine(who.ThingID, text));
                        }
                    }

                    RimSynapse.SynapseGameComponent.Enqueue(() =>
                    {
                        wc.ClearPending(key);
                        if (lines.Count < 2)
                        {
                            RimSynapse.SynapseLogger.Warn("conversations", $"[#60 pre-gen] <2 lines parsed for {speakerName} -> {listenerName} point {pointId}");
                            return;
                        }
                        var off = SocialOffsetCalculator.Compute(speaker, listener, beat);
                        wc.AddPointPreGen(new PreGeneratedConversation
                        {
                            initiatorId = speaker.ThingID,
                            recipientId = listener.ThingID,
                            pointId = pointId,
                            isDeep = deep,
                            topicDefName = beat.topicKey,
                            initiatorStatement = lines[0].text,
                            recipientResponse = lines[1].text,
                            lines = lines,
                            trustOffset = off.trust,
                            familiarityOffset = off.familiarity,
                            affinityOffset = off.affinity,
                            generatedAtTick = Find.TickManager.TicksGame,
                            generatedAtAbsTick = Find.TickManager.TicksAbs
                        });
                        RimSynapse.SynapseLogger.Info("conversations", $"[#60 pre-gen] pooled {lines.Count}-line {(deep ? "deep" : "chit")} exchange {speakerName} -> {listenerName} for \"{beat.subject}\" (pool {wc.PointPreGenCount}/{SynapseConversationsWorldComponent.MaxPointPreGensTotal})");
                    });
                },
                new RimSynapse.ChatOptions
                {
                    priority = 5,
                    requestName = deep ? "Agenda deep talk (pre-gen)" : "Agenda chit-chat (pre-gen)",
                    targetName = $"{speakerName} -> {listenerName}"
                });
            return true;
        }
    }
}
