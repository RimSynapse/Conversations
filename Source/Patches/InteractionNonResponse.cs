using System.Collections.Generic;
using RimWorld;
using Verse;
using RimSynapse.Comps;
using RimSynapse.Models;

namespace RimSynapse.Conversations.Patches
{
    /// <summary>
    /// Handles non-response effects when a pawn chooses not to engage in a social interaction.
    /// Applies mood thoughts, social memories, and visual indicators.
    /// </summary>
    public static partial class Patch_Pawn_InteractionsTracker_TryInteractWith
    {
        private static void TriggerNonResponseEffects(Pawn initiator, Pawn recipient)
        {
            // Throw visual ellipses speech bubble
            UI.SpeechBubbleManager.AddBubble(recipient, "...", 3.0f);

            bool isInsulting = initiator.InMentalState && 
                               initiator.MentalState.def != null &&
                               initiator.MentalState.def.defName.IndexOf("insulting", System.StringComparison.OrdinalIgnoreCase) >= 0;

            if (isInsulting)
            {
                // Insulting pawn respects the recipient's silent/calm composure.
                // Apply a vanilla positive social thought (RapportBuilt) to improve relation/opinion.
                var thoughtDef = DefDatabase<ThoughtDef>.GetNamed("RapportBuilt", false);
                if (thoughtDef != null)
                {
                    initiator.needs?.mood?.thoughts?.memories?.TryGainMemory(thoughtDef, recipient);
                }
                
                var coreComp = initiator.TryGetComp<SynapseCorePawnComp>();
                if (coreComp != null)
                {
                    coreComp.memories.Add(new WeightedMemory
                    {
                        summary = $"Insulted {recipient.Name.ToStringShort} but they remained calm and stayed silent.",
                        memoryType = "social",
                        tags = new List<string> { "non_response", recipient.ThingID },
                        absTick = Utils.SynapseDateHelper.GameTickToAbsTick(Find.TickManager.TicksGame),
                        gameTick = Find.TickManager.TicksGame,
                        weight = 0.10f,
                        baseWeight = 0.10f,
                        decayRate = 0.10f
                    });
                }
            }
            else
            {
                // Standard chitchat / deep talk ignored
                if (Rand.Value < 0.75f)
                {
                    // Negative shift using Slighted memory
                    var thoughtDef = DefDatabase<ThoughtDef>.GetNamed("Slighted", false);
                    if (thoughtDef != null)
                    {
                        initiator.needs?.mood?.thoughts?.memories?.TryGainMemory(thoughtDef, recipient);
                    }
                    
                    var coreComp = initiator.TryGetComp<SynapseCorePawnComp>();
                    if (coreComp != null)
                    {
                        coreComp.memories.Add(new WeightedMemory
                        {
                            summary = $"Tried to talk to {recipient.Name.ToStringShort} but was ignored.",
                            memoryType = "social",
                            tags = new List<string> { "non_response", recipient.ThingID },
                            absTick = Utils.SynapseDateHelper.GameTickToAbsTick(Find.TickManager.TicksGame),
                            gameTick = Find.TickManager.TicksGame,
                            weight = 0.05f,
                            baseWeight = 0.05f,
                            decayRate = 0.05f
                        });
                    }
                }
                else
                {
                    // Positive shift using Chitchat memory (respected their quietness)
                    var thoughtDef = DefDatabase<ThoughtDef>.GetNamed("Chitchat", false);
                    if (thoughtDef != null)
                    {
                        initiator.needs?.mood?.thoughts?.memories?.TryGainMemory(thoughtDef, recipient);
                    }
                    
                    var coreComp = initiator.TryGetComp<SynapseCorePawnComp>();
                    if (coreComp != null)
                    {
                        coreComp.memories.Add(new WeightedMemory
                        {
                            summary = $"{recipient.Name.ToStringShort} quietly listened without speaking.",
                            memoryType = "social",
                            tags = new List<string> { "non_response", recipient.ThingID },
                            absTick = Utils.SynapseDateHelper.GameTickToAbsTick(Find.TickManager.TicksGame),
                            gameTick = Find.TickManager.TicksGame,
                            weight = 0.03f,
                            baseWeight = 0.03f,
                            decayRate = 0.03f
                        });
                    }
                }
            }
        }
    }
}
