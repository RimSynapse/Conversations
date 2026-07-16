using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Newtonsoft.Json;

namespace RimSynapse.Chat.API
{
    /// <summary>
    /// DLC-specific tool handlers for ChatMcpTools: Royalty, Ideology, Biotech, Anomaly, and SOS2/Odyssey.
    /// </summary>
    public static partial class ChatMcpTools
    {
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
    }
}
