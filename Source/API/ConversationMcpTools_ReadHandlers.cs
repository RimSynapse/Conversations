using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Newtonsoft.Json;

namespace RimSynapse.Conversations.API
{
    /// <summary>
    /// Read-only agent tool handlers (Conversations#10): pull a colonist's recent chat history
    /// and derived interest topics on demand, instead of loading full chats into prompt headers.
    /// No writes to the world component, no memory/thought mutation — safe to run under
    /// allowMutating:false.
    /// </summary>
    public static partial class ConversationMcpTools
    {
        private const int DefaultHistoryMessages = 20;

        /// <summary>
        /// get_chat_history — returns the named colonist's pawn-to-pawn messages, newest-first.
        /// Source is <see cref="SynapseConversationsWorldComponent.pawnConversations"/>; the flat
        /// storyteller-window <c>chatHistory</c> is Player/Storyteller only and carries no colonist
        /// speaker to filter on, so it is intentionally not included here.
        /// </summary>
        private static string GetChatHistoryHandler(string argumentsJson)
        {
            try
            {
                var args = JsonConvert.DeserializeAnonymousType(argumentsJson, new { pawnName = "", maxMessages = 0 });
                Pawn pawn = FindPawnByName(args.pawnName);
                if (pawn == null) return JsonConvert.SerializeObject(new { error = "Pawn not found." });

                var wc = Find.World?.GetComponent<SynapseConversationsWorldComponent>();
                if (wc == null) return JsonConvert.SerializeObject(new { error = "No conversations world component." });

                int max = args.maxMessages > 0 ? args.maxMessages : DefaultHistoryMessages;

                var collected = new List<SynapseConversationMessage>();
                foreach (var conv in wc.pawnConversations)
                {
                    if (conv?.messages == null) continue;
                    if (conv.pawnAId != pawn.ThingID && conv.pawnBId != pawn.ThingID) continue;
                    collected.AddRange(conv.messages);
                }

                var messages = collected
                    .OrderByDescending(m => m.gameTick)
                    .Take(max)
                    .Select(m => new
                    {
                        speaker = ResolveSpeakerLabel(m.sender),
                        text = m.message,
                        tick = m.gameTick
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new { pawn = pawn.LabelShort, count = messages.Count, messages });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = ex.Message });
            }
        }

        /// <summary>
        /// get_colonist_interests — derives conversational topics from the pawn's Minor/Major-passion
        /// skills plus its traits. A pawn with no passions and no traits returns an empty list.
        /// </summary>
        private static string GetColonistInterestsHandler(string argumentsJson)
        {
            try
            {
                var args = JsonConvert.DeserializeAnonymousType(argumentsJson, new { pawnName = "" });
                Pawn pawn = FindPawnByName(args.pawnName);
                if (pawn == null) return JsonConvert.SerializeObject(new { error = "Pawn not found." });

                var interests = new List<object>();

                if (pawn.skills?.skills != null)
                {
                    foreach (var sk in pawn.skills.skills)
                    {
                        if (sk == null || sk.TotallyDisabled) continue;
                        if (sk.passion == Passion.Minor || sk.passion == Passion.Major)
                        {
                            interests.Add(new
                            {
                                topic = sk.def?.skillLabel ?? sk.def?.defName ?? "unknown",
                                source = "passion:" + sk.passion
                            });
                        }
                    }
                }

                if (pawn.story?.traits?.allTraits != null)
                {
                    foreach (var tr in pawn.story.traits.allTraits)
                    {
                        if (tr?.def == null) continue;
                        interests.Add(new { topic = tr.LabelCap, source = "trait" });
                    }
                }

                return JsonConvert.SerializeObject(new { pawn = pawn.LabelShort, interests });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = ex.Message });
            }
        }

        /// <summary>Resolve a message sender's ThingID (as stored in PawnConversation messages) to a
        /// display label, falling back to the raw id if the pawn is no longer spawned.</summary>
        private static string ResolveSpeakerLabel(string thingId)
        {
            if (string.IsNullOrEmpty(thingId)) return "?";
            var p = SynapseConversationsWorldComponent.PawnFromId(thingId);
            return p?.LabelShort ?? thingId;
        }
    }
}
