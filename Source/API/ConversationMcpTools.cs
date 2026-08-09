using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Newtonsoft.Json;
using RimSynapse.Comps;

namespace RimSynapse.Conversations.API
{
    /// <summary>
    /// Registers and handles MCP tools for RimSynapse-Chat, integrating DLCs and balanced game-engine actions.
    /// Tool handlers are split across partial files:
    ///   - ConversationMcpTools_UniverseHandlers.cs (mood, relationship, inspiration)
    ///   - ConversationMcpTools_DlcHandlers.cs (Royalty, Ideology, Biotech, Anomaly, SOS2)
    /// </summary>
    public static partial class ConversationMcpTools
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

            // 12. get_chat_history (read-only)
            SynapseToolRegistry.RegisterTool(
                "get_chat_history",
                "Returns a colonist's recent pawn-to-pawn conversation messages, newest-first. Read-only.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        pawnName = new { type = "string", description = "Name of the colonist whose chat history to retrieve" },
                        maxMessages = new { type = "number", description = "Optional cap on messages returned (default 20, newest-first)" }
                    },
                    required = new[] { "pawnName" }
                },
                GetChatHistoryHandler
            );

            // 13. get_colonist_interests (read-only)
            SynapseToolRegistry.RegisterTool(
                "get_colonist_interests",
                "Returns a colonist's conversational interests derived from Minor/Major-passion skills and traits. Read-only.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        pawnName = new { type = "string", description = "Name of the colonist whose interests to retrieve" }
                    },
                    required = new[] { "pawnName" }
                },
                GetColonistInterestsHandler
            );

            SynapseLogger.Message("[RimSynapse Chat] Registered MCP tools for DLCs and balanced actions.");
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
