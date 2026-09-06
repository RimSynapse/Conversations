using System.Collections.Generic;
using System.Linq;
using Verse;
using RimSynapse.Conversations.Comps;

namespace RimSynapse.Conversations
{
    /// <summary>
    /// Def-injects <see cref="SynapseConversationAgendaComp"/> onto every humanlike ThingDef at startup —
    /// the same pattern Core and Psychology use for their pawn comps, so there is no XML patch to keep in
    /// sync with race mods. Runs after Psychology's injector because Psychology is a load-order dependency.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class SynapseConversationsInjector
    {
        static SynapseConversationsInjector()
        {
            int injected = 0;
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs.Where(d => d.race != null && d.race.Humanlike))
            {
                if (def.comps == null) def.comps = new List<CompProperties>();
                if (def.comps.Any(c => c.compClass == typeof(SynapseConversationAgendaComp))) continue;
                def.comps.Add(new CompProperties { compClass = typeof(SynapseConversationAgendaComp) });
                injected++;
            }
            RimSynapse.SynapseLogger.Info("conversations", $"[RimSynapse-Conversations] Injected SynapseConversationAgendaComp into {injected} humanlike ThingDefs.");
        }
    }
}
