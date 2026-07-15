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

            // Register MCP Tools
            API.ChatMcpTools.RegisterTools();

            // Apply Harmony patches
            var harmony = new Harmony("RimSynapse.Chat");
            harmony.PatchAll();

            SynapseLogger.Info("Chat mod initialized and Harmony patches applied successfully.", "chat");
        }

        public override string SettingsCategory() => "RimSynapse - Chat";

        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            Listing_Standard listingStandard = new Listing_Standard();
            listingStandard.Begin(inRect);

            listingStandard.Label("Note: Chat history settings and routing are configured in RimSynapse Core settings.");
            listingStandard.Gap(12f);

            if (listingStandard.ButtonText("Open Encyclopedia"))
            {
                Find.WindowStack.Add(new RimSynapse.UI.Dialog_Wiki());
            }

            listingStandard.End();
            base.DoSettingsWindowContents(inRect);
        }
    }
}
