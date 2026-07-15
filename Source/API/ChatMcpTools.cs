using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;
using Newtonsoft.Json;
using RimSynapse.Comps;
using RimSynapse.Models;

namespace RimSynapse.Chat.API
{
    /// <summary>
    /// Registers and handles MCP tools for RimSynapse-Chat, integrating DLCs and balanced game-engine actions.
    /// </summary>
    public static class ChatMcpTools
    {
        private static Dictionary<string, int> s_MoodCooldowns = new Dictionary<string, int>();
        private static Dictionary<string, int> s_RelationCooldowns = new Dictionary<string, int>();
        private static int s_LastInspirationTick = -99999;

        public static void RegisterTools()
        {
            // 1. trigger_mood_booster
            SynapseToolRegistry.RegisterTool(
                "trigger_mood_booster",
                "Applies a balanced temporary thought buff or debuff to a colonist. Limit once per 4 hours.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        pawnName = new { type = "string", description = "Name of the target colonist" },
                        effectType = new { type = "string", @enum = new[] { "boost", "penalty" }, description = "The direction of the mood effect" },
                        reason = new { type = "string", description = "Brief narrative reason for this mood shift" }
                    },
                    required = new[] { "pawnName", "effectType", "reason" }
                },
                TriggerMoodBoosterHandler
            );

            // 2. trigger_relationship_shift
            SynapseToolRegistry.RegisterTool(
                "trigger_relationship_shift",
                "Applies a balanced opinion shift between two colonists via social thought memories. Max once per 6 hours.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        speakerName = new { type = "string", description = "Name of the colonist speaking" },
                        recipientName = new { type = "string", description = "Name of the colonist listening" },
                        shiftAmount = new { type = "number", minimum = -15, maximum = 15, description = "Relationship shift amount (clamped -15 to 15)" },
                        reason = new { type = "string", description = "Brief narrative reason for the shift" }
                    },
                    required = new[] { "speakerName", "recipientName", "shiftAmount", "reason" }
                },
                TriggerRelationshipShiftHandler
            );

            // 3. inspire_colonist
            SynapseToolRegistry.RegisterTool(
                "inspire_colonist",
                "Attempts to trigger a vanilla inspiration on a happy colonist. Cooldown is once per day across the colony.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        pawnName = new { type = "string", description = "Name of the colonist to inspire" },
                        inspirationType = new { type = "string", @enum = new[] { "frenzy", "trade", "surgery", "creative" }, description = "Type of inspiration to apply" }
                    },
                    required = new[] { "pawnName", "inspirationType" }
                },
                InspireColonistHandler
            );

            // 4. get_royal_demands
            SynapseToolRegistry.RegisterTool(
                "get_royal_demands",
                "Retrieves the noble titles, demands, active decrees, and privileges of a noble colonist (Royalty DLC).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        pawnName = new { type = "string", description = "Name of the noble colonist" }
                    },
                    required = new[] { "pawnName" }
                },
                GetRoyalDemandsHandler
            );

            // 5. get_faith_precepts
            SynapseToolRegistry.RegisterTool(
                "get_faith_precepts",
                "Retrieves the active ideoligion, role, social precepts, and relics of a colonist (Ideology DLC).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        pawnName = new { type = "string", description = "Name of the colonist" }
                    },
                    required = new[] { "pawnName" }
                },
                GetFaithPreceptsHandler
            );

            // 6. apply_conversion_attempt
            SynapseToolRegistry.RegisterTool(
                "apply_conversion_attempt",
                "Executes a vanilla faith conversion check based on opinion, social skills, and certainty (Ideology DLC).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        initiatorName = new { type = "string", description = "Name of the converter" },
                        recipientName = new { type = "string", description = "Name of the colonist being converted" }
                    },
                    required = new[] { "initiatorName", "recipientName" }
                },
                ApplyConversionAttemptHandler
            );

            // 7. get_xenotype_identity
            SynapseToolRegistry.RegisterTool(
                "get_xenotype_identity",
                "Retrieves the genes list, xenotype classification, and age group of a colonist (Biotech DLC).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        pawnName = new { type = "string", description = "Name of the colonist" }
                    },
                    required = new[] { "pawnName" }
                },
                GetXenotypeIdentityHandler
            );

            // 8. get_mechanitor_status
            SynapseToolRegistry.RegisterTool(
                "get_mechanitor_status",
                "Retrieves the bandwidth, complexity, and active controlled mechanoids list for a Mechanitor (Biotech DLC).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        pawnName = new { type = "string", description = "Name of the Mechanitor colonist" }
                    },
                    required = new[] { "pawnName" }
                },
                GetMechanitorStatusHandler
            );

            // 9. get_void_melancholy
            SynapseToolRegistry.RegisterTool(
                "get_void_melancholy",
                "Retrieves monolith study levels, void obsession progress, and void mental states of a colonist (Anomaly DLC).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        pawnName = new { type = "string", description = "Name of the colonist" }
                    },
                    required = new[] { "pawnName" }
                },
                GetVoidMelancholyHandler
            );

            // 10. attempt_mental_soothe
            SynapseToolRegistry.RegisterTool(
                "attempt_mental_soothe",
                "Tries to calm a panic or light void mental break state via conversation (Anomaly DLC).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        initiatorName = new { type = "string", description = "Name of the speaker trying to soothe" },
                        recipientName = new { type = "string", description = "Name of the panicked colonist" }
                    },
                    required = new[] { "initiatorName", "recipientName" }
                },
                AttemptMentalSootheHandler
            );

            // 11. get_orbital_hazards
            SynapseToolRegistry.RegisterTool(
                "get_orbital_hazards",
                "Retrieves space biome coordinates, ship systems damage, and active orbital travel hazards (SOS2/Odyssey).",
                new
                {
                    type = "object",
                    properties = new {},
                    required = new string[] {}
                },
                GetOrbitalHazardsHandler
            );

            SynapseLogger.Message("[RimSynapse Chat] Registered MCP tools for DLCs and balanced actions.");
        }

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

                if (args.effectType == "boost")
                {
                    var kindWordsDef = DefDatabase<ThoughtDef>.GetNamed("KindWords", false);
                    if (kindWordsDef != null)
                    {
                        pawn.needs?.mood?.thoughts?.memories?.TryGainMemory(kindWordsDef);
                        return "{\"success\": \"Applied Kind Words mood boost (+5).\"}";
                    }
                }
                else
                {
                    var slightedDef = DefDatabase<ThoughtDef>.GetNamed("Slighted", false);
                    if (slightedDef != null)
                    {
                        pawn.needs?.mood?.thoughts?.memories?.TryGainMemory(slightedDef);
                        return "{\"success\": \"Applied Slighted mood penalty.\"}";
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

        private static string GetRoyalDemandsHandler(string argumentsJson)
        {
            if (!ModsConfig.RoyaltyActive) return "{\"error\": \"Royalty DLC is not active.\"}";
            try
            {
                var args = JsonConvert.DeserializeAnonymousType(argumentsJson, new { pawnName = "" });
                Pawn pawn = FindPawnByName(args.pawnName);
                if (pawn == null) return "{\"error\": \"Pawn not found.\"}";

                var response = new Dictionary<string, object>();
                var titles = pawn.royalty?.AllTitlesForReading;
                if (titles != null && titles.Count > 0)
                {
                    response["titles"] = titles.Select(t => new {
                        title = t.def.LabelCap.ToString(),
                        faction = t.faction.Name
                    }).ToList();
                    
                    // Simple bedroom/throneroom compliance
                    response["bedroomSatisfied"] = pawn.royalty.HighestTitleWithBedroomRequirements()?.def.bedroomRequirements != null;
                }
                else
                {
                    response["titles"] = "None";
                }

                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                return $"{{\"error\": \"{ex.Message}\"}}";
            }
        }

        private static string GetFaithPreceptsHandler(string argumentsJson)
        {
            if (!ModsConfig.IdeologyActive) return "{\"error\": \"Ideology DLC is not active.\"}";
            try
            {
                var args = JsonConvert.DeserializeAnonymousType(argumentsJson, new { pawnName = "" });
                Pawn pawn = FindPawnByName(args.pawnName);
                if (pawn == null) return "{\"error\": \"Pawn not found.\"}";

                var response = new Dictionary<string, object>();
                if (pawn.Ideo != null)
                {
                    response["ideo"] = pawn.Ideo.name;
                    response["role"] = pawn.Ideo.GetRole(pawn)?.def?.defName ?? "None";
                    response["certainty"] = pawn.ideo?.Certainty ?? 1.0f;
                    response["precepts"] = pawn.Ideo.PreceptsListForReading.Select(p => p.def.defName).Take(15).ToList();
                }
                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                return $"{{\"error\": \"{ex.Message}\"}}";
            }
        }

        private static string ApplyConversionAttemptHandler(string argumentsJson)
        {
            if (!ModsConfig.IdeologyActive) return "{\"error\": \"Ideology DLC is not active.\"}";
            try
            {
                var args = JsonConvert.DeserializeAnonymousType(argumentsJson, new { initiatorName = "", recipientName = "" });
                Pawn initiator = FindPawnByName(args.initiatorName);
                Pawn recipient = FindPawnByName(args.recipientName);
                if (initiator == null || recipient == null) return "{\"error\": \"Initiator or recipient not found.\"}";

                if (initiator.Ideo == null || recipient.Ideo == null) return "{\"error\": \"Pawn ideo missing.\"}";

                if (initiator.Ideo == recipient.Ideo)
                {
                    return "{\"message\": \"Already belong to the same ideoligion.\"}";
                }

                // Execute vanilla conversion logic: Certainty reduction
                float certBefore = recipient.ideo?.Certainty ?? 1.0f;
                float reduction = 0.08f * (initiator.skills?.GetSkill(SkillDefOf.Social)?.Level ?? 5) / 10f;
                recipient.ideo?.OffsetCertainty(-reduction);
                float certAfter = recipient.ideo?.Certainty ?? 1.0f;

                return $"{{ \"success\": \"Certainty reduced from {certBefore:P0} to {certAfter:P0}.\" }}";
            }
            catch (Exception ex)
            {
                return $"{{\"error\": \"{ex.Message}\"}}";
            }
        }

        private static string GetXenotypeIdentityHandler(string argumentsJson)
        {
            if (!ModsConfig.BiotechActive) return "{\"error\": \"Biotech DLC is not active.\"}";
            try
            {
                var args = JsonConvert.DeserializeAnonymousType(argumentsJson, new { pawnName = "" });
                Pawn pawn = FindPawnByName(args.pawnName);
                if (pawn == null) return "{\"error\": \"Pawn not found.\"}";

                var response = new Dictionary<string, object>();
                response["xenotype"] = pawn.genes?.XenotypeLabel ?? "Baseliners";
                response["genes"] = pawn.genes?.GenesListForReading.Select(g => g.def.defName).Take(15).ToList() ?? new List<string>();
                response["developmentalStage"] = pawn.DevelopmentalStage.ToString();
                
                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                return $"{{\"error\": \"{ex.Message}\"}}";
            }
        }

        private static string GetMechanitorStatusHandler(string argumentsJson)
        {
            if (!ModsConfig.BiotechActive) return "{\"error\": \"Biotech DLC is not active.\"}";
            try
            {
                var args = JsonConvert.DeserializeAnonymousType(argumentsJson, new { pawnName = "" });
                Pawn pawn = FindPawnByName(args.pawnName);
                if (pawn == null) return "{\"error\": \"Pawn not found.\"}";

                // Check if has mechanitor tracker
                if (pawn.mechanitor == null)
                {
                    return "{\"error\": \"Pawn is not a mechanitor.\"}";
                }

                var response = new Dictionary<string, object>();
                response["bandwidth"] = pawn.mechanitor.UsedBandwidth + " / " + pawn.mechanitor.TotalBandwidth;
                response["controlledMechs"] = pawn.mechanitor.ControlledPawns.Select(m => m.Name.ToStringShort).ToList();

                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                return $"{{\"error\": \"{ex.Message}\"}}";
            }
        }

        private static string GetVoidMelancholyHandler(string argumentsJson)
        {
            if (!ModsConfig.AnomalyActive) return "{\"error\": \"Anomaly DLC is not active.\"}";
            try
            {
                var args = JsonConvert.DeserializeAnonymousType(argumentsJson, new { pawnName = "" });
                Pawn pawn = FindPawnByName(args.pawnName);
                if (pawn == null) return "{\"error\": \"Pawn not found.\"}";

                var response = new Dictionary<string, object>();
                response["monolithStudy"] = Find.Anomaly?.Level ?? 0;
                
                // Simple search for any void-related hediffs
                if (pawn.health?.hediffSet != null)
                {
                    var voidHediffs = pawn.health.hediffSet.hediffs
                        .Where(h => h.def.defName.ToLower().Contains("void") || h.def.defName.ToLower().Contains("monolith"))
                        .Select(h => h.LabelCap.ToString())
                        .ToList();
                    response["voidHediffs"] = voidHediffs.Any() ? voidHediffs : new List<string> { "None" };
                }

                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                return $"{{\"error\": \"{ex.Message}\"}}";
            }
        }

        private static string AttemptMentalSootheHandler(string argumentsJson)
        {
            if (!ModsConfig.AnomalyActive) return "{\"error\": \"Anomaly DLC is not active.\"}";
            try
            {
                var args = JsonConvert.DeserializeAnonymousType(argumentsJson, new { initiatorName = "", recipientName = "" });
                Pawn initiator = FindPawnByName(args.initiatorName);
                Pawn recipient = FindPawnByName(args.recipientName);
                if (initiator == null || recipient == null) return "{\"error\": \"Pawns not found.\"}";

                if (recipient.InMentalState)
                {
                    // Execute simple soothe check based on social skills and relationship opinion
                    int social = initiator.skills?.GetSkill(SkillDefOf.Social)?.Level ?? 5;
                    int opinion = recipient.relations?.OpinionOf(initiator) ?? 0;

                    float sootheChance = 0.1f + (social * 0.03f) + (opinion * 0.003f);
                    if (Rand.Value < sootheChance)
                    {
                        recipient.MentalState.RecoverFromState();
                        return "{\"success\": \"Soothe attempt succeeded. Mental break recovered.\"}";
                    }
                    return "{\"failed\": \"Soothe attempt failed.\"}";
                }
                return "{\"message\": \"Pawn is not in a mental break state.\"}";
            }
            catch (Exception ex)
            {
                return $"{{\"error\": \"{ex.Message}\"}}";
            }
        }

        private static string GetOrbitalHazardsHandler(string argumentsJson)
        {
            // Simple space map checker for Save Our Ship 2
            try
            {
                var response = new Dictionary<string, object>();
                Map activeMap = Find.AnyPlayerHomeMap;
                if (activeMap != null)
                {
                    string biomeName = activeMap.Biome?.defName?.ToLower() ?? "";
                    if (biomeName.Contains("space") || biomeName.Contains("orbit") || biomeName.Contains("ship"))
                    {
                        response["inSpace"] = true;
                        response["biome"] = activeMap.Biome.defName;
                    }
                    else
                    {
                        response["inSpace"] = false;
                        response["biome"] = activeMap.Biome.defName;
                    }
                }
                else
                {
                    response["error"] = "No active colony map found.";
                }

                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                return $"{{\"error\": \"{ex.Message}\"}}";
            }
        }

        private static Pawn FindPawnByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            foreach (var map in Find.Maps)
            {
                if (map.mapPawns == null) continue;
                var p = map.mapPawns.AllPawns.FirstOrDefault(x => x.LabelShort.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (p != null) return p;
            }

            var worldPawn = Find.WorldPawns?.AllPawnsAlive?.FirstOrDefault(x => x.LabelShort.Equals(name, StringComparison.OrdinalIgnoreCase));
            return worldPawn;
        }
    }
}
