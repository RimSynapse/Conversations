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
        public Pawn listener;
        public string text;
        public int startTick;
        public int expiresAtTick;
    }

    public static class SpeechBubbleManager
    {
        public static List<ActiveSpeechBubble> activeBubbles = new List<ActiveSpeechBubble>();

        private static Texture2D _pointerTexture;
        public static Texture2D PointerTexture
        {
            get
            {
                if (_pointerTexture == null)
                {
                    _pointerTexture = new Texture2D(16, 12);
                    for (int y = 0; y < 12; y++)
                    {
                        int width = 16 - (int)(y * 14f / 11f);
                        int startX = (16 - width) / 2;
                        for (int x = 0; x < 16; x++)
                        {
                            if (x >= startX && x < startX + width)
                                _pointerTexture.SetPixel(x, 11 - y, Color.white);
                            else
                                _pointerTexture.SetPixel(x, 11 - y, Color.clear);
                        }
                    }
                    _pointerTexture.Apply();
                }
                return _pointerTexture;
            }
        }

        public static void AddBubble(Pawn speaker, Pawn listener, string text, int delayTicks = 0, float durationSeconds = 4.5f)
        {
            if (speaker == null || string.IsNullOrEmpty(text)) return;

            // Enforce thread safety by running on the main game thread
            SynapseGameComponent.Enqueue(() =>
            {
                // Remove existing bubbles for this speaker that start at or after our delay to prevent overlap
                activeBubbles.RemoveAll(b => b.speaker == speaker && b.startTick >= Find.TickManager.TicksGame + delayTicks);

                int currentTick = Find.TickManager.TicksGame;
                int start = currentTick + delayTicks;
                int expires = start + (int)(durationSeconds * 60f);

                activeBubbles.Add(new ActiveSpeechBubble
                {
                    speaker = speaker,
                    listener = listener,
                    text = text,
                    startTick = start,
                    expiresAtTick = expires
                });
            });
        }

        public static void DrawBubbles()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
            if (Find.TickManager.CurTimeSpeed != TimeSpeed.Normal) return; // Only draw at Normal speed

            int currentTick = Find.TickManager.TicksGame;

            // Remove expired bubbles
            activeBubbles.RemoveAll(b => currentTick >= b.expiresAtTick);

            for (int i = 0; i < activeBubbles.Count; i++)
            {
                var b = activeBubbles[i];
                if (currentTick < b.startTick) continue; // Not started yet
                if (b.speaker == null || !b.speaker.Spawned || b.speaker.Dead || b.speaker.Map != Find.CurrentMap) continue;

                // Project world position of speaker head to UI screen coordinates (using 0.85f height offset to project above head)
                Vector2 speakerScreen = GenMapUI.LabelDrawPosFor(b.speaker, 0.85f);

                // Compute size and rect of bubble based on wrapped text size
                Text.Font = GameFont.Tiny;
                Vector2 textSize = Text.CalcSize(b.text);
                float maxBubbleWidth = 200f;
                float width = textSize.x + 16f;
                float height = textSize.y + 10f;

                if (width > maxBubbleWidth)
                {
                    width = maxBubbleWidth;
                    height = Text.CalcHeight(b.text, maxBubbleWidth - 16f) + 10f;
                }

                // Determine horizontal shift based on listener direction
                float bubbleCenterX = speakerScreen.x;
                if (b.listener != null && b.listener.Spawned && b.listener.Map == b.speaker.Map)
                {
                    Vector2 listenerScreen = GenMapUI.LabelDrawPosFor(b.listener, 0.85f);
                    bool listenerIsRight = listenerScreen.x > speakerScreen.x;
                    float maxShift = Math.Min(45f, width * 0.3f); // Shift bubble to the side of voice projection
                    bubbleCenterX = listenerIsRight ? (speakerScreen.x + maxShift) : (speakerScreen.x - maxShift);
                }

                // Position bubble box vertically above head
                float verticalOffset = 15f;
                Rect rect = new Rect(bubbleCenterX - width / 2f, speakerScreen.y - height - verticalOffset, width, height);

                // Skip drawing if entirely off-screen
                if (rect.xMax < 0 || rect.xMin > Verse.UI.screenWidth || rect.yMax < 0 || rect.yMin > Verse.UI.screenHeight)
                {
                    continue;
                }

                var settings = RimSynapseConversationsMod.Settings;
                float r = settings?.bubbleRed ?? 0.12f;
                float g = settings?.bubbleGreen ?? 0.12f;
                float bVal = settings?.bubbleBlue ?? 0.12f;
                float alphaVal = settings?.bubbleAlpha ?? 0.85f;

                Color bgColor = new Color(r, g, bVal, alphaVal);
                Color borderColor = new Color(0.6f, 0.6f, 0.6f, 0.7f);

                // 1. Draw bubble background
                GUI.color = bgColor;
                GUI.DrawTexture(rect, BaseContent.WhiteTex);

                // 2. Draw bubble border
                GUI.color = borderColor;
                Widgets.DrawBox(rect, 1);

                // Calculate pointer horizontal position (pointing to speaker, clamped inside bubble bounds)
                float pointerTargetX = Mathf.Clamp(speakerScreen.x, rect.x + 12f, rect.xMax - 12f);
                Rect pointerRect = new Rect(pointerTargetX - 8f, rect.yMax - 1f, 16f, 12f);

                // 3. Draw pointer triangle (seamlessly connects to bubble background, overwriting bottom border segment)
                GUI.color = bgColor;
                GUI.DrawTexture(pointerRect, PointerTexture);

                // 4. Draw pointer borders pointing from bubble bottom to speaker head projection
                GUI.color = borderColor;
                Widgets.DrawLine(new Vector2(pointerRect.x, pointerRect.y), new Vector2(pointerTargetX, pointerRect.yMax), borderColor, 1f);
                Widgets.DrawLine(new Vector2(pointerRect.xMax, pointerRect.y), new Vector2(pointerTargetX, pointerRect.yMax), borderColor, 1f);

                // 5. Draw text
                GUI.color = Color.white;
                Rect textRect = new Rect(rect.x + 8f, rect.y + 5f, rect.width - 16f, rect.height - 10f);
                Widgets.Label(textRect, b.text);
            }
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

    /// <summary>
    /// Harmony patch on MoteMaker.MakeInteractionBubble to suppress vanilla icon bubbles when playing at Normal speed.
    /// </summary>
    [HarmonyPatch(typeof(MoteMaker), "MakeInteractionBubble")]
    public static class Patch_MoteMaker_MakeInteractionBubble
    {
        public static bool Prefix()
        {
            if (Find.TickManager != null && Find.TickManager.CurTimeSpeed == TimeSpeed.Normal)
            {
                return false; // Suppress vanilla bubble at normal speed!
            }
            return true;
        }
    }
}
