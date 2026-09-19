using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using RimSynapse.Comps;
using RimSynapse.Conversations.Generation;

namespace RimSynapse.Conversations.Patches
{
    /// <summary>
    /// Caregiving → conversation topics (#64). An act of care records a <see cref="AgendaFormation.CaregivingMemoryType"/>
    /// memory on BOTH parties, POV-phrased, which <see cref="AgendaFormation.FormCaregiving"/> turns into a warm
    /// talking point. These postfixes cover only the TOPIC — the trust/relationship effect of tending lives in
    /// Psychology's own <c>Patch_TendUtility_DoTend</c> and is untouched here (two mods postfix DoTend for two
    /// separate concerns). Biotech nursing/teaching hooks are the follow-on (#65).
    /// </summary>
    public static class CaregivingHooks
    {
        /// <summary>Weight at/above which the care reads as serious — a deep-talk point rather than chit-chat.
        /// Matches <see cref="AgendaFormation.DeepThreshold"/> so the register split is consistent.</summary>
        private const float SeriousCareWeight = 0.6f;

        /// <summary>Record one act of care on both pawns. <paramref name="carerLine"/>/<paramref name="caredLine"/>
        /// are phrased from each holder's point of view; <paramref name="serious"/> raises the weight so the rule
        /// forms a deep point.</summary>
        public static void Record(Pawn carer, Pawn cared, string carerLine, string caredLine, string actTag, bool serious)
        {
            if (carer == null || cared == null || carer == cared) return;
            if (!carer.RaceProps.Humanlike || !cared.RaceProps.Humanlike) return;

            float weight = serious ? 0.7f : 0.35f;
            var tags = new List<string> { "Caregiving", actTag };

            carer.TryGetComp<SynapseCorePawnComp>()?.AddMemoryAbout(
                cared, carerLine, AgendaFormation.CaregivingMemoryType, weight,
                tags: new List<string>(tags) { "carer" });
            cared.TryGetComp<SynapseCorePawnComp>()?.AddMemoryAbout(
                carer, caredLine, AgendaFormation.CaregivingMemoryType, weight,
                tags: new List<string>(tags) { "cared-for" });
        }

        /// <summary>Tending is one treatment covering every optimally-tended wound in a pass, so this fires once
        /// per act of care. Serious when the patient is (or just was) in real danger — downed or bleeding out.</summary>
        [HarmonyPatch(typeof(TendUtility), nameof(TendUtility.DoTend))]
        public static class Patch_TendUtility_DoTend_Topic
        {
            public static void Postfix(Pawn doctor, Pawn patient)
            {
                if (doctor == null || patient == null || doctor == patient) return;

                bool serious = patient.Downed
                    || (patient.health?.hediffSet?.BleedRateTotal ?? 0f) > 0.1f
                    || (patient.health?.summaryHealth?.SummaryHealthPercent ?? 1f) < 0.5f;

                string patientName = patient.Name.ToStringShort;
                string doctorName = doctor.Name.ToStringShort;
                CaregivingHooks.Record(
                    carer: doctor, cared: patient,
                    carerLine: serious ? $"patching {patientName} up when they were in a bad way" : $"tending {patientName}'s wounds",
                    caredLine: serious ? $"how {doctorName} patched me up when I was in a bad way" : $"how {doctorName} tended my wounds",
                    actTag: "Tend", serious: serious);
            }
        }
    }
}
