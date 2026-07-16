using HarmonyLib;
using Verse;

namespace RimSynapse.Conversations
{
    public class RimSynapseConversationsMod : Mod
    {
        public static SynapseModHandle ModHandle;

        public RimSynapseConversationsMod(ModContentPack content) : base(content)
        {
            // Register with RimSynapse Core
            ModHandle = SynapseCore.Register("RimSynapse.Conversations", "RimSynapse - Conversations");

            // Register MCP Tools
            API.ConversationMcpTools.RegisterTools();

            // Apply Harmony patches
            var harmony = new Harmony("RimSynapse.Conversations");
            harmony.PatchAll();

            SynapseLogger.Info("Conversations mod initialized and Harmony patches applied successfully.", "conversations");
        }

        public override string SettingsCategory() => "RimSynapse - Conversations";

        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            Listing_Standard listingStandard = new Listing_Standard();
            listingStandard.Begin(inRect);

            listingStandard.Label("Note: Conversation history settings and routing are configured in RimSynapse Core settings.");
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
