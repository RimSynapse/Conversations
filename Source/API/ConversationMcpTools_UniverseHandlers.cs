using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using UnityEngine;
using Newtonsoft.Json;
using RimSynapse.Comps;

namespace RimSynapse.Conversations.API
{
    /// <summary>
    /// Universe action handlers for ChatMcpTools: mood, relationship, and inspiration tools.
    /// </summary>
    public static partial class ConversationMcpTools
    {
        private static string TriggerMoodBoosterHandler(string argumentsJson)
        {
            try
            {
                var args = JsonConvert.DeserializeAnonymousType(argumentsJson, new { pawnName = "", effectType = "", reason = "" });
                Pawn pawn = FindPawnByName(args.pawnName);
                if (pawn == null) return "{\"error\": \"Pawn not found.\"}";

                int currentTick = Find.TickManager.TicksGame;
                string key = pawn.ThingID;
                if (s_MoodCooldowns.TryGetValue(key, out int lastTick) && (currentTick - lastTick < 10000))
                {
                    return "{\"error\": \"Mood adjuster is on cooldown for this colonist.\"}";
                }
                s_MoodCooldowns[key] = currentTick;

                // Use the *Mood memory thoughts (Thought_Memory), not the social KindWords/Slighted
                // (Thought_MemorySocial): a mood booster targets one pawn, and a social thought added
                // without an otherPawn silently fails to stick. KindWordsMood/InsultedMood apply cleanly.
                if (args.effectType == "boost")
                {
                    var boostDef = DefDatabase<ThoughtDef>.GetNamed("KindWordsMood", false);
                    if (boostDef != null)
                    {
                        pawn.needs?.mood?.thoughts?.memories?.TryGainMemory(boostDef);
                        return "{\"success\": \"Applied Kind Words mood boost.\"}";
                    }
                }
                else
                {
                    var penaltyDef = DefDatabase<ThoughtDef>.GetNamed("InsultedMood", false);
                    if (penaltyDef != null)
                    {
                        pawn.needs?.mood?.thoughts?.memories?.TryGainMemory(penaltyDef);
                        return "{\"success\": \"Applied Insulted mood penalty.\"}";
                    }
                }
                return "{\"error\": \"Failed to apply mood effect.\"}";
            }
            catch (Exception ex)
            {
                return $"{{\"error\": \"{ex.Message}\"}}";
            }
        }

        private static string TriggerRelationshipShiftHandler(string argumentsJson)
        {
            try
            {
                var args = JsonConvert.DeserializeAnonymousType(argumentsJson, new { speakerName = "", recipientName = "", shiftAmount = 0f, reason = "" });
                Pawn speaker = FindPawnByName(args.speakerName);
                Pawn recipient = FindPawnByName(args.recipientName);
                if (speaker == null || recipient == null) return "{\"error\": \"One or both pawns not found.\"}";

                int currentTick = Find.TickManager.TicksGame;
                string key = $"{speaker.ThingID}_{recipient.ThingID}";
                string keyAlt = $"{recipient.ThingID}_{speaker.ThingID}";
                if ((s_RelationCooldowns.TryGetValue(key, out int lastTick) && (currentTick - lastTick < 15000)) ||
                    (s_RelationCooldowns.TryGetValue(keyAlt, out int lastTickAlt) && (currentTick - lastTickAlt < 15000)))
                {
                    return "{\"error\": \"Relationship shift is on cooldown for this pair.\"}";
                }
                s_RelationCooldowns[key] = currentTick;
                s_RelationCooldowns[keyAlt] = currentTick;

                float shift = Mathf.Clamp(args.shiftAmount, -15f, 15f);
                if (shift > 0)
                {
                    var thought = DefDatabase<ThoughtDef>.GetNamed(shift > 7 ? "RapportBuilt" : "Chitchat", false);
                    if (thought != null)
                    {
                        speaker.needs?.mood?.thoughts?.memories?.TryGainMemory(thought, recipient);
                        return $"{{\"success\": \"Added positive social thought from {speaker.LabelShort} to {recipient.LabelShort}.\"}}";
                    }
                }
                else if (shift < 0)
                {
                    var thought = DefDatabase<ThoughtDef>.GetNamed(shift < -7 ? "Insulted" : "Slighted", false);
                    if (thought != null)
                    {
                        speaker.needs?.mood?.thoughts?.memories?.TryGainMemory(thought, recipient);
                        return $"{{\"success\": \"Added negative social thought from {speaker.LabelShort} to {recipient.LabelShort}.\"}}";
                    }
                }
                return "{\"error\": \"No shift applied.\"}";
            }
            catch (Exception ex)
            {
                return $"{{\"error\": \"{ex.Message}\"}}";
            }
        }

        private static string InspireColonistHandler(string argumentsJson)
        {
            try
            {
                var args = JsonConvert.DeserializeAnonymousType(argumentsJson, new { pawnName = "", inspirationType = "" });
                Pawn pawn = FindPawnByName(args.pawnName);
                if (pawn == null) return "{\"error\": \"Pawn not found.\"}";

                // Balance check: Must be in decent mood
                if (pawn.needs?.mood == null || pawn.needs.mood.CurLevel < pawn.mindState.mentalBreaker.BreakThresholdMajor)
                {
                    return "{\"error\": \"Pawn's mood is too low to receive an inspiration.\"}";
                }

                // Balanced Cooldown check
                int currentTick = Find.TickManager.TicksGame;
                if (currentTick - s_LastInspirationTick < 60000)
                {
                    return "{\"error\": \"Inspiration is on colony-wide cooldown today.\"}";
                }
                s_LastInspirationTick = currentTick;

                InspirationDef def = null;
                if (args.inspirationType == "frenzy") def = DefDatabase<InspirationDef>.GetNamed("GoFrenzy", false);
                else if (args.inspirationType == "trade") def = DefDatabase<InspirationDef>.GetNamed("Inspired_Trade", false);
                else if (args.inspirationType == "surgery") def = DefDatabase<InspirationDef>.GetNamed("Inspired_Surgery", false);
                else if (args.inspirationType == "creative") def = DefDatabase<InspirationDef>.GetNamed("Inspired_Creativity", false);

                if (def != null && pawn.mindState?.inspirationHandler != null)
                {
                    if (pawn.mindState.inspirationHandler.TryStartInspiration(def))
                    {
                        return $"{{ \"success\": \"Successfully triggered {def.LabelCap} on {pawn.LabelShort}.\" }}";
                    }
                }
                return "{\"error\": \"Failed to trigger inspiration.\"}";
            }
            catch (Exception ex)
            {
                return $"{{\"error\": \"{ex.Message}\"}}";
            }
        }
    }
}
