using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimSynapse.Conversations
{
    /// <summary>
    /// A short-term conversation record. Multiway (#40): a conversation has a SET of participants, not a
    /// fixed pair — a settled, co-present pawn joins the room's active conversation rather than starting a
    /// private one (the kitchen/dining model). Each <see cref="SynapseConversationMessage.sender"/> is a
    /// participant's ThingID, so any speaker's line is stored faithfully.
    ///
    /// Save compat: pre-#40 records scribed <c>pawnAId</c>/<c>pawnBId</c>. Those are still read, and on load
    /// a legacy pair is migrated into <see cref="participantIds"/> so old saves keep their history.
    /// </summary>
    public class PawnConversation : IExposable
    {
        /// <summary>Everyone who has spoken in or been addressed by this conversation (ThingIDs), in join order.</summary>
        public List<string> participantIds = new List<string>();

        // Legacy pair fields — load-only, migrated into participantIds. Not written on save any more.
        private string pawnAId;
        private string pawnBId;

        public List<SynapseConversationMessage> messages = new List<SynapseConversationMessage>();
        public int lastTick;

        /// <summary>Recently-used topic defNames for this conversation (most recent last), so consecutive
        /// conversations avoid repeating a topic. Capped ring buffer.</summary>
        public List<string> recentTopics = new List<string>();
        private const int MaxRecentTopics = 4;

        public PawnConversation()
        {
        }

        public PawnConversation(IEnumerable<string> participants, int lastTick)
        {
            if (participants != null)
                foreach (var id in participants) AddParticipant(id);
            this.lastTick = lastTick;
        }

        public PawnConversation(string idA, string idB, int lastTick)
            : this(new[] { idA, idB }, lastTick)
        {
        }

        /// <summary>Is this pawn one of the participants?</summary>
        public bool Involves(string id) => !string.IsNullOrEmpty(id) && participantIds.Contains(id);

        /// <summary>Add a participant (idempotent, preserves join order). Returns true if newly added.</summary>
        public bool AddParticipant(string id)
        {
            if (string.IsNullOrEmpty(id) || participantIds.Contains(id)) return false;
            participantIds.Add(id);
            return true;
        }

        /// <summary>The other participants from this pawn's point of view.</summary>
        public IEnumerable<string> Others(string selfId) => participantIds.Where(p => p != selfId);

        /// <summary>Record a topic as most-recently used, de-duplicating and capping the history.</summary>
        public void PushRecentTopic(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return;
            recentTopics.Remove(defName);
            recentTopics.Add(defName);
            while (recentTopics.Count > MaxRecentTopics) recentTopics.RemoveAt(0);
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref participantIds, "participantIds", LookMode.Value);
            // Legacy pair fields: still read from old saves, defaulted to null for new ones.
            Scribe_Values.Look(ref pawnAId, "pawnAId");
            Scribe_Values.Look(ref pawnBId, "pawnBId");
            Scribe_Collections.Look(ref messages, "messages", LookMode.Deep);
            Scribe_Values.Look(ref lastTick, "lastTick");
            Scribe_Collections.Look(ref recentTopics, "recentTopics", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (participantIds == null) participantIds = new List<string>();
                if (messages == null) messages = new List<SynapseConversationMessage>();
                if (recentTopics == null) recentTopics = new List<string>();
                MigrateLegacyPairIfNeeded();
            }
        }

        /// <summary>Fold a pre-#40 <c>pawnAId</c>/<c>pawnBId</c> pair into <see cref="participantIds"/> when the
        /// participant set is empty (an old save), then clear the legacy fields. Idempotent. Exposed so it is
        /// unit-testable without a Scribe context.</summary>
        public void MigrateLegacyPairIfNeeded()
        {
            if (participantIds == null) participantIds = new List<string>();
            if (participantIds.Count == 0)
            {
                AddParticipant(pawnAId);
                AddParticipant(pawnBId);
            }
            pawnAId = null;
            pawnBId = null;
        }

        /// <summary>Test seam: set the legacy pair fields as an old save would have scribed them.</summary>
        public void SetLegacyPairForTest(string a, string b) { pawnAId = a; pawnBId = b; }
    }
}
