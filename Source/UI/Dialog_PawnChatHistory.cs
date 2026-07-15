using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;

namespace RimSynapse.Chat
{
    /// <summary>
    /// Dual-pane window that resembles a personal chat application,
    /// displaying a list of contacts on the left and conversation bubble history on the right.
    /// </summary>
    public class Dialog_PawnChatHistory : Window
    {
        private Pawn pawn;
        private Pawn selectedRecipient;
        private Vector2 leftScrollPosition = Vector2.zero;
        private Vector2 rightScrollPosition = Vector2.zero;

        public override Vector2 InitialSize => new Vector2(720f, 540f);

        public Dialog_PawnChatHistory(Pawn pawn)
        {
            this.pawn = pawn;
            doCloseX = true;
            closeOnClickedOutside = false;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var worldComp = Find.World?.GetComponent<SynapseChatWorldComponent>();
            if (worldComp == null || pawn == null) return;

            // Title
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), $"Chat History: {pawn.Name.ToStringShort}");
            Text.Font = GameFont.Small;

            // Dividing Layout
            float topY = 45f;
            float leftPaneWidth = 200f;
            float margin = 10f;
            float rightPaneX = leftPaneWidth + margin;
            float rightPaneWidth = inRect.width - rightPaneX;
            float paneHeight = inRect.height - topY - 15f;

            Rect leftRect = new Rect(0f, topY, leftPaneWidth, paneHeight);
            Rect rightRect = new Rect(rightPaneX, topY, rightPaneWidth, paneHeight);

            // Dividing line
            Widgets.DrawLineVertical(leftPaneWidth + 5f, topY, paneHeight);

            // 1. Gather all contacts who had active chats with this pawn
            var activeConvs = worldComp.pawnConversations
                .Where(c => c.pawnAId == pawn.ThingID || c.pawnBId == pawn.ThingID)
                .OrderByDescending(c => c.lastTick)
                .ToList();

            var contacts = new List<Pawn>();
            foreach (var conv in activeConvs)
            {
                string otherId = conv.pawnAId == pawn.ThingID ? conv.pawnBId : conv.pawnAId;
                Pawn otherPawn = FindPawnById(otherId);
                if (otherPawn != null && !contacts.Contains(otherPawn))
                {
                    contacts.Add(otherPawn);
                }
            }

            // Default selection
            if (selectedRecipient == null && contacts.Count > 0)
            {
                selectedRecipient = contacts[0];
            }

            // 2. Render Left Panel (Contacts list)
            float rowHeight = 45f;
            float leftScrollHeight = contacts.Count * rowHeight;
            Rect leftViewRect = new Rect(0f, 0f, leftPaneWidth - 16f, leftScrollHeight);

            Widgets.BeginScrollView(leftRect, ref leftScrollPosition, leftViewRect);
            float curY = 0f;
            for (int i = 0; i < contacts.Count; i++)
            {
                Pawn otherPawn = contacts[i];
                Rect rowRect = new Rect(0f, curY, leftPaneWidth - 16f, rowHeight - 4f);

                // Highlight states
                if (selectedRecipient == otherPawn)
                {
                    Widgets.DrawHighlightSelected(rowRect);
                }
                else
                {
                    Widgets.DrawHighlightIfMouseover(rowRect);
                }

                // Selection check
                if (Widgets.ButtonInvisible(rowRect, true))
                {
                    selectedRecipient = otherPawn;
                    rightScrollPosition = Vector2.zero;
                }

                // Render contact details
                Widgets.ThingIcon(new Rect(rowRect.x + 4f, rowRect.y + 4f, 32f, 32f), otherPawn);
                Rect labelRect = new Rect(rowRect.x + 40f, rowRect.y + 10f, rowRect.width - 44f, 25f);
                Widgets.Label(labelRect, otherPawn.Name.ToStringShort);

                curY += rowHeight;
            }
            Widgets.EndScrollView();

            // 3. Render Right Panel (Chat conversation bubbles)
            if (selectedRecipient != null)
            {
                PawnConversation conversation = activeConvs.FirstOrDefault(c => 
                    c.pawnAId == selectedRecipient.ThingID || c.pawnBId == selectedRecipient.ThingID);

                if (conversation != null && conversation.messages.Count > 0)
                {
                    float rightScrollWidth = rightPaneWidth - 16f;
                    float totalChatHeight = CalculateChatScrollHeight(conversation.messages, rightScrollWidth);
                    Rect rightViewRect = new Rect(0f, 0f, rightScrollWidth, totalChatHeight);
                    
                    Rect rightScrollRect = new Rect(rightRect.x, rightRect.y, rightRect.width, rightRect.height - 40f);

                    Widgets.BeginScrollView(rightScrollRect, ref rightScrollPosition, rightViewRect);
                    float chatY = 5f;
                    
                    foreach (var msg in conversation.messages)
                    {
                        bool isSenderSelf = msg.sender == pawn.ThingID;
                        float maxBubbleWidth = rightScrollWidth * 0.72f;
                        float textHeight = Text.CalcHeight(msg.message, maxBubbleWidth - 16f);
                        float bubbleWidth = maxBubbleWidth;
                        float textWidth = Text.CalcSize(msg.message).x;
                        if (textWidth < maxBubbleWidth - 16f)
                        {
                            bubbleWidth = Mathf.Max(60f, textWidth + 20f);
                        }

                        float bubbleHeight = textHeight + 14f;
                        float bubbleX = isSenderSelf ? (rightScrollWidth - bubbleWidth - 10f) : 10f;
                        Rect bubbleRect = new Rect(bubbleX, chatY, bubbleWidth, bubbleHeight);

                        // Draw chat bubble
                        Color bubbleColor = isSenderSelf 
                            ? new Color(0.12f, 0.28f, 0.44f, 0.65f) // messaging blue
                            : new Color(0.22f, 0.22f, 0.22f, 0.65f); // grey
                        Widgets.DrawBoxSolid(bubbleRect, bubbleColor);
                        Widgets.DrawBox(bubbleRect, 1);

                        // Draw text
                        Rect textRect = new Rect(bubbleRect.x + 8f, bubbleRect.y + 6f, bubbleRect.width - 16f, textHeight);
                        Widgets.Label(textRect, msg.message);

                        // Draw timestamp
                        string timeStr = FormatTimestamp(msg.gameTick);
                        Rect timeRect = new Rect(bubbleRect.x, chatY + bubbleHeight + 1f, bubbleRect.width, 15f);
                        
                        Text.Font = GameFont.Tiny;
                        GUI.color = new Color(0.65f, 0.65f, 0.65f, 0.85f);
                        Text.Anchor = isSenderSelf ? TextAnchor.UpperRight : TextAnchor.UpperLeft;
                        Widgets.Label(timeRect, timeStr);
                        Text.Anchor = TextAnchor.UpperLeft;
                        GUI.color = Color.white;
                        Text.Font = GameFont.Small;

                        chatY += bubbleHeight + 20f;
                    }
                    Widgets.EndScrollView();

                    // Clear conversation button
                    Rect clearBtnRect = new Rect(rightRect.x + rightRect.width - 90f, rightRect.y + rightRect.height - 30f, 90f, 30f);
                    if (Widgets.ButtonText(clearBtnRect, "Clear"))
                    {
                        worldComp.pawnConversations.Remove(conversation);
                        selectedRecipient = null;
                    }
                }
                else
                {
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(rightRect, "No messages in history.");
                    Text.Anchor = TextAnchor.UpperLeft;
                }
            }
            else
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(rightRect, "Select a contact to view conversation history.");
                Text.Anchor = TextAnchor.UpperLeft;
            }
        }

        private float CalculateChatScrollHeight(List<SynapseChatMessage> messages, float width)
        {
            float total = 10f;
            foreach (var msg in messages)
            {
                float maxBubbleWidth = width * 0.72f;
                float textHeight = Text.CalcHeight(msg.message, maxBubbleWidth - 16f);
                total += textHeight + 34f;
            }
            return total;
        }

        private static string FormatTimestamp(int gameTick)
        {
            float hoursAgo = (Find.TickManager.TicksGame - gameTick) / 2500f;
            if (hoursAgo < 1f)
            {
                int mins = Mathf.RoundToInt(hoursAgo * 60f);
                return mins <= 1 ? "Just now" : $"{mins}m ago";
            }
            else if (hoursAgo < 24f)
            {
                return $"{hoursAgo:F1}h ago";
            }
            else
            {
                float daysAgo = hoursAgo / 24f;
                return $"{daysAgo:F1}d ago";
            }
        }

        private Pawn FindPawnById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            foreach (var map in Find.Maps)
            {
                if (map.mapPawns == null) continue;
                var p = map.mapPawns.AllPawns.FirstOrDefault(x => x.ThingID == id);
                if (p != null) return p;
            }

            var worldPawn = Find.WorldPawns?.AllPawnsAlive?.FirstOrDefault(x => x.ThingID == id);
            return worldPawn;
        }
    }
}
