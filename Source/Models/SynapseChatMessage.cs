using Verse;

namespace RimSynapse.Chat
{
    /// <summary>
    /// Represents an individual chat message in the storyteller dialogue history.
    /// </summary>
    public class SynapseChatMessage : IExposable
    {
        public string sender; // "Player" or "Storyteller"
        public string message;
        public int gameTick;

        public SynapseChatMessage() {}

        public SynapseChatMessage(string sender, string message, int gameTick)
        {
            this.sender = sender;
            this.message = message;
            this.gameTick = gameTick;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref sender, "sender");
            Scribe_Values.Look(ref message, "message");
            Scribe_Values.Look(ref gameTick, "gameTick");
        }
    }
}
