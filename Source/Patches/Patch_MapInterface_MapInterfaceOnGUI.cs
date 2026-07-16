using HarmonyLib;
using UnityEngine;
using Verse;
using RimWorld;

namespace RimSynapse.Conversations.Patches
{
    /// <summary>
    /// Harmony patch on MapInterface.MapInterfaceOnGUI to render a Storyteller Chat toggle button on the HUD.
    /// </summary>
    [HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_AfterMainTabs))]
    public static class Patch_MapInterface_MapInterfaceOnGUI
    {
        public static void Postfix()
        {
            // Only engage when the LLM-enabled storyteller ("Synapse") is loaded and active
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;
            if (Find.Storyteller?.def?.defName != "Synapse") return;

            // Render the button directly above the bottom-right game speed controls
            float width = 140f;
            float height = 26f;
            float x = Verse.UI.screenWidth - width - 15f;
            float y = Verse.UI.screenHeight - height - 45f;
            Rect btnRect = new Rect(x, y, width, height);

            if (Widgets.ButtonText(btnRect, "Storyteller Chat"))
            {
                if (Find.WindowStack.IsOpen<StorytellerConversationWindow>())
                {
                    Find.WindowStack.TryRemove(typeof(StorytellerConversationWindow));
                }
                else
                {
                    Find.WindowStack.Add(new StorytellerConversationWindow());
                }
            }
        }
    }
}
