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
    /// A session feed of one pawn's talk. Instead of a persistent per-contact thread split by day, the
    /// pawn's conversation lines and the things they overheard are sliced into discrete SESSIONS — a burst
    /// of talk bounded by a quiet gap (<see cref="SessionGapTicks"/>). The left pane lists sessions newest
    /// first (type · time · participant avatars); the right pane is one continuous stream of every session
    /// (each a block with a divider header), or just the selected session when one is picked. Conversations
    /// and overheard live in the same stream but are always separate blocks — a conversation and an
    /// overheard at the same moment are two threads, never interleaved line by line.
    /// </summary>
    public class Dialog_PawnConversationHistory : Window
    {
        /// <summary>A quiet gap of more than this (game ticks; 2500 = 1 in-game hour, so ~30 min) ends a
        /// session — the next line after the gap begins a new one.</summary>
        private const int SessionGapTicks = 1250;

        private struct Line { public string senderId; public string text; public int tick; }

        private class Session
        {
            public bool overheard;
            public int startTick;
            public List<string> participantIds = new List<string>();
            public List<Line> lines = new List<Line>();
        }

        private Pawn pawn;
        private Session selected;                 // null = show the whole stream
        private Vector2 leftScroll = Vector2.zero;
        private Vector2 rightScroll = Vector2.zero;

        public override Vector2 InitialSize => new Vector2(760f, 560f);

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

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), $"Chat History: {pawn.Name.ToStringShort}");
            Text.Font = GameFont.Small;

            var sessions = BuildSessions(worldComp);

            float topY = 44f;
            float paneHeight = inRect.height - topY - 8f;
            const float leftPaneWidth = 210f;
            const float margin = 12f;

            Rect leftRect = new Rect(0f, topY, leftPaneWidth, paneHeight);
            Rect rightRect = new Rect(leftPaneWidth + margin, topY, inRect.width - leftPaneWidth - margin, paneHeight);
            Widgets.DrawLineVertical(leftPaneWidth + margin / 2f, topY, paneHeight);

            DrawSessionList(leftRect, sessions);
            DrawStream(rightRect, sessions);
        }

        // ── Session assembly ────────────────────────────────────────────────────────────────────

        /// <summary>Slice this pawn's conversation records and overheard memories into gap-bounded sessions,
        /// merged and sorted newest first. Two different records (participant sets) never share a session even
        /// when their times overlap, and overheard is always its own session type — so simultaneous talk and
        /// eavesdropping stay separate threads.</summary>
        private List<Session> BuildSessions(SynapseConversationsWorldComponent worldComp)
        {
            var result = new List<Session>();

            // Conversation sessions — segment each record the pawn is in by the quiet gap.
            foreach (var conv in worldComp.pawnConversations)
            {
                if (conv == null || !conv.Involves(pawn.ThingID) || conv.messages == null || conv.messages.Count == 0)
                    continue;
                var ordered = conv.messages.Where(m => m != null).OrderBy(m => m.gameTick).ToList();
                Session cur = null;
                int lastTick = 0;
                foreach (var m in ordered)
                {
                    if (cur == null || m.gameTick - lastTick > SessionGapTicks)
                    {
                        cur = new Session { overheard = false, startTick = m.gameTick, participantIds = new List<string>(conv.participantIds) };
                        result.Add(cur);
                    }
                    cur.lines.Add(new Line { senderId = m.sender, text = m.message, tick = m.gameTick });
                    lastTick = m.gameTick;
                }
            }

            // Overheard sessions — from Core memories tagged "overheard" (tags[1] = speaker, tags[2] = target).
            var core = pawn.TryGetComp<SynapseCorePawnComp>();
            if (core?.memories != null)
            {
                var heard = core.memories
                    .Where(m => m != null && m.tags != null && m.tags.Contains("overheard") && !string.IsNullOrEmpty(m.summary))
                    .OrderBy(m => m.gameTick).ToList();
                Session cur = null;
                int lastTick = 0;
                foreach (var m in heard)
                {
                    string speaker = m.tags.Count > 1 ? m.tags[1] : null;
                    string target = m.tags.Count > 2 ? m.tags[2] : null;
                    if (cur == null || m.gameTick - lastTick > SessionGapTicks)
                    {
                        cur = new Session { overheard = true, startTick = m.gameTick, participantIds = new List<string>() };
                        result.Add(cur);
                    }
                    if (!string.IsNullOrEmpty(speaker) && !cur.participantIds.Contains(speaker)) cur.participantIds.Add(speaker);
                    if (!string.IsNullOrEmpty(target) && !cur.participantIds.Contains(target)) cur.participantIds.Add(target);
                    cur.lines.Add(new Line { senderId = speaker, text = ExtractOverheardReply(m.summary), tick = m.gameTick });
                    lastTick = m.gameTick;
                }
            }

            result.Sort((a, b) => b.startTick.CompareTo(a.startTick)); // newest first
            if (selected != null && !result.Contains(selected)) selected = null;
            return result;
        }

        // ── Left pane: the session list ─────────────────────────────────────────────────────────

        private void DrawSessionList(Rect rect, List<Session> sessions)
        {
            const float rowH = 52f;
            var view = new Rect(0f, 0f, rect.width - 16f, sessions.Count * rowH + 2f);
            Widgets.BeginScrollView(rect, ref leftScroll, view);
            float y = 0f;
            foreach (var s in sessions)
            {
                var row = new Rect(0f, y, view.width, rowH - 4f);
                if (selected == s) Widgets.DrawHighlightSelected(row);
                else Widgets.DrawHighlightIfMouseover(row);
                if (Widgets.ButtonInvisible(row, true))
                {
                    selected = (selected == s) ? null : s; // toggle: click the selected row again to see all
                    rightScroll = Vector2.zero;
                }

                var origFont = Text.Font;
                Text.Font = GameFont.Tiny;
                GUI.color = s.overheard ? new Color(0.75f, 0.72f, 0.55f) : new Color(0.6f, 0.78f, 0.95f);
                Widgets.Label(new Rect(row.x + 4f, row.y + 2f, row.width - 8f, 16f),
                    $"{(s.overheard ? "Overheard" : "Conversation")} · {FormatTimeOnly(s.startTick, pawn)}");
                GUI.color = Color.white;

                // Participant avatars along the second line.
                float ax = row.x + 4f;
                foreach (var pid in s.participantIds)
                {
                    if (ax + 24f > row.xMax) break;
                    Pawn p = pid == pawn.ThingID ? pawn : FindPawnById(pid);
                    if (p != null) Widgets.ThingIcon(new Rect(ax, row.y + 20f, 22f, 22f), p);
                    ax += 24f;
                }
                Text.Font = origFont;
                y += rowH;
            }
            Widgets.EndScrollView();

            if (sessions.Count == 0)
            {
                var oa = Text.Anchor; Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(rect, "No conversations yet.");
                Text.Anchor = oa;
            }
        }

        // ── Right pane: the stream ──────────────────────────────────────────────────────────────

        private void DrawStream(Rect rect, List<Session> sessions)
        {
            var shown = selected != null ? new List<Session> { selected } : sessions;
            if (shown.Count == 0)
            {
                var oa = Text.Anchor; Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(rect, "Select a conversation, or let some happen.");
                Text.Anchor = oa;
                return;
            }

            float width = rect.width - 16f;
            float total = 6f;
            foreach (var s in shown) total += SessionHeight(s, width);

            var view = new Rect(0f, 0f, width, total);
            Widgets.BeginScrollView(rect, ref rightScroll, view);
            float y = 6f;
            foreach (var s in shown) y = DrawSession(s, width, y);
            Widgets.EndScrollView();
        }

        private float SessionHeight(Session s, float width)
        {
            var origFont = Text.Font;
            Text.Font = GameFont.Small;
            float h = 46f; // divider header
            float textWidth = width - 62f;
            foreach (var l in s.lines)
            {
                float th = Text.CalcHeight(l.text ?? "", textWidth);
                h += Mathf.Max(32f, 22f + th) + 12f;
            }
            Text.Font = origFont;
            return h + 10f;
        }

        private float DrawSession(Session s, float width, float y)
        {
            // Divider header: type · date · time, a rule line, and the participant avatars.
            var origFont = Text.Font;
            var origColor = GUI.color;
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.55f, 0.55f, 0.55f, 0.8f);
            Widgets.DrawLineHorizontal(4f, y + 8f, width - 8f);
            string head = $"  {(s.overheard ? "Overheard" : "Conversation")} · {FormatDateOnly(s.startTick, pawn)} {FormatTimeOnly(s.startTick, pawn)}  ";
            float headW = Text.CalcSize(head).x;
            GUI.color = s.overheard ? new Color(0.78f, 0.74f, 0.55f) : new Color(0.6f, 0.78f, 0.95f);
            var headRect = new Rect(width / 2f - headW / 2f, y, headW, 16f);
            Widgets.DrawBoxSolid(headRect, new Color(0.12f, 0.12f, 0.12f));
            Widgets.Label(headRect, head);

            // Participant avatars on the header's right.
            float ax = width - 8f - s.participantIds.Count * 22f;
            foreach (var pid in s.participantIds)
            {
                Pawn pp = pid == pawn.ThingID ? pawn : FindPawnById(pid);
                if (pp != null && ax > headRect.xMax) Widgets.ThingIcon(new Rect(ax, y - 2f, 20f, 20f), pp);
                ax += 22f;
            }
            GUI.color = origColor;
            Text.Font = origFont;
            y += 26f;

            foreach (var l in s.lines)
            {
                bool isSelf = l.senderId == pawn.ThingID;
                Pawn sp = isSelf ? pawn : FindPawnById(l.senderId);
                if (sp != null) Widgets.ThingIcon(new Rect(10f, y, 32f, 32f), sp);

                string name = sp != null ? sp.Name.ToStringShort : (l.senderId ?? "Someone");
                float nameW = Text.CalcSize(name).x;
                Text.Font = GameFont.Small;
                GUI.color = SpeakerColor(l.senderId, isSelf);
                Widgets.Label(new Rect(52f, y, nameW, 20f), name);

                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.55f, 0.55f, 0.55f, 0.85f);
                Widgets.Label(new Rect(52f + nameW + 8f, y + 2f, width - 62f - nameW, 18f), FormatTimeOnly(l.tick, pawn));

                float textWidth = width - 62f;
                Text.Font = GameFont.Small;
                float th = Text.CalcHeight(l.text ?? "", textWidth);
                GUI.color = new Color(0.92f, 0.92f, 0.95f);
                Widgets.Label(new Rect(52f, y + 22f, textWidth, th), l.text ?? "");
                GUI.color = origColor;

                y += Mathf.Max(32f, 22f + th) + 12f;
            }
            return y + 10f;
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────

        // Distinct colour per speaker so a multiway session is legible: the viewing pawn is blue, every other
        // participant a stable colour by ThingID hash, so no two speakers blur together.
        private static readonly Color SelfColor = new Color(0.35f, 0.65f, 1.0f);
        private static readonly Color UnknownColor = new Color(0.85f, 0.85f, 0.85f);
        private static readonly Color[] OtherPalette =
        {
            new Color(0.55f, 0.85f, 0.55f), new Color(0.95f, 0.78f, 0.45f), new Color(0.88f, 0.60f, 0.88f),
            new Color(0.55f, 0.85f, 0.85f), new Color(0.95f, 0.62f, 0.55f), new Color(0.75f, 0.75f, 0.98f),
        };

        private static Color SpeakerColor(string senderId, bool isSelf)
        {
            if (isSelf) return SelfColor;
            if (string.IsNullOrEmpty(senderId)) return UnknownColor;
            return OtherPalette[System.Math.Abs(senderId.GetHashCode()) % OtherPalette.Length];
        }

        /// <summary>Pull the spoken line out of an "overheard" memory summary: prefer the LAST quoted run,
        /// tolerate an unbalanced/absent quote by returning the whole summary rather than slicing garbage.</summary>
        private static string ExtractOverheardReply(string summary)
        {
            if (string.IsNullOrEmpty(summary)) return summary ?? "";
            int close = summary.LastIndexOf('"');
            if (close <= 0) return summary.Trim();
            int open = summary.LastIndexOf('"', close - 1);
            if (open < 0) return summary.Trim();
            string inner = summary.Substring(open + 1, close - open - 1).Trim();
            return inner.Length > 0 ? inner : summary.Trim();
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
            return Find.WorldPawns?.AllPawnsAliveOrDead?.FirstOrDefault(x => x.ThingID == id);
        }

        private static string FormatDateOnly(int gameTick, Pawn pawn)
        {
            float longitude = 0f;
            if (pawn != null && pawn.Tile >= 0 && Find.WorldGrid != null)
                longitude = Find.WorldGrid.LongLatOf(pawn.Tile).x;
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
                longitude = Find.WorldGrid.LongLatOf(pawn.Tile).x;
            long absTick = RimSynapse.Utils.SynapseDateHelper.GameTickToAbsTick(gameTick);

            int hour = GenDate.HourOfDay(absTick, longitude);
            int pmHour = hour % 12;
            if (pmHour == 0) pmHour = 12;
            string amPm = hour >= 12 ? "PM" : "AM";

            long localTicks = absTick + (long)(longitude * (60000f / 360f));
            long inHour = localTicks % 2500L;
            if (inHour < 0) inHour += 2500L;
            int minute = Mathf.Clamp((int)(inHour * 60L / 2500L), 0, 59);

            return $"{pmHour}:{minute:00} {amPm}";
        }
    }
}
