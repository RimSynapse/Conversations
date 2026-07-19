using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;
using RimSynapse.Comps;
using RimSynapse.Models;

namespace RimSynapse.Conversations
{
    /// <summary>
    /// Dual-pane window that resembles a personal chat application,
    /// displaying a list of contacts on the left and a Discord-style chat layout on the right.
    /// </summary>
    public class Dialog_PawnConversationHistory : Window
    {
        private enum ConversationTab
        {
            Conversations,
            Overheard
        }

        private Pawn pawn;
        private Pawn selectedRecipient;
        private Vector2 leftScrollPosition = Vector2.zero;
        private Vector2 rightScrollPosition = Vector2.zero;
        private Vector2 overheardScrollPosition = Vector2.zero;
        private ConversationTab currentTab = ConversationTab.Conversations;

        public override Vector2 InitialSize => new Vector2(720f, 540f);

        public Dialog_PawnConversationHistory(Pawn pawn)
        {
            this.pawn = pawn;
            doCloseX = true;
            closeOnClickedOutside = false;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var worldComp = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
            if (worldComp == null || pawn == null) return;

            // Title
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), $"Chat History: {pawn.Name.ToStringShort}");
            Text.Font = GameFont.Small;

            // Setup Tab space
            float topY = 75f;
            float paneHeight = inRect.height - topY - 15f;
            Rect contentRect = new Rect(0f, topY, inRect.width, paneHeight);

            List<TabRecord> tabs = new List<TabRecord>();
            tabs.Add(new TabRecord("Conversations", () => currentTab = ConversationTab.Conversations, currentTab == ConversationTab.Conversations));
            tabs.Add(new TabRecord("Overheard", () => currentTab = ConversationTab.Overheard, currentTab == ConversationTab.Overheard));
            TabDrawer.DrawTabs(contentRect, tabs, 180f);

            if (currentTab == ConversationTab.Conversations)
            {
                // Render conversations list & chat
                float leftPaneWidth = 200f;
                float margin = 10f;
                float rightPaneX = leftPaneWidth + margin;
                float rightPaneWidth = contentRect.width - rightPaneX;

                Rect leftRect = new Rect(contentRect.x, contentRect.y, leftPaneWidth, contentRect.height);
                Rect rightRect = new Rect(contentRect.x + rightPaneX, contentRect.y, rightPaneWidth, contentRect.height);

                // Dividing line
                Widgets.DrawLineVertical(contentRect.x + leftPaneWidth + 5f, contentRect.y, contentRect.height);

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

                // 3. Render Right Panel (Discord-style Chat view)
                if (selectedRecipient != null)
                {
                    PawnConversation conversation = activeConvs.FirstOrDefault(c => 
                        c.pawnAId == selectedRecipient.ThingID || c.pawnBId == selectedRecipient.ThingID);

                    if (conversation != null && conversation.messages.Count > 0)
                    {
                        float rightScrollWidth = rightPaneWidth - 16f;
                        float totalChatHeight = CalculateChatScrollHeight(conversation.messages, rightScrollWidth, pawn);
                        Rect rightViewRect = new Rect(0f, 0f, rightScrollWidth, totalChatHeight);
                        
                        Rect rightScrollRect = new Rect(rightRect.x, rightRect.y, rightRect.width, rightRect.height);

                        Widgets.BeginScrollView(rightScrollRect, ref rightScrollPosition, rightViewRect);
                        float chatY = 5f;
                        string lastDateStr = null;

                        foreach (var msg in conversation.messages)
                        {
                            // Draw Day Separator if date changed
                            string dateStr = FormatDateOnly(msg.gameTick, pawn);
                            if (dateStr != lastDateStr)
                            {
                                lastDateStr = dateStr;

                                var originalFont = Text.Font;
                                var originalAnchor = Text.Anchor;
                                var originalColor = GUI.color;

                                Text.Font = GameFont.Tiny;
                                GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.7f);
                                float sepWidth = rightScrollWidth - 20f;

                                float dateTextWidth = Text.CalcSize(dateStr).x + 10f;
                                float lineStart = (sepWidth - dateTextWidth) / 2f;

                                // draw line left
                                Widgets.DrawLineHorizontal(10f, chatY + 8f, lineStart - 10f);
                                // draw text
                                Rect dateRect = new Rect(lineStart, chatY, dateTextWidth, 20f);
                                Text.Anchor = TextAnchor.MiddleCenter;
                                Widgets.Label(dateRect, dateStr);
                                Text.Anchor = originalAnchor;
                                // draw line right
                                Widgets.DrawLineHorizontal(lineStart + dateTextWidth, chatY + 8f, sepWidth - (lineStart + dateTextWidth));

                                GUI.color = originalColor;
                                Text.Font = originalFont;
                                chatY += 25f;
                            }

                            bool isSenderSelf = msg.sender == pawn.ThingID;
                            Pawn senderPawn = isSenderSelf ? pawn : selectedRecipient;

                            // Draw Avatar
                            Rect avatarRect = new Rect(10f, chatY, 32f, 32f);
                            if (senderPawn != null)
                            {
                                Widgets.ThingIcon(avatarRect, senderPawn);
                            }

                            // Draw Name + Timestamp
                            string nameStr = senderPawn != null ? senderPawn.Name.ToStringShort : "Unknown";
                            Vector2 nameSize = Text.CalcSize(nameStr);
                            Rect nameRect = new Rect(52f, chatY, nameSize.x, 20f);
                            
                            Text.Font = GameFont.Small;
                            GUI.color = isSenderSelf ? new Color(0.35f, 0.65f, 1.0f) : new Color(0.85f, 0.85f, 0.85f);
                            Widgets.Label(nameRect, nameStr);

                            string timeStr = FormatTimeOnly(msg.gameTick, pawn);
                            Rect timeRect = new Rect(52f + nameSize.x + 8f, chatY + 2f, rightScrollWidth - 62f - nameSize.x, 18f);
                            
                            Text.Font = GameFont.Tiny;
                            GUI.color = new Color(0.55f, 0.55f, 0.55f, 0.85f);
                            Widgets.Label(timeRect, timeStr);

                            // Draw Message Content
                            float textWidth = rightScrollWidth - 62f;
                            Text.Font = GameFont.Small;
                            float textHeight = Text.CalcHeight(msg.message, textWidth);
                            Rect textRect = new Rect(52f, chatY + 22f, textWidth, textHeight);

                            GUI.color = new Color(0.92f, 0.92f, 0.95f);
                            Widgets.Label(textRect, msg.message);

                            GUI.color = Color.white;
                            float entryHeight = Mathf.Max(32f, 22f + textHeight);
                            chatY += entryHeight + 16f;
                        }
                        Widgets.EndScrollView();
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
            else if (currentTab == ConversationTab.Overheard)
            {
                // Render overheard statements
                var coreComp = pawn.TryGetComp<SynapseCorePawnComp>();
                var overheardMemories = coreComp?.memories?
                    .Where(m => m.tags != null && m.tags.Contains("overheard"))
                    .OrderByDescending(m => m.gameTick)
                    .ToList() ?? new List<WeightedMemory>();

                if (overheardMemories.Count > 0)
                {
                    float rightScrollWidth = contentRect.width - 16f;
                    float totalChatHeight = CalculateOverheardScrollHeight(overheardMemories, rightScrollWidth, pawn);
                    Rect rightViewRect = new Rect(0f, 0f, rightScrollWidth, totalChatHeight);

                    Widgets.BeginScrollView(contentRect, ref overheardScrollPosition, rightViewRect);
                    float chatY = 5f;
                    string lastDateStr = null;

                    foreach (var m in overheardMemories)
                    {
                        // Draw Day Separator if date changed
                        string dateStr = FormatDateOnly(m.gameTick, pawn);
                        if (dateStr != lastDateStr)
                        {
                            lastDateStr = dateStr;

                            var originalFont = Text.Font;
                            var originalAnchor = Text.Anchor;
                            var originalColor = GUI.color;

                            Text.Font = GameFont.Tiny;
                            GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.7f);
                            float sepWidth = rightScrollWidth - 20f;

                            float dateTextWidth = Text.CalcSize(dateStr).x + 10f;
                            float lineStart = (sepWidth - dateTextWidth) / 2f;

                            // draw line left
                            Widgets.DrawLineHorizontal(10f, chatY + 8f, lineStart - 10f);
                            // draw text
                            Rect dateRect = new Rect(lineStart, chatY, dateTextWidth, 20f);
                            Text.Anchor = TextAnchor.MiddleCenter;
                            Widgets.Label(dateRect, dateStr);
                            Text.Anchor = originalAnchor;
                            // draw line right
                            Widgets.DrawLineHorizontal(lineStart + dateTextWidth, chatY + 8f, sepWidth - (lineStart + dateTextWidth));

                            GUI.color = originalColor;
                            Text.Font = originalFont;
                            chatY += 25f;
                        }

                        Pawn initiator = null;
                        Pawn recipient = null;
                        if (m.tags.Count > 1) initiator = FindPawnById(m.tags[1]);
                        if (m.tags.Count > 2) recipient = FindPawnById(m.tags[2]);

                        // Parse the reply out of the quotes in summary
                        string reply = "";
                        int firstQuote = m.summary.IndexOf('"');
                        int lastQuote = m.summary.LastIndexOf('"');
                        if (firstQuote >= 0 && lastQuote > firstQuote)
                        {
                            reply = m.summary.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                        }
                        else
                        {
                            reply = m.summary;
                        }

                        // Draw Avatar
                        Rect avatarRect = new Rect(10f, chatY, 32f, 32f);
                        if (initiator != null)
                        {
                            Widgets.ThingIcon(avatarRect, initiator);
                        }

                        // Draw Name/Who is speaking
                        string nameStr = initiator != null ? initiator.Name.ToStringShort : "Unknown";
                        string targetStr = recipient != null ? recipient.Name.ToStringShort : "someone";
                        string spokeToStr = $" said to {targetStr}";

                        Vector2 nameSize = Text.CalcSize(nameStr);
                        Rect nameRect = new Rect(52f, chatY, nameSize.x, 20f);
                        
                        Text.Font = GameFont.Small;
                        GUI.color = new Color(0.35f, 0.65f, 1.0f);
                        Widgets.Label(nameRect, nameStr);

                        Vector2 spokeToSize = Text.CalcSize(spokeToStr);
                        Rect spokeToRect = new Rect(52f + nameSize.x, chatY, spokeToSize.x, 20f);
                        
                        Text.Font = GameFont.Small;
                        GUI.color = new Color(0.7f, 0.7f, 0.7f);
                        Widgets.Label(spokeToRect, spokeToStr);

                        // Draw time stamp
                        string timeStr = FormatTimeOnly(m.gameTick, pawn);
                        Rect timeRect = new Rect(52f + nameSize.x + spokeToSize.x + 8f, chatY + 2f, rightScrollWidth - 62f - nameSize.x - spokeToSize.x, 18f);
                        
                        Text.Font = GameFont.Tiny;
                        GUI.color = new Color(0.55f, 0.55f, 0.55f, 0.85f);
                        Widgets.Label(timeRect, timeStr);

                        // Draw message content
                        float textWidth = rightScrollWidth - 62f;
                        Text.Font = GameFont.Small;
                        float textHeight = Text.CalcHeight(reply, textWidth);
                        Rect textRect = new Rect(52f, chatY + 22f, textWidth, textHeight);

                        GUI.color = new Color(0.92f, 0.92f, 0.95f);
                        Widgets.Label(textRect, reply);

                        GUI.color = Color.white;
                        float entryHeight = Mathf.Max(32f, 22f + textHeight);
                        chatY += entryHeight + 16f;
                    }
                    Widgets.EndScrollView();
                }
                else
                {
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(contentRect, "No overheard conversations in recent history.");
                    Text.Anchor = TextAnchor.UpperLeft;
                }
            }
        }

        private float CalculateChatScrollHeight(List<SynapseConversationMessage> messages, float width, Pawn pawn)
        {
            var originalFont = Text.Font;
            Text.Font = GameFont.Small;
            float total = 10f;
            float textWidth = width - 62f;
            string lastDateStr = null;

            foreach (var msg in messages)
            {
                string dateStr = FormatDateOnly(msg.gameTick, pawn);
                if (dateStr != lastDateStr)
                {
                    lastDateStr = dateStr;
                    total += 24f;
                }
                float textHeight = Text.CalcHeight(msg.message, textWidth);
                float entryHeight = Mathf.Max(32f, 22f + textHeight);
                total += entryHeight + 16f;
            }
            Text.Font = originalFont;
            return total;
        }

        private float CalculateOverheardScrollHeight(List<WeightedMemory> memories, float width, Pawn pawn)
        {
            float total = 10f;
            float textWidth = width - 62f;
            string lastDateStr = null;

            foreach (var m in memories)
            {
                string dateStr = FormatDateOnly(m.gameTick, pawn);
                if (dateStr != lastDateStr)
                {
                    lastDateStr = dateStr;
                    total += 35f;
                }

                string reply = "";
                int firstQuote = m.summary.IndexOf('"');
                int lastQuote = m.summary.LastIndexOf('"');
                if (firstQuote >= 0 && lastQuote > firstQuote)
                {
                    reply = m.summary.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                }
                else
                {
                    reply = m.summary;
                }

                Text.Font = GameFont.Small;
                float textHeight = Text.CalcHeight(reply, textWidth);
                float entryHeight = Mathf.Max(32f, 22f + textHeight);
                total += entryHeight + 16f;
            }
            return total + 20f;
        }

        private static string FormatDateOnly(int gameTick, Pawn pawn)
        {
            float longitude = 0f;
            if (pawn != null && pawn.Tile >= 0 && Find.WorldGrid != null)
            {
                longitude = Find.WorldGrid.LongLatOf(pawn.Tile).x;
            }
            long absTick = RimSynapse.Utils.SynapseDateHelper.GameTickToAbsTick(gameTick);
            
            long ticks = absTick + (long)(longitude * (60000f / 360f));
            long ticksRemainder = ticks % 3600000L;
            if (ticksRemainder < 0) ticksRemainder += 3600000L;
            int daysTotal = (int)(ticksRemainder / 60000L);
            int quadrumIndex = daysTotal / 15;
            int dayOfQuadrum = (daysTotal % 15) + 1;
            
            int year = 5500 + (int)(ticks / 3600000L);
            if (ticks < 0 && ticksRemainder != 0) year--;

            string quadrumLabel = quadrumIndex switch
            {
                0 => "Aprimay",
                1 => "Jugust",
                2 => "Septober",
                3 => "Decembary",
                _ => "Unknown"
            };

            return $"{quadrumLabel} {dayOfQuadrum}, {year}";
        }

        private static string FormatTimeOnly(int gameTick, Pawn pawn)
        {
            float longitude = 0f;
            if (pawn != null && pawn.Tile >= 0 && Find.WorldGrid != null)
            {
                longitude = Find.WorldGrid.LongLatOf(pawn.Tile).x;
            }
            long absTick = RimSynapse.Utils.SynapseDateHelper.GameTickToAbsTick(gameTick);
            
            int hour = GenDate.HourOfDay(absTick, longitude);
            int pmHour = hour % 12;
            if (pmHour == 0) pmHour = 12;
            string amPm = hour >= 12 ? "PM" : "AM";

            return $"{pmHour}:00 {amPm}";
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

            var worldPawn = Find.WorldPawns?.AllPawnsAliveOrDead?.FirstOrDefault(x => x.ThingID == id);
            return worldPawn;
        }
    }
}
