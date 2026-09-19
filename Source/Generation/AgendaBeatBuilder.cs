using System;
using System.Linq;
using Verse;
using RimSynapse.Comps;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>
    /// Turns a <see cref="TalkingPoint"/> into the <see cref="ConversationBeat"/> the thin prompt phrases
    /// (#60 step 3). The point IS the beat: its subject, its register, and stances coloured by who is
    /// listening. This is the agenda-side replacement for <see cref="ConversationBeatResolver"/>'s reactive
    /// picking — same beat contract, so <see cref="ThinDialoguePrompt"/> and
    /// <see cref="SocialOffsetCalculator"/> need no changes.
    /// </summary>
    public static class AgendaBeatBuilder
    {
        /// <summary>The point's kind from its subject key: "memory" for a Core memId, else the synthetic
        /// prefix ("link", "rumor", "activity", "bond", "ambient").</summary>
        public static string Kind(TalkingPoint point)
        {
            string id = point?.subjectMemId;
            if (string.IsNullOrEmpty(id)) return "memory";
            int c = id.IndexOf(':');
            return c > 0 ? id.Substring(0, c) : "memory";
        }

        /// <summary>Recent-topic / metrics label. Memory points keep the resolver's "event:&lt;memId&gt;" scheme;
        /// synthetic points already carry a prefixed key.</summary>
        public static string TopicKeyFor(TalkingPoint point)
        {
            if (point == null || string.IsNullOrEmpty(point.subjectMemId)) return "point";
            return Kind(point) == "memory" ? "event:" + point.subjectMemId : point.subjectMemId;
        }

        public static BeatTone ToneFor(PointRegister register)
            => register == PointRegister.DeepTalk ? BeatTone.Heartfelt : BeatTone.Casual;

        public static ConversationBeat FromPoint(Pawn speaker, Pawn listener, TalkingPoint point)
        {
            if (point == null) throw new ArgumentNullException(nameof(point));
            bool deep = point.register == PointRegister.DeepTalk;
            string colour = OpinionColour(listener, speaker);

            var beat = new ConversationBeat
            {
                subject = point.subjectSummary,
                isDeep = deep,
                tone = ToneFor(point.register),
                framing = BeatFraming.InitiatorTells,
                topicKey = TopicKeyFor(point)
            };

            switch (Kind(point))
            {
                case "memory":
                {
                    bool shares = ListenerShares(listener, point.subjectMemId, point.subjectSummary);
                    beat.framing = shares ? BeatFraming.Shared : BeatFraming.InitiatorTells;
                    beat.initiatorStance = point.provenance == PointProvenance.Heard
                        ? "passing on something they were told, not something they saw themselves"
                        : deep ? "opening up about what it did to them" : "recounting how it actually went";
                    beat.recipientStance = colour + (shares ? "adding what they remember of it" : "hearing it for the first time and reacting");
                    break;
                }
                case "activity":
                    beat.initiatorStance = "talking about how the work has been going";
                    beat.recipientStance = colour + "chipping in about it";
                    break;
                case "bond":
                    beat.initiatorStance = deep
                        ? "confiding what has changed between them and someone else"
                        : "mentioning, half to themselves, how things have shifted with someone";
                    beat.recipientStance = colour + "reacting carefully — the person in question isn't here";
                    break;
                case "link":
                {
                    // The point IS the opener. The listener answers from their own life here (cheap facts).
                    beat.initiatorStance = $"asking them, in their own words: \"{point.subjectSummary}\"";
                    beat.subject = "what " + speaker.Name.ToStringShort + " just asked";
                    string facts = LinkContext.FactsFor(listener, speaker);
                    beat.recipientStance = colour + "answering from their own experience here"
                        + (facts != null ? $" — they can draw on this: {facts}" : "");
                    break;
                }
                case "rumor":
                {
                    beat.initiatorStance = "bringing up what they heard on the road, fishing for the real story";
                    string facts = LinkContext.FactsFor(listener, speaker);
                    beat.recipientStance = colour + "setting the record straight from having actually been here"
                        + (facts != null ? $" — they can draw on this: {facts}" : "");
                    break;
                }
                case "ambient":
                    beat.initiatorStance = "remarking on it";
                    beat.recipientStance = colour + "passing the time";
                    break;
                default:
                    beat.initiatorStance = "bringing it up";
                    beat.recipientStance = colour + "responding";
                    break;
            }
            return beat;
        }

        /// <summary>Colour the listener's stance by how they feel about the speaker, without scripting words.</summary>
        public static string OpinionColour(Pawn listener, Pawn speaker)
        {
            int op = listener?.relations?.OpinionOf(speaker) ?? 0;
            return op > 40 ? "warmly " : op < -20 ? "curtly " : "";
        }

        /// <summary>Did the listener live the same memory (same memId, or the same summary text)?</summary>
        public static bool ListenerShares(Pawn listener, string memId, string summary)
        {
            var core = listener?.TryGetComp<SynapseCorePawnComp>();
            if (core?.memories == null) return false;
            foreach (var m in core.memories)
            {
                if (m == null) continue;
                if (!string.IsNullOrEmpty(memId) && m.memId == memId) return true;
                if (!string.IsNullOrEmpty(summary) && string.Equals(m.summary, summary, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
