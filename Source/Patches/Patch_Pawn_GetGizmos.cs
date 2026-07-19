using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;
using RimWorld;

namespace RimSynapse.Conversations.Patches
{
    /// <summary>
    /// Harmony patch on Pawn.GetGizmos to display a "Chat History" button on colonist inspect panes.
    /// </summary>
    [HarmonyPatch(typeof(ThingWithComps), nameof(ThingWithComps.GetGizmos))]
    public static class Patch_Pawn_GetGizmos
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, ThingWithComps __instance)
        {
            foreach (var g in __result)
            {
                yield return g;
            }

            if (!(__instance is Pawn pawn)) yield break;
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) yield break;
            if (pawn.Faction != Faction.OfPlayer || !pawn.RaceProps.Humanlike) yield break;

            yield return new Command_Action
            {
                defaultLabel = "Dialogue History",
                defaultDesc = "View the history of statements overheard or spoken by this colonist.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/ChatHistoryIcon", false) ?? BaseContent.BadTex,
                action = () =>
                {
                    Find.WindowStack.Add(new Dialog_PawnConversationHistory(pawn));
                }
            };
        }
    }
}
