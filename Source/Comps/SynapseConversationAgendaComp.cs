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

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref points, "points", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (points == null) points = new List<TalkingPoint>();
                points.RemoveAll(p => p == null);
            }
        }
    }
}
