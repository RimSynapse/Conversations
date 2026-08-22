using System.Linq;
using Verse;
using Verse.AI;
using LudeonTK;
using RimWorld;
using RimSynapse.Conversations.Warden;

namespace RimSynapse.Conversations.UI
{
    /// <summary>
    /// Dev/headless validation for warden conversations (Conversations#42). Click a prisoner (or slave):
    /// each action forces that mode's exchange regardless of job state or cooldown and dumps the resolved
    /// prisoner context block plus the generated lines to the log, so it is verifiable the dialogue
    /// reflects THIS prisoner's resistance, needs and beliefs. Grouped under the shared "RimSynapse" menu;
    /// ToolMapForPawns so each is reachable headlessly via run_debug_action (single-Pawn signature).
    /// </summary>
    public static class DebugActions_Warden
    {
        [DebugAction("RimSynapse", "Warden: Force recruitment (Tool)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ForceRecruitment(Pawn p) => Force(p, WardenMode.Recruitment, requiresIdeology: false);

        [DebugAction("RimSynapse", "Warden: Force conversion (Tool)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ForceConversion(Pawn p) => Force(p, WardenMode.Conversion, requiresIdeology: true);

        [DebugAction("RimSynapse", "Warden: Force enslavement (Tool)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ForceEnslavement(Pawn p) => Force(p, WardenMode.Enslavement, requiresIdeology: true);

        [DebugAction("RimSynapse", "Warden: Force suppression (Tool)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ForceSuppression(Pawn p) => Force(p, WardenMode.Suppression, requiresIdeology: true);

        /// <summary>Validation helper (not a feature): spawn a deliberately poorly-kept prisoner — resistance,
        /// hunger, and stripped apparel — next to a colonist, so the warden debug actions have a target whose
        /// dialogue should visibly raise those conditions. Logs the prisoner's name for the force actions.</summary>
        [DebugAction("RimSynapse", "Warden: Spawn test prisoner (Log)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void SpawnTestPrisoner()
        {
            var map = Find.CurrentMap;
            if (map == null) return;

            Pawn anchor = map.mapPawns.FreeColonists.FirstOrDefault();
            IntVec3 near = anchor?.Position ?? map.Center;

            Faction fac = Find.FactionManager.AllFactions
                .FirstOrDefault(f => !f.IsPlayer && f.def.humanlikeFaction && !f.def.hidden && !f.temporary)
                ?? Faction.OfAncients;

            Pawn prisoner = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                PawnKindDefOf.Villager, fac, PawnGenerationContext.NonPlayer, -1,
                forceGenerateNewPawn: true, allowDowned: false));

            IntVec3 cell = CellFinder.RandomClosewalkCellNear(near, map, 4);
            GenSpawn.Spawn(prisoner, cell, map);
            prisoner.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
            prisoner.guest.resistance = 18f;
            if (ModsConfig.IdeologyActive) prisoner.guest.will = 4f;

            // Make the neglect concrete so recruitment dialogue has something to seize on.
            prisoner.apparel?.DestroyAll();
            if (prisoner.needs?.food != null) prisoner.needs.food.CurLevel = prisoner.needs.food.MaxLevel * 0.15f;

            RimSynapse.SynapseLogger.Info("conversations",
                $"[RimSynapse-Warden] Spawned test prisoner {prisoner.LabelShort} ({prisoner.Faction?.Name}) at {cell} — resistance 18, hungry, stripped. " +
                "Use a Warden force action on them to validate.");
        }

        private static void Force(Pawn prisoner, WardenMode mode, bool requiresIdeology)
        {
            if (prisoner == null) return;

            if (requiresIdeology && !ModsConfig.IdeologyActive)
            {
                RimSynapse.SynapseLogger.Info("conversations",
                    $"[RimSynapse-Warden] {mode.Label()} needs the Ideology DLC, which is inactive — nothing to force.");
                return;
            }

            if (!prisoner.IsPrisonerOfColony && !prisoner.IsSlaveOfColony)
            {
                RimSynapse.SynapseLogger.Info("conversations",
                    $"[RimSynapse-Warden] {prisoner.LabelShort} is not a prisoner or slave — pick a captive to force a warden conversation.");
                return;
            }

            // Nearest free colonist stands in as the warden.
            Pawn warden = prisoner.Map?.mapPawns?.FreeColonists?
                .Where(o => o != prisoner && o.RaceProps.Humanlike && o.Spawned)
                .OrderBy(o => o.Position.DistanceToSquared(prisoner.Position))
                .FirstOrDefault();

            if (warden == null)
            {
                RimSynapse.SynapseLogger.Info("conversations",
                    $"[RimSynapse-Warden] No free colonist available to act as warden for {prisoner.LabelShort}.");
                return;
            }

            WardenConversationController.ForceWardenConversation(warden, prisoner, mode);
        }
    }
}
