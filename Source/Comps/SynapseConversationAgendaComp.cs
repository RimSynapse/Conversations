using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimSynapse.Conversations.Comps
{
    /// <summary>
    /// A pawn's conversational agenda: the few things they currently WANT to talk about (#60, Phase 1 §1).
    /// Per-pawn state that scribes with the pawn and dies with it — no world-component lifecycle
    /// bookkeeping. Injected onto every humanlike ThingDef by <see cref="SynapseConversationsInjector"/>.
    /// Pure container: formation, matching, decay and propagation are the lifecycle stages (§3) and live
    /// in their own services; this comp only owns the list and enforces the cap.
    /// </summary>
    public class SynapseConversationAgendaComp : ThingComp
    {
        /// <summary>Keep only the top N points by salience (§3 "cap &amp; prioritise", start N=4) —
        /// forces pawns to have a FEW things they actually care about.</summary>
        public const int MaxPoints = 4;

        public List<TalkingPoint> points = new List<TalkingPoint>();

        /// <summary>Subjects recently pruned or consumed (ring, newest last) so formation does not reform
        /// the same point the moment decay retires it.</summary>
        public List<string> retiredSubjects = new List<string>();
        public const int MaxRetired = 16;

        /// <summary>Bond-shift baselines (rule 4): last-seen Psychology trust per other pawn (their unique
        /// load id, as the social network keys them).</summary>
        public Dictionary<string, float> bondTrust = new Dictionary<string, float>();

        /// <summary>Per-other-pawn WARMTH baseline (#70) — the "do they like each other" axis of the
        /// relationship compass, now live on Psychology's SocialRecord. Mirrors <see cref="bondTrust"/>;
        /// a notable swing forms a "grown fond of / cooled on" point.</summary>
        public Dictionary<string, float> bondWarmth = new Dictionary<string, float>();

        /// <summary>Colony rumors (rule 6) are seeded once per visit.</summary>
        public bool rumorsSeeded;

        /// <summary>Last-seen ideoligion certainty baseline (#66 rule 3), −1 = unset. A notable swing since
        /// this forms a "faith gained / lost" point — the bond-shift pattern pointed at conviction.</summary>
        public float certaintyBaseline = -1f;

        public Pawn Pawn => parent as Pawn;
        public bool IsEmpty => points == null || points.Count == 0;
        public int Count => points?.Count ?? 0;

        public TalkingPoint Find(string pointId)
            => string.IsNullOrEmpty(pointId) ? null : points.FirstOrDefault(p => p.id == pointId);

        /// <summary>Cheap "they were there / already heard it" test used by matching (§3).</summary>
        public bool HasSubject(string subjectMemId)
            => !string.IsNullOrEmpty(subjectMemId) && points.Any(p => p.subjectMemId == subjectMemId);

        /// <summary>
        /// Add a point under the cap. Same-subject duplicates collapse to the more salient one. Returns
        /// true if the point is on the agenda afterwards (it may be rejected outright when the agenda is
        /// full of stronger points — that is the cap doing its job, not an error).
        /// </summary>
        public bool Add(TalkingPoint point)
        {
            if (point == null || string.IsNullOrEmpty(point.subjectSummary)) return false;
            if (string.IsNullOrEmpty(point.id)) point.id = TalkingPoint.NewId();

            var dup = !string.IsNullOrEmpty(point.subjectMemId) ? points.FirstOrDefault(p => p.subjectMemId == point.subjectMemId) : null;
            if (dup != null)
            {
                if (dup.salience >= point.salience) return false;
                points.Remove(dup);
            }

            points.Add(point);
            points.Sort((a, b) => b.salience.CompareTo(a.salience));
            while (points.Count > MaxPoints) points.RemoveAt(points.Count - 1);
            return points.Contains(point);
        }

        public bool Remove(string pointId)
        {
            var p = Find(pointId);
            return p != null && points.Remove(p);
        }

        public int RemoveAll(System.Predicate<TalkingPoint> match) => points.RemoveAll(match);

        /// <summary>Remove matching points AND retire their subjects, so they don't reform next pass.</summary>
        public int Prune(System.Predicate<TalkingPoint> match)
        {
            int n = 0;
            for (int i = points.Count - 1; i >= 0; i--)
            {
                if (!match(points[i])) continue;
                Retire(points[i].subjectMemId);
                points.RemoveAt(i);
                n++;
            }
            return n;
        }

        public void Retire(string subjectMemId)
        {
            if (string.IsNullOrEmpty(subjectMemId)) return;
            retiredSubjects.Remove(subjectMemId);
            retiredSubjects.Add(subjectMemId);
            while (retiredSubjects.Count > MaxRetired) retiredSubjects.RemoveAt(0);
        }

        public bool IsRetired(string subjectMemId)
            => !string.IsNullOrEmpty(subjectMemId) && retiredSubjects.Contains(subjectMemId);

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref points, "points", LookMode.Deep);
            Scribe_Collections.Look(ref retiredSubjects, "retiredSubjects", LookMode.Value);
            Scribe_Collections.Look(ref bondTrust, "bondTrust", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref bondWarmth, "bondWarmth", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref rumorsSeeded, "rumorsSeeded", false);
            Scribe_Values.Look(ref certaintyBaseline, "certaintyBaseline", -1f);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (points == null) points = new List<TalkingPoint>();
                points.RemoveAll(p => p == null);
                if (retiredSubjects == null) retiredSubjects = new List<string>();
                if (bondTrust == null) bondTrust = new Dictionary<string, float>();
                if (bondWarmth == null) bondWarmth = new Dictionary<string, float>();
            }
        }
    }
}
