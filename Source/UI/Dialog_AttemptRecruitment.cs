using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.Sound;
using RimWorld;
using RimSynapse.Comps;

namespace RimSynapse.Conversations
{
    public class Dialog_AttemptRecruitment : Window
    {
        private Pawn resident;
        private Vector2 scrollPosition = Vector2.zero;

        public override Vector2 InitialSize => new Vector2(500f, 400f);

        public Dialog_AttemptRecruitment(Pawn resident)
        {
            this.resident = resident;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            forcePause = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            // Title
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), "Invite to Join: " + resident.Name.ToStringShort);
            Text.Font = GameFont.Small;

            // Faction & Relation
            string factionName = resident.Faction?.Name ?? "None";
            var relation = resident.Faction?.RelationWith(Faction.OfPlayer);
            string relationStr = relation != null ? $"{relation.kind} ({relation.baseGoodwill})" : "Neutral";
            Widgets.Label(new Rect(0f, 40f, inRect.width, 25f), $"Faction: {factionName} | Relation: {relationStr}");

            // Divider line
            Widgets.DrawLineHorizontal(0f, 70f, inRect.width);

            var comp = resident.TryGetComp<SynapseCorePawnComp>();
            if (comp == null)
            {
                Widgets.Label(new Rect(0f, 80f, inRect.width, 30f), "Error: Resident has no RimSynapse component.");
                return;
            }

            // Cooldown check
            int currentTick = Find.TickManager.TicksGame;
            int ticksSinceAttempt = currentTick - comp.lastRecruitmentAttemptTick;
            int cooldownTicks = 60000; // 1 in-game day
            int remainingTicks = cooldownTicks - ticksSinceAttempt;

            if (remainingTicks > 0)
            {
                float hoursLeft = remainingTicks / 2500f;
                Rect warningRect = new Rect(0f, 80f, inRect.width, 50f);
                GUI.color = Color.yellow;
                Widgets.Label(warningRect, $"[Recruitment Cooldown] The resident is not interested in discussing recruitment right now.\nNext attempt available in {hoursLeft:F1} hours.");
                GUI.color = Color.white;
                return;
            }

            // List of colonists (negotiators)
            Rect listRect = new Rect(0f, 80f, inRect.width, inRect.height - 100f);
            var colonists = Find.Maps
                .SelectMany(m => m.mapPawns.FreeColonists)
                .Where(p => !p.Dead && !p.Downed && p.skills != null)
                .ToList();

            if (colonists.Count == 0)
            {
                Widgets.Label(new Rect(0f, 80f, inRect.width, 30f), "No available colonists to recruit.");
                return;
            }

            float rowHeight = 50f;
            Rect viewRect = new Rect(0f, 0f, listRect.width - 16f, colonists.Count * rowHeight);
            Widgets.BeginScrollView(listRect, ref scrollPosition, viewRect);

            float curY = 0f;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn recruiter = colonists[i];
                Rect rowRect = new Rect(0f, curY, viewRect.width, rowHeight - 4f);
                Widgets.DrawBoxSolid(rowRect, new Color(0.15f, 0.15f, 0.15f, 0.2f));

                // Recruiter Icon & Name
                Widgets.ThingIcon(new Rect(rowRect.x + 4f, rowRect.y + 4f, 32f, 32f), recruiter);
                
                int socialLevel = recruiter.skills.GetSkill(SkillDefOf.Social).Level;
                int opinion = resident.relations.OpinionOf(recruiter);
                
                // Calculate success chance
                float socialChance = socialLevel * 0.025f;
                float opinionFactor = opinion * 0.003f;
                float relationFactor = 0f;
                if (resident.Faction != null)
                {
                    var rKind = resident.Faction.RelationWith(Faction.OfPlayer).kind;
                    if (rKind == FactionRelationKind.Ally) relationFactor = 0.20f;
                    else if (rKind == FactionRelationKind.Hostile) relationFactor = -0.80f;
                }
                float baseChance = Mathf.Clamp(0.10f + socialChance + opinionFactor + relationFactor, 0.01f, 0.99f);
                float finalChance = RimSynapse.Utils.SynapseRecruitmentMath.CalculateRecruitmentChance(recruiter, resident, baseChance);

                string detailStr = $"{recruiter.Name.ToStringShort} (Social: {socialLevel}, Opinion: {opinion})";
                Widgets.Label(new Rect(rowRect.x + 44f, rowRect.y + 4f, 220f, 20f), detailStr);
                
                Widgets.Label(new Rect(rowRect.x + 44f, rowRect.y + 24f, 220f, 20f), $"Success Chance: {finalChance:P0}");

                // Recruitment button
                Rect btnRect = new Rect(rowRect.width - 140f, rowRect.y + 8f, 130f, 30f);
                if (resident.Faction != null && resident.Faction.HostileTo(Faction.OfPlayer))
                {
                    GUI.color = Color.gray;
                    Widgets.Label(btnRect, "Hostile Faction");
                    GUI.color = Color.white;
                }
                else if (Widgets.ButtonText(btnRect, "Recruit"))
                {
                    AttemptRecruit(recruiter, finalChance, comp);
                    break;
                }

                curY += rowHeight;
            }

            Widgets.EndScrollView();
        }

        private void AttemptRecruit(Pawn recruiter, float chance, SynapseCorePawnComp comp)
        {
            // Sound
            SoundDefOf.Click?.PlayOneShotOnCamera();

            // Set cooldown
            comp.lastRecruitmentAttemptTick = Find.TickManager.TicksGame;

            // Roll chance
            float roll = Rand.Value;
            bool success = roll <= chance;

            if (success)
            {
                var faction = resident.Faction;
                var map = resident.Map;

                // Join Faction
                resident.SetFaction(Faction.OfPlayer);

                if (map != null && faction != null)
                {
                    bool otherResidentAlive = map.mapPawns.AllPawns
                        .Any(p => p != resident && p.Faction == faction && p.RaceProps.Humanlike && !p.Dead && RimSynapse.SynapseCoreProviders.IsResident(p));

                    if (!otherResidentAlive)
                    {
                        var thingsToClear = map.listerThings.AllThings
                            .Where(t => t.Faction == faction)
                            .ToList();
                        
                        foreach (var t in thingsToClear)
                        {
                            t.SetFaction(null);
                        }
                        
                        Messages.Message($"The residents of {faction.Name} on this map have all died or been recruited. Their property is now unclaimed.", MessageTypeDefOf.NeutralEvent, true);
                    }
                }
                
                // Send success message/letter
                string msg = $"{resident.Name.ToStringShort} has been persuaded by {recruiter.Name.ToStringShort} to join the colony!\n\nThey have officially packed up their things and became a member of your faction.";
                Find.LetterStack.ReceiveLetter(
                    "Pawn Recruited",
                    msg,
                    LetterDefOf.PositiveEvent,
                    resident
                );

                this.Close();
            }
            else
            {
                // Decrease opinion
                if (resident.relations != null)
                {
                    resident.relations.OpinionOf(recruiter);
                    // Add a negative memory/relation change using RefusedMyProposal (which is a Thought_MemorySocial)
                    var thoughtDef = DefDatabase<ThoughtDef>.GetNamed("RefusedMyProposal", false);
                    if (thoughtDef != null)
                    {
                        Thought_MemorySocial socialThought = (Thought_MemorySocial)ThoughtMaker.MakeThought(thoughtDef);
                        socialThought.opinionOffset = -15;
                        resident.needs?.mood?.thoughts?.memories?.TryGainMemory(socialThought, recruiter);
                    }
                    else
                    {
                        var fallbackDef = DefDatabase<ThoughtDef>.GetNamed("Slighted", false);
                        if (fallbackDef != null)
                        {
                            Thought_MemorySocial socialThought = (Thought_MemorySocial)ThoughtMaker.MakeThought(fallbackDef);
                            socialThought.opinionOffset = -15;
                            resident.needs?.mood?.thoughts?.memories?.TryGainMemory(socialThought, recruiter);
                        }
                    }
                }

                // Check for critical failure (e.g. rolled near 1.0 or very low chance)
                bool critFail = (roll > 0.95f) || (chance < 0.15f && Rand.Value < 0.33f);
                if (critFail && resident.Faction != null)
                {
                    // Faction goodwill drop
                    resident.Faction.TryAffectGoodwillWith(Faction.OfPlayer, -15);
                    string failMsg = $"{resident.Name.ToStringShort} was offended by {recruiter.Name.ToStringShort}'s recruitment pitch! Faction goodwill decreased by 15.";
                    Messages.Message(failMsg, MessageTypeDefOf.CautionInput, true);
                }
                else
                {
                    string failMsg = $"{resident.Name.ToStringShort} politely declined {recruiter.Name.ToStringShort}'s invite to join the colony.";
                    Messages.Message(failMsg, MessageTypeDefOf.NeutralEvent, true);
                }
            }
        }
    }
}
