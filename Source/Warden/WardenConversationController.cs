using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;
using RimSynapse.Comps;
using RimSynapse.Conversations.Generation;
using RimSynapse.Conversations.Patches;

namespace RimSynapse.Conversations.Warden
{
    /// <summary>
    /// Turns vanilla warden work into a spoken exchange (Conversations#42). The four warden
    /// InteractionWorkers (recruit, convert, enslave, suppress) postfix into <see cref="OnWardenInteraction"/>;
    /// this resolves the prisoner's real situation, builds a mode-appropriate beat, generates one multi-line
    /// exchange, and plays it out through the same bubble + drip-feed path as ambient chatter.
    ///
    /// Rides the agent-first thin-prompt path (Conversations#46): rules go in the SYSTEM message and the
    /// concrete scene (identities, the prisoner's real situation, the mode directive) in the USER message —
    /// a lone system message makes local models return an empty ack. Social effects are computed in code by
    /// <see cref="SocialOffsetCalculator"/>, whose coercive branch already encodes the warden rule that
    /// enslavement/suppression never plant warm familiarity — so no per-mode offset hand-coding here.
    ///
    /// OUTCOME COUPLING (issue #42): these conversations are FLAVOUR ONLY. They never touch resistance,
    /// will, or ideology certainty — the vanilla roll stands untouched. Coupling LLM text to recruitment
    /// success is a balance and exploit surface deferred to its own issue.
    /// </summary>
    public static class WardenConversationController
    {
        // Warden work repeats far more often than a full conversation should, so throttle per
        // warden+prisoner pair on its own window (settings.wardenConversationCooldownHours).
        private static readonly Dictionary<string, int> lastWardenTickByPair = new Dictionary<string, int>();

        // A warden exchange is a pointed few lines, not a rambling heart-to-heart.
        private const int MaxWardenLines = 8;

        private static string PairKey(Pawn warden, Pawn prisoner) => warden.ThingID + "|" + prisoner.ThingID;

        /// <summary>Trigger entry from the warden InteractionWorker postfixes. Applies the enable toggle,
        /// validity gate and cooldown, then fires a live generation.</summary>
        public static void OnWardenInteraction(Pawn warden, Pawn prisoner, WardenMode mode)
        {
            if (!(RimSynapseConversationsMod.Settings?.enableWardenConversations ?? true)) return;
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;
            if (!IsValidPair(warden, prisoner)) return;

            int nowTick = Find.TickManager.TicksGame;
            float cooldownHours = RimSynapseConversationsMod.Settings?.wardenConversationCooldownHours ?? 2f;
            int cooldownTicks = Mathf.Max(0, Mathf.RoundToInt(cooldownHours * 2500f));
            string key = PairKey(warden, prisoner);
            if (cooldownTicks > 0
                && lastWardenTickByPair.TryGetValue(key, out int last)
                && (nowTick - last) < cooldownTicks)
            {
                return;
            }

            lastWardenTickByPair[key] = nowTick; // mark synchronously — generation is async
            Generate(warden, prisoner, mode, dumpContext: false);
        }

        /// <summary>Debug/validation path (issue #42): force the exchange regardless of job state or
        /// cooldown, and dump the resolved prisoner context so it is verifiable the dialogue reflects
        /// THIS prisoner. Safe to call headlessly.</summary>
        public static void ForceWardenConversation(Pawn warden, Pawn prisoner, WardenMode mode)
        {
            if (warden == null || prisoner == null)
            {
                SynapseLogger.Info("conversations", "[RimSynapse-Warden] Missing warden or prisoner; cannot force.");
                return;
            }
            Generate(warden, prisoner, mode, dumpContext: true);
        }

        private static bool IsValidPair(Pawn warden, Pawn prisoner)
        {
            if (warden == null || prisoner == null || warden == prisoner) return false;
            if (!warden.Spawned || !prisoner.Spawned || warden.Dead || prisoner.Dead) return false;
            if (!warden.RaceProps.Humanlike || !prisoner.RaceProps.Humanlike) return false;
            return prisoner.IsPrisonerOfColony || prisoner.IsSlaveOfColony;
        }

        // ── Which prisoner-side context each mode leans on ───────────────
        private static List<string> ContextKeysFor(WardenMode mode)
        {
            switch (mode)
            {
                case WardenMode.Conversion:
                    return new List<string> { "captivity", "resistanceWill", "ideoConflict", "captureMemory" };
                case WardenMode.Recruitment:
                case WardenMode.Enslavement:
                    return new List<string> { "captivity", "resistanceWill", "prisonerComfort", "captureMemory" };
                case WardenMode.Suppression:
                default:
                    return new List<string> { "captivity", "resistanceWill", "captureMemory" };
            }
        }

        private static void Generate(Pawn warden, Pawn prisoner, WardenMode mode, bool dumpContext)
        {
            var prisonerCore = prisoner.TryGetComp<SynapseCorePawnComp>();
            string prisonerCtx = ConversationContextResolver.ResolveAll(ContextKeysFor(mode), prisoner, warden, prisonerCore);

            // The beat carries only the tone/register the offset calculator needs; the concrete substance
            // lives in the user message below (the #46 pattern). Coercive modes route through the calculator's
            // coercive branch (no warm familiarity, resentment planted).
            var beat = new ConversationBeat
            {
                tone = mode.IsCoercive() ? BeatTone.Coercive : BeatTone.Negotiating,
                isDeep = false,
                topicKey = "warden:" + mode.Label()
            };

            if (dumpContext)
            {
                SynapseLogger.Info("conversations",
                    $"--- Warden {mode.Label()} exchange: {warden.LabelShort} -> {prisoner.LabelShort} ---");
                SynapseLogger.Info("conversations",
                    $"Prisoner context block:\n{(string.IsNullOrEmpty(prisonerCtx) ? "(none resolved)" : prisonerCtx)}");
            }

            string wardenBlock = ParticipantLine(warden, prisoner, isPrisoner: false);
            string prisonerBlock = ParticipantLine(prisoner, warden, isPrisoner: true);
            var directive = BuildModeDirective(mode, warden, prisoner);

            // SYSTEM: thin rules + lines-only schema (offsets are computed in code, not asked of the model).
            string toneRule = mode.IsCoercive()
                ? "The mood is cold and controlling — threats and power, NOT friendliness."
                : "It's a wary negotiation — each is angling for something.";
            string system =
                "You write short, natural spoken dialogue between two people on a rimworld colony. " +
                toneRule + " " +
                $"Write a pointed exchange — 2 to {MaxWardenLines} lines, each 1-2 sentences under 30 words — and stop when it's done. " +
                "Make the two sound like DIFFERENT people and stay concrete about the prisoner's actual situation you're given — " +
                "no vague filler, no status-report lines. Do NOT put names, labels, or quotation marks inside the line text. " +
                "Return STRICTLY valid JSON and nothing else: {\"lines\": [\"first line\", \"reply\", \"...\"]}.";

            // USER: the concrete scene — who they are, the prisoner's real situation, and what's happening.
            string ctxBlock = string.IsNullOrEmpty(prisonerCtx) ? "" : $"\nThe prisoner's real situation right now:\n{prisonerCtx}\n";
            string user =
                $"{wardenBlock}\n{prisonerBlock}\n{ctxBlock}\n" +
                $"What's happening: {directive.scenario}\n{directive.style}\n\n" +
                $"Write their spoken exchange, alternating and STARTING with {warden.LabelShort}. Return the JSON now.";

            SynapseClient.PromptAsync(
                RimSynapseConversationsMod.ModHandle,
                system,
                user,
                result =>
                {
                    if (!result.success || string.IsNullOrEmpty(result.content))
                    {
                        if (dumpContext) SynapseLogger.Info("conversations", "[RimSynapse-Warden] Generation failed (no content).");
                        return;
                    }

                    var cleanLines = Patch_Pawn_InteractionsTracker_TryInteractWith.ParseLinesLenient(result.content)?
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Select(l => Patch_Pawn_InteractionsTracker_TryInteractWith.CleanLine(l, warden, prisoner))
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Take(MaxWardenLines)
                        .ToList();

                    if (cleanLines == null || cleanLines.Count == 0)
                    {
                        if (dumpContext) SynapseLogger.Info("conversations", "[RimSynapse-Warden] Generation returned no usable lines.");
                        return;
                    }

                    // Offsets in code (Conversations#46): the beat's tone drives persuasion vs coercion.
                    var off = SocialOffsetCalculator.Compute(warden, prisoner, beat);

                    if (dumpContext)
                    {
                        for (int i = 0; i < cleanLines.Count; i++)
                        {
                            Pawn s = (i % 2 == 0) ? warden : prisoner;
                            SynapseLogger.Info("conversations", $"  {s.LabelShort}: {cleanLines[i]}");
                        }
                        SynapseLogger.Info("conversations",
                            $"  (offsets: trust {off.trust:F1}, familiarity {off.familiarity:F1}, affinity {off.affinity:F1}; coercive={mode.IsCoercive()})");
                    }

                    SynapseGameComponent.Enqueue(() => ApplyExchange(warden, prisoner, mode, cleanLines, off));
                },
                new ChatOptions
                {
                    priority = 1,
                    requestName = $"Warden {mode.Label()}",
                    targetName = $"{warden.LabelShort} & {prisoner.LabelShort}"
                }
            );
        }

        /// <summary>Land the first line immediately, hand the rest to the map drip-feed, then apply the
        /// code-computed social effects and memories.</summary>
        private static void ApplyExchange(Pawn warden, Pawn prisoner, WardenMode mode, List<string> cleanLines, SocialOffsets off)
        {
            if (!IsValidPair(warden, prisoner)) return;

            var worldComp = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            if (worldComp == null) return;

            string idW = warden.ThingID;
            string idP = prisoner.ThingID;
            int nowTick = Find.TickManager.TicksGame;

            // Reuse the pair's conversation record so warden exchanges show in the Dialogue History too.
            PawnConversation conversation = worldComp.pawnConversations.FirstOrDefault(c =>
                (c.pawnAId == idW && c.pawnBId == idP) || (c.pawnAId == idP && c.pawnBId == idW));
            if (conversation == null)
            {
                conversation = new PawnConversation(idW, idP, nowTick);
                worldComp.pawnConversations.Add(conversation);
            }

            var lines = new List<SynapseConversationMessage>();
            for (int i = 0; i < cleanLines.Count; i++)
            {
                string senderId = (i % 2 == 0) ? idW : idP;
                lines.Add(new SynapseConversationMessage(senderId, cleanLines[i], 0));
            }

            var first = lines[0];
            first.gameTick = nowTick;
            conversation.messages.Add(first);
            conversation.lastTick = nowTick;
            while (conversation.messages.Count > 50) conversation.messages.RemoveAt(0);
            conversation.PushRecentTopic("warden:" + mode.Label());

            if (Find.TickManager.CurTimeSpeed == TimeSpeed.Normal)
            {
                UI.SpeechBubbleManager.AddBubble(warden, prisoner, first.message, 0, 4.5f);
            }

            // Coercive vs persuasion is already baked into the offsets by SocialOffsetCalculator: a coercive
            // beat yields negative trust, zero familiarity and a resentment thought; persuasion yields modest
            // opinion-based rapport. So we just apply what it computed.
            Patch_Pawn_InteractionsTracker_TryInteractWith.ApplyPsychologyOffsets(warden, prisoner, off.trust, off.familiarity);
            Patch_Pawn_InteractionsTracker_TryInteractWith.ApplyVanillaAffinityThought(warden, prisoner, off.affinity);
            Patch_Pawn_InteractionsTracker_TryInteractWith.PropagateContextMemories(warden, prisoner, mode.Label(), first.message);

            if (lines.Count > 1 && warden.Map != null)
            {
                var mapComp = warden.Map.GetComponent<SynapseConversationsMapComponent>();
                mapComp?.EnqueuePlayback(warden, prisoner, conversation, lines.GetRange(1, lines.Count - 1));
            }
        }

        // ── Per-mode prompt shaping ──────────────────────────────────────
        private struct ModeDirective { public string scenario; public string style; }

        private static ModeDirective BuildModeDirective(WardenMode mode, Pawn warden, Pawn prisoner)
        {
            string w = warden.LabelShort;
            string p = prisoner.LabelShort;
            switch (mode)
            {
                case WardenMode.Recruitment:
                    return new ModeDirective
                    {
                        scenario = $"a recruitment attempt: the warden {w} is trying to persuade the prisoner {p} to join the colony.",
                        style = "The warden makes their pitch, but the prisoner is the one negotiating: from their real conditions above, " +
                                "they name the concrete things that would actually move them — better food, warmer clothes, a real bed, " +
                                "less time in the dark — or, if they are already well kept, they push back on other grounds instead of complaining. " +
                                "Keep each line short and pointed."
                    };
                case WardenMode.Conversion:
                    string colonyIdeo = (ModsConfig.IdeologyActive ? Faction.OfPlayer?.ideos?.PrimaryIdeo?.name : null) ?? "the colony's ideoligion";
                    return new ModeDirective
                    {
                        scenario = $"an ideological conversion attempt: the warden {w} is trying to bring the prisoner {p} into {colonyIdeo}.",
                        style = "Draw on the real belief clash above — which specific precepts or memes would appeal or repel, what the prisoner " +
                                "already believes, and what abandoning their faith would cost them. The prisoner argues their own side; the warden " +
                                "presses theirs. Keep each line short."
                    };
                case WardenMode.Enslavement:
                    return new ModeDirective
                    {
                        scenario = $"an enslavement session: the warden {w} is breaking the prisoner {p}'s will to force them into slavery.",
                        style = "This is coercion, not persuasion — the warden is cold and threatening, not friendly. The prisoner is defiant, " +
                                "frightened, or hollowed out depending on how much will they have left above. Do NOT make this sound like a chat. Keep it terse and grim."
                    };
                case WardenMode.Suppression:
                default:
                    return new ModeDirective
                    {
                        scenario = $"a slave suppression session: the warden {w} is intimidating the slave {p} to crush any thought of rebellion.",
                        style = "The tone is harsh, controlling and grim — threats and reminders of who holds power, NOT friendly conversation. " +
                                "The slave answers with sullen compliance, buried resentment, or fear. Keep each line short and cold."
                    };
            }
        }

        /// <summary>One compact line of who this participant is — voice, personality, mood, and their
        /// opinion of the other. Mirrors the ambient builder but stays terse for the warden scene.</summary>
        private static string ParticipantLine(Pawn p, Pawn other, bool isPrisoner)
        {
            var core = p.TryGetComp<SynapseCorePawnComp>();
            string psych = core?.llmTraits != null && core.llmTraits.Count > 0 ? string.Join(", ", core.llmTraits) : "none";
            string traits = string.Join(", ", p.story?.traits?.allTraits?.Select(t => t.Label) ?? Enumerable.Empty<string>());
            int opinion = p.relations?.OpinionOf(other) ?? 0;
            string mood = p.needs?.mood?.CurLevelPercentage.ToStringPercent() ?? "50%";
            string voice = !string.IsNullOrEmpty(core?.voiceProfile) ? $"speaks like this — {core.voiceProfile}; " : "";
            string role = isPrisoner ? "the captive" : "the warden";

            return $"{p.LabelShort} ({role}) — {voice}personality: {traits} (Psychology: {psych}); " +
                   $"mood: {mood}; opinion of {other.LabelShort}: {opinion} (-100 to 100).";
        }
    }
}
