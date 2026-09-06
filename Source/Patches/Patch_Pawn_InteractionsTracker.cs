namespace RimSynapse.Conversations.Patches
{
    /// <summary>
    /// Historical name, kept because the partial file InteractionDialogue.cs and its callers reference it:
    /// this class NO LONGER patches <c>Pawn_InteractionsTracker.TryInteractWith</c>. The vanilla
    /// social-interaction hook (Chitchat / DeepTalk) and its personality "initiation / response chance"
    /// gates were retired in #60 step 4 — pawns now talk because a talking point on their agenda has a
    /// pregenerated exchange for a listener in range (<see cref="Generation.AgendaTrigger"/>).
    /// What remains in the partial is the generation + apply machinery shared with agenda pregeneration:
    /// the debug-only live force path (<c>ForceConversation</c>), the per-line-speaker parser, social
    /// offsets, memory propagation and earshot.
    /// </summary>
    public static partial class Patch_Pawn_InteractionsTracker_TryInteractWith
    {
    }
}
