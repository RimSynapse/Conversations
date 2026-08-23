using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Verse;
using RimWorld;
using RimSynapse.Comps;
using RimSynapse.Models;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>
    /// The agent (C#) that decides WHAT a conversation is about before any LLM call (Conversations#46).
    /// It reads live pawn state and picks exactly one concrete subject plus each speaker's angle, so the
    /// model only has to phrase it. This is where relevance lives — including the framing decision that
    /// keeps a pawn from claiming a memory that isn't theirs (Bill/Lipos; see Core#103).
    /// </summary>
    public static class ConversationBeatResolver
    {
        private const int RecentEventTicks = 180000; // ~3 in-game days
        private const float EventBeatChance = 0.55f;

        public static ConversationBeat Resolve(Pawn initiator, Pawn recipient, bool isDeep, ICollection<string> avoidTopics)
        {
            var initCore = initiator.TryGetComp<SynapseCorePawnComp>();
            var recipCore = recipient.TryGetComp<SynapseCorePawnComp>();

            // 1) A concrete event the initiator actually lived through — the richest, most specific beat.
            //    Deep talk always reaches for one; chit-chat takes one often but not always.
            if (isDeep || Rand.Value < EventBeatChance)
            {
                var beat = TryEventBeat(initiator, recipient, initCore, recipCore, isDeep, avoidTopics);
                if (beat != null) return beat;
            }

            // 2) Deep talk with no event: the weightiest thing quietly weighing on them.
            if (isDeep)
            {
                var burden = TopBurden(initCore);
                if (burden != null)
                {
                    return new ConversationBeat
                    {
                        subject = burden,
                        initiatorStance = "finally letting it out, quiet and raw",
                        recipientStance = RecipientColour(recipient, initiator, "listening closely"),
                        tone = BeatTone.Heartfelt,
                        isDeep = true,
                        topicKey = "burden"
                    };
                }
            }

            // 3) Chit-chat: something concrete happening in the initiator's day right now.
            string activity = RecentActivity(initCore);
            string pressing = PressingState(initiator);

            string subject;
            string initStance;
            if (activity != null && (pressing == null || Rand.Value < 0.5f))
            {
                subject = activity;
                initStance = pressing != null ? $"bringing it up while {pressing}" : "making passing conversation about it";
            }
            else if (pressing != null)
            {
                subject = $"how they're {pressing} right now";
                initStance = "not hiding that it's wearing on them";
            }
            else
            {
                subject = ObservationSubject(initiator);
                initStance = "just making small talk";
            }

            return new ConversationBeat
            {
                subject = subject,
                initiatorStance = initStance,
                recipientStance = RecipientColour(recipient, initiator, "chatting back"),
                tone = BeatTone.Casual,
                isDeep = false,
                topicKey = "day"
            };
        }

        // ── Event beat with involvement-aware framing ────────────────────
        private static ConversationBeat TryEventBeat(Pawn initiator, Pawn recipient, SynapseCorePawnComp initCore,
            SynapseCorePawnComp recipCore, bool isDeep, ICollection<string> avoidTopics)
        {
            var mem = SelectEventMemory(initCore, isDeep, avoidTopics);
            if (mem == null) return null;

            bool recipientShares = RecipientSharesEvent(recipCore, mem);
            var framing = recipientShares ? BeatFraming.Shared : BeatFraming.InitiatorTells;

            string initStance = isDeep
                ? "opening up about what it did to them"
                : "recounting how it actually went";
            string recipStance = recipientShares
                ? RecipientColour(recipient, initiator, "adding what they remember of it")
                : RecipientColour(recipient, initiator, "hearing it for the first time and reacting");

            return new ConversationBeat
            {
                subject = mem.summary,
                initiatorStance = initStance,
                recipientStance = recipStance,
                tone = isDeep ? BeatTone.Heartfelt : BeatTone.Casual,
                framing = framing,
                isDeep = isDeep,
                topicKey = "event:" + (mem.memId ?? mem.summary)
            };
        }

        /// <summary>A recent significant EventReflection the initiator lived through; deep talk takes the
        /// weightiest, chit-chat a recent one. Honours the pair's recent-topic avoid set.</summary>
        private static WeightedMemory SelectEventMemory(SynapseCorePawnComp core, bool deep, ICollection<string> avoid)
        {
            if (core?.memories == null || core.memories.Count == 0) return null;
            long nowAbs = Find.TickManager != null ? Find.TickManager.TicksAbs : 0L;
            var candidates = core.memories
                .Where(m => m != null && m.memoryType == "EventReflection" && !string.IsNullOrEmpty(m.summary))
                .Where(m => m.isLongTerm || nowAbs - m.absTick <= RecentEventTicks)
                .Where(m => avoid == null || !avoid.Contains("event:" + (m.memId ?? m.summary)))
                .ToList();
            if (candidates.Count == 0) return null;
            return deep
                ? candidates.OrderByDescending(m => m.salience > 0f ? m.salience : m.weight).First()
                : candidates.OrderByDescending(m => m.absTick).Take(5).RandomElement();
        }

        /// <summary>Best-effort involvement check until Core#103 lands: the recipient "shares" an event only
        /// if they hold a memory with the same id, or a near-identical summary. Absent that, they were not
        /// there and must not speak of it in first person.</summary>
        private static bool RecipientSharesEvent(SynapseCorePawnComp recipCore, WeightedMemory mem)
        {
            if (recipCore?.memories == null || mem == null) return false;
            foreach (var m in recipCore.memories)
            {
                if (m == null) continue;
                if (!string.IsNullOrEmpty(mem.memId) && mem.memId == m.memId) return true;
                if (!string.IsNullOrEmpty(mem.summary) && string.Equals(mem.summary, m.summary, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ── Concrete state → concrete phrases ────────────────────────────
        private static string TopBurden(SynapseCorePawnComp core)
        {
            if (core?.memories == null || core.memories.Count == 0) return null;
            var m = core.memories
                .Where(x => !string.IsNullOrEmpty(x.summary))
                .OrderByDescending(x => x.isLongTerm)
                .ThenByDescending(x => x.salience)
                .ThenByDescending(x => x.weight)
                .FirstOrDefault();
            return m?.summary;
        }

        private static string RecentActivity(SynapseCorePawnComp core)
        {
            string summary = core?.GetRecentJobsSummary();
            if (string.IsNullOrEmpty(summary)) return null;
            int comma = summary.IndexOf(',');
            string job = (comma != -1 ? summary.Substring(0, comma) : summary).Trim();
            job = StripTrailingPercent(job);
            if (string.IsNullOrEmpty(job)) return null;
            return $"the {job.ToLowerInvariant()} they've been busy with";
        }

        // Core's activity summary appends a completion percentage to each job segment
        // ("wandering (100%)"). The subject only wants the phrase, so drop a trailing " (NN%)".
        private static readonly Regex TrailingPercent = new Regex(@"\s*\(\d+(?:\.\d+)?%\)$", RegexOptions.Compiled);

        /// <summary>Strips a trailing progress annotation like " (100%)" from an activity phrase.</summary>
        public static string StripTrailingPercent(string phrase)
        {
            if (string.IsNullOrEmpty(phrase)) return phrase;
            return TrailingPercent.Replace(phrase, string.Empty).TrimEnd();
        }

        /// <summary>The single most pressing physical/emotional state, as a concrete phrase, or null if fine.</summary>
        private static string PressingState(Pawn pawn)
        {
            float food = pawn.needs?.food?.CurLevelPercentage ?? 1f;
            float rest = pawn.needs?.rest?.CurLevel ?? 1f;
            float pain = pawn.health?.hediffSet?.PainTotal ?? 0f;
            float mood = pawn.needs?.mood?.CurLevelPercentage ?? 1f;

            // Rank by severity so the worst thing surfaces.
            var candidates = new List<(float sev, string text)>();
            if (food < 0.3f) candidates.Add((0.3f - food, "hungry and running low"));
            if (rest < 0.3f) candidates.Add((0.3f - rest, "dead on their feet from no sleep"));
            if (pain > 0.2f) candidates.Add((pain, "aching from injuries"));
            if (mood < 0.3f) candidates.Add((0.3f - mood, "in a dark, fragile mood"));
            if (candidates.Count == 0) return null;
            return candidates.OrderByDescending(c => c.sev).First().text;
        }

        private static string ObservationSubject(Pawn pawn)
        {
            string weather = pawn.Map?.weatherManager?.curWeather?.label;
            if (!string.IsNullOrEmpty(weather)) return $"the {weather.ToLowerInvariant()} outside";
            return "nothing much in particular";
        }

        /// <summary>Colour the recipient's stance by how they feel about the initiator, without scripting words.</summary>
        private static string RecipientColour(Pawn recipient, Pawn initiator, string baseAction)
        {
            int op = recipient.relations?.OpinionOf(initiator) ?? 0;
            string mood = op > 40 ? "warmly " : op < -20 ? "curtly " : "";
            string own = PressingState(recipient);
            string ownClause = own != null ? $", themselves {own}" : "";
            return $"{mood}{baseAction}{ownClause}";
        }
    }
}
