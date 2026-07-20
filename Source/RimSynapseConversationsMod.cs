using HarmonyLib;
using Verse;
using System.Collections.Generic;

namespace RimSynapse.Conversations
{
    public class RimSynapseConversationsMod : Mod
    {
        public static SynapseModHandle ModHandle;
        public static RimSynapseConversationsSettings Settings;

        public RimSynapseConversationsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RimSynapseConversationsSettings>();

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

            if (RimSynapse.RimSynapseMod.Instance?.Settings != null)
            {
                var settings = RimSynapse.RimSynapseMod.Instance.Settings;
                settings.shortTermMemoryHours = listingStandard.SliderLabeled(
                    $"Chat History Retention: {settings.shortTermMemoryHours:F0} hours",
                    settings.shortTermMemoryHours, 6f, 168f,
                    1f,
                    "Configure how many in-game hours chat history between pawns is retained before it naturally fades."
                );
                listingStandard.Gap(12f);
            }

            listingStandard.CheckboxLabeled(
                "Enable Experimental Conversation Caching",
                ref Settings.enablePreGeneratedCaching,
                "Pre-generates social conversations in the background to eliminate response delay."
            );
            listingStandard.Gap(12f);

            listingStandard.Label("Speech Bubble Aesthetics:");
            listingStandard.Label($"Background Red: {Settings.bubbleRed:F2}");
            Settings.bubbleRed = listingStandard.Slider(Settings.bubbleRed, 0f, 1f);
            listingStandard.Label($"Background Green: {Settings.bubbleGreen:F2}");
            Settings.bubbleGreen = listingStandard.Slider(Settings.bubbleGreen, 0f, 1f);
            listingStandard.Label($"Background Blue: {Settings.bubbleBlue:F2}");
            Settings.bubbleBlue = listingStandard.Slider(Settings.bubbleBlue, 0f, 1f);
            listingStandard.Label($"Background Transparency (Alpha): {Settings.bubbleAlpha:F2}");
            Settings.bubbleAlpha = listingStandard.Slider(Settings.bubbleAlpha, 0.1f, 1f);
            listingStandard.Gap(12f);

            listingStandard.Label("Adjust Chat Topics:");
            var allTopics = DefDatabase<ChatTopicDef>.AllDefsListForReading;
            if (allTopics != null && allTopics.Count > 0)
            {
                foreach (var topic in allTopics)
                {
                    bool isEnabled = Settings.disabledTopicDefNames == null || !Settings.disabledTopicDefNames.Contains(topic.defName);
                    bool check = isEnabled;
                    listingStandard.CheckboxLabeled($"  {topic.topicName} ({(topic.isDeepTalk ? "Deep Talk" : "Chitchat")})", ref check, topic.description);
                    if (check != isEnabled)
                    {
                        if (check)
                        {
                            Settings.disabledTopicDefNames.Remove(topic.defName);
                        }
                        else
                        {
                            if (Settings.disabledTopicDefNames == null) Settings.disabledTopicDefNames = new List<string>();
                            Settings.disabledTopicDefNames.Add(topic.defName);
                        }
                    }
                }
            }
            else
            {
                listingStandard.Label("  (No XML chat topics loaded yet)");
            }
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
