using HarmonyLib;
using Verse;

namespace RimSynapse.Chat
{
    public class RimSynapseChatMod : Mod
    {
        public static SynapseModHandle ModHandle;

        public RimSynapseChatMod(ModContentPack content) : base(content)
        {
            // Register with RimSynapse Core
            ModHandle = SynapseCore.Register("RimSynapse.Chat", "RimSynapse - Chat");

            // Apply Harmony patches
            var harmony = new Harmony("RimSynapse.Chat");
            harmony.PatchAll();

            SynapseLogger.Info("Chat mod initialized and Harmony patches applied successfully.", "chat");
        }
    }
}
