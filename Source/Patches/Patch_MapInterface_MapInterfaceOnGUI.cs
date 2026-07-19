using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;
using RimWorld;

namespace RimSynapse.Conversations.UI
{
    public class ActiveSpeechBubble
    {
        public Pawn speaker;
        public string text;
        public int expiresAtTick;
    }

    public static class SpeechBubbleManager
    {
        public static List<ActiveSpeechBubble> activeBubbles = new List<ActiveSpeechBubble>();

        public static void AddBubble(Pawn speaker, string text, float durationSeconds = 5.5f)
        {
            if (speaker == null || string.IsNullOrEmpty(text)) return;

            // Enforce thread safety by running on the main game thread
            SynapseGameComponent.Enqueue(() =>
            {
                // Remove existing bubble for this speaker to prevent stacking
                activeBubbles.RemoveAll(b => b.speaker == speaker);

                activeBubbles.Add(new ActiveSpeechBubble
                {
                    speaker = speaker,
                    text = text,
                    expiresAtTick = Find.TickManager.TicksGame + (int)(durationSeconds * 60f)
                });
            });
        }

        public static void DrawBubbles()
        {
            // Disabled to optimize performance and rely on vanilla interaction bubbles.
        }
    }
}

namespace RimSynapse.Conversations.Patches
{
    /// <summary>
    /// Harmony patch on MapInterface.MapInterfaceOnGUI to render active speech bubbles and the Storyteller Chat HUD button.
    /// </summary>
    [HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_AfterMainTabs))]
    public static class Patch_MapInterface_MapInterfaceOnGUI
    {
        public static void Postfix()
        {
            // Render active speech bubbles on the HUD
            UI.SpeechBubbleManager.DrawBubbles();

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
