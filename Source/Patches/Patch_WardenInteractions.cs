using HarmonyLib;
using Verse;
using RimWorld;
using RimSynapse.Conversations.Warden;

namespace RimSynapse.Conversations.Patches
{
    /// <summary>
    /// Upgrades vanilla warden interactions into spoken exchanges (Conversations#42), in the same spirit
    /// as <see cref="Patch_Pawn_InteractionsTracker_TryInteractWith"/>. Each of the four warden activities
    /// runs through its own InteractionWorker.Interacted(initiator = warden, recipient = prisoner); we
    /// postfix that exact moment so the conversation only fires when the warden work actually happens and
    /// respects all vanilla gating. The controller owns the enable toggle, cooldown and validity checks.
    ///
    /// Conversion, enslavement and suppression are Ideology-DLC interactions — their workers never fire
    /// without the DLC, so those modes degrade to silence with no explicit guard needed here.
    /// </summary>
    [HarmonyPatch(typeof(InteractionWorker_RecruitAttempt), nameof(InteractionWorker.Interacted))]
    public static class Patch_InteractionWorker_RecruitAttempt
    {
        // Covers both "reduce resistance" and "attempt recruit" — vanilla routes both through here.
        public static void Postfix(Pawn initiator, Pawn recipient)
            => WardenConversationController.OnWardenInteraction(initiator, recipient, WardenMode.Recruitment);
    }

    [HarmonyPatch(typeof(InteractionWorker_ConvertIdeoAttempt), nameof(InteractionWorker.Interacted))]
    public static class Patch_InteractionWorker_ConvertIdeoAttempt
    {
        public static void Postfix(Pawn initiator, Pawn recipient)
            => WardenConversationController.OnWardenInteraction(initiator, recipient, WardenMode.Conversion);
    }

    [HarmonyPatch(typeof(InteractionWorker_EnslaveAttempt), nameof(InteractionWorker.Interacted))]
    public static class Patch_InteractionWorker_EnslaveAttempt
    {
        public static void Postfix(Pawn initiator, Pawn recipient)
            => WardenConversationController.OnWardenInteraction(initiator, recipient, WardenMode.Enslavement);
    }

    [HarmonyPatch(typeof(InteractionWorker_Suppress), nameof(InteractionWorker.Interacted))]
    public static class Patch_InteractionWorker_Suppress
    {
        public static void Postfix(Pawn initiator, Pawn recipient)
            => WardenConversationController.OnWardenInteraction(initiator, recipient, WardenMode.Suppression);
    }
}
