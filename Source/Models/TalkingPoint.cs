using System;
using System.Collections.Generic;
using Verse;

namespace RimSynapse.Conversations
{
    /// <summary>Chit-chat vs deep talk survives only as a register — derived at formation from memory
    /// weight + mood, never from a vanilla InteractionDef (#60).</summary>
    public enum PointRegister { ChitChat, DeepTalk }

    /// <summary><see cref="Heard"/> is a rumor: a derived point propagated from another pawn's agenda (#36).</summary>
    public enum PointProvenance { FirstHand, Heard }

    /// <summary>Who a point may be told to. <see cref="Only"/>/<see cref="Not"/> qualify <see cref="TalkingPoint.audiencePawnId"/>.</summary>
    public enum AudienceKind { Anyone, Only, Not }

    /// <summary>
    /// One thing a pawn WANTS to say — the unit of the motivated-dialogue agenda (#60, Phase 1 §1).
    /// Grounded in a Core memory (<see cref="subjectMemId"/> = <c>WeightedMemory.memId</c>) or a synthetic
    /// key for activity/observation points. The pregenerated dialogue is NOT here: that is pair state and
    /// lives in the pre-gen pool keyed (speaker, listener, <see cref="id"/>).
    /// </summary>
    public class TalkingPoint : IExposable
    {
        public string id;
        public string subjectMemId;
        /// <summary>Cached human phrase — becomes the beat subject.</summary>
        public string subjectSummary;
        public PointRegister register = PointRegister.ChitChat;
        /// <summary>How much they want to share — drives priority and decay rate.</summary>
        public float salience;
        public PointProvenance provenance = PointProvenance.FirstHand;
        /// <summary>If <see cref="PointProvenance.Heard"/>, who told them — rumor chains, and "never retell it to the source".</summary>
        public string sourcePawnId;
        public AudienceKind audience = AudienceKind.Anyone;
        public string audiencePawnId;
        /// <summary>Limits spread (rumor mill); derived from memory tags (trauma/betrayal) + salience.</summary>
        public bool secret;
        /// <summary>Listeners already told — consumed PER LISTENER, not globally.</summary>
        public List<string> toldIds = new List<string>();
        /// <summary>TicksGame at formation — drives decay and the retention window.</summary>
        public int formedTick;

        public static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>Does the audience rule admit this listener? (Told-status is a separate check.)</summary>
        public bool Accepts(string listenerId)
        {
            if (string.IsNullOrEmpty(listenerId)) return false;
            if (provenance == PointProvenance.Heard && listenerId == sourcePawnId) return false; // never retell to the teller
            switch (audience)
            {
                case AudienceKind.Only: return listenerId == audiencePawnId;
                case AudienceKind.Not:  return listenerId != audiencePawnId;
                default:                return true;
            }
        }

        public bool Told(string listenerId) => !string.IsNullOrEmpty(listenerId) && toldIds.Contains(listenerId);

        public void MarkTold(string listenerId)
        {
            if (!string.IsNullOrEmpty(listenerId) && !toldIds.Contains(listenerId)) toldIds.Add(listenerId);
        }

        /// <summary>A point whose whole audience has heard it is spent. Only decidable for a single-target
        /// audience; open-audience points are retired by decay, not exhaustion.</summary>
        public bool FullyTold => audience == AudienceKind.Only && Told(audiencePawnId);

        public int AgeTicks(int nowTick) => nowTick - formedTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref subjectMemId, "subjectMemId");
            Scribe_Values.Look(ref subjectSummary, "subjectSummary");
            Scribe_Values.Look(ref register, "register", PointRegister.ChitChat);
            Scribe_Values.Look(ref salience, "salience", 0f);
            Scribe_Values.Look(ref provenance, "provenance", PointProvenance.FirstHand);
            Scribe_Values.Look(ref sourcePawnId, "sourcePawnId");
            Scribe_Values.Look(ref audience, "audience", AudienceKind.Anyone);
            Scribe_Values.Look(ref audiencePawnId, "audiencePawnId");
            Scribe_Values.Look(ref secret, "secret", false);
            Scribe_Collections.Look(ref toldIds, "toldIds", LookMode.Value);
            Scribe_Values.Look(ref formedTick, "formedTick", 0);
            if (Scribe.mode == LoadSaveMode.LoadingVars && toldIds == null) toldIds = new List<string>();
        }

        public override string ToString()
        {
            string aud = audience == AudienceKind.Anyone ? "anyone" : $"{audience.ToString().ToLowerInvariant()}({audiencePawnId})";
            string prov = provenance == PointProvenance.Heard ? $"heard<{sourcePawnId}>" : "first-hand";
            return $"[{id}] \"{subjectSummary}\" {register} sal={salience:F2} {prov} aud={aud}{(secret ? " SECRET" : "")} told={toldIds.Count} mem={subjectMemId}";
        }
    }
}
