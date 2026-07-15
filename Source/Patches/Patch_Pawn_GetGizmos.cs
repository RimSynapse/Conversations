using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;
using RimWorld;

namespace RimSynapse.Chat.Patches
{
    /// <summary>
    /// Harmony patch on Pawn.GetGizmos to display a "Chat History" button on colonist inspect panes.
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class Patch_Pawn_GetGizmos
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (var g in __result)
            {
                yield return g;
            }

            if (Current.ProgramState != ProgramState.Playing || Find.World == null) yield break;
            if (Find.Storyteller?.def?.defName != "Synapse") yield break;
            if (__instance.Faction != Faction.OfPlayer || !__instance.RaceProps.Humanlike) yield break;

            yield return new Command_Action
            {
                defaultLabel = "Chat History",
                defaultDesc = "View this pawn's short-term conversation logs with other colonists.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/ChatHistoryIcon", false) ?? BaseContent.BadTex,
                action = () =>
                {
                    Find.WindowStack.Add(new Dialog_PawnChatHistory(__instance));
                }
            };
        }
    }
}
