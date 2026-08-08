using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimSynapse.Conversations
{
    /// <summary>
    /// Playtest instrumentation (#29): records the latency and geometry of each generated conversation so
    /// we can answer — how much in-game time passes from start to the message landing, how far apart the
    /// pawns drift, and (by varying context) how much context a good chit-chat vs deep talk actually needs.
    /// Logs one <c>[CONV-METRIC]</c> line per conversation and keeps a small ring buffer for a debug dump.
    /// </summary>
    public static class ConversationMetrics
    {
        public class Entry
        {
            public string a, b, topic, source;
            public bool deep;
            public float startDist, endDist;
            public int elapsedTicks;
            public long realMs;
            public int atTick;
        }

        private static readonly List<Entry> recent = new List<Entry>();
        private const int MaxRecords = 60;

        public static void Add(Pawn a, Pawn b, ChatTopicDef topic, float startDist, float endDist,
            int elapsedTicks, long realMs, string source)
        {
            var e = new Entry
            {
                a = a?.LabelShort ?? "?",
                b = b?.LabelShort ?? "?",
                topic = topic?.topicName ?? "Social",
                deep = topic?.isDeepTalk ?? false,
                startDist = startDist,
                endDist = endDist,
                elapsedTicks = elapsedTicks,
                realMs = realMs,
                source = source,
                atTick = Find.TickManager != null ? Find.TickManager.TicksGame : 0
            };
            recent.Add(e);
            while (recent.Count > MaxRecords) recent.RemoveAt(0);

            RimSynapse.SynapseLogger.Info("conversations",
                $"[CONV-METRIC] {e.a}->{e.b} topic=\"{e.topic}\" deep={(e.deep ? 1 : 0)} " +
                $"dist={e.startDist:F1}->{e.endDist:F1} elapsedTicks={e.elapsedTicks} realMs={e.realMs} source={e.source}");
        }

        public static IReadOnlyList<Entry> Recent => recent;

        /// <summary>Averages by chit-chat vs deep talk, for a quick read on latency and drift.</summary>
        public static string Summary()
        {
            if (recent.Count == 0) return "No conversation metrics recorded yet.";
            string Line(string label, IEnumerable<Entry> es)
            {
                var list = es.ToList();
                if (list.Count == 0) return $"{label}: none";
                return $"{label}: n={list.Count} avgTicks={list.Average(x => x.elapsedTicks):F0} " +
                       $"avgMs={list.Average(x => x.realMs):F0} avgDrift={list.Average(x => x.endDist - x.startDist):F1} tiles";
            }
            return "--- Conversation metrics ---\n" +
                   Line("Chit-chat", recent.Where(e => !e.deep)) + "\n" +
                   Line("Deep talk", recent.Where(e => e.deep)) + "\n" +
                   Line("All", recent);
        }
    }
}
