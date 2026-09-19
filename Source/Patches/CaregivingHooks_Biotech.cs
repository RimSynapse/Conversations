using Verse;
using Verse.AI;
using RimWorld;
using HarmonyLib;

namespace RimSynapse.Conversations.Patches
{
    /// <summary>
    /// Biotech caregiving hooks (#65) — nursing and teaching become topics on the same substrate as tending
    /// (#64). Rather than patch the individual toil-based JobDrivers (fragile, version-sensitive), this hooks
    /// the single robust seam every job passes through on completion, <see cref="Pawn_JobTracker.EndCurrentJob"/>,
    /// and branches on the driver type. Everything is guarded by <see cref="ModsConfig.BiotechActive"/> so it is
    /// inert without the DLC, and every lookup is defensive so a wrong assumption fails silent, never throws.
    ///
    /// ⚠ UNVALIDATED: the exact driver types and target-index conventions are from the API index, not a live
    /// Biotech colony. Confirm in a running Biotech save (a feed and a school lesson each produce one caregiving
    /// topic on both parties) before treating this as done. See #65.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class Patch_Pawn_JobTracker_EndCurrentJob_Caregiving
    {
        // Prefix: read the job/driver BEFORE EndCurrentJob clears them. Cheap type checks on a hot path.
        public static void Prefix(Pawn_JobTracker __instance, JobCondition condition)
        {
            if (!ModsConfig.BiotechActive) return;
            if (condition != JobCondition.Succeeded) return;

            var driver = __instance?.curDriver;
            var job = __instance?.curJob;
            var actor = driver?.pawn;   // JobDriver.pawn is public; Pawn_JobTracker.pawn is not
            if (driver == null || job == null || actor == null || !actor.RaceProps.Humanlike) return;

            if (driver is JobDriver_FeedBaby)
            {
                Pawn baby = OtherPawnTarget(job, actor);
                if (baby == null) return;
                string babyName = baby.Name != null ? baby.Name.ToStringShort : "the baby";
                string feederName = actor.Name != null ? actor.Name.ToStringShort : "someone";
                // Routine care — never "serious"; low warmth. actor feeds baby.
                CaregivingHooks.Record(
                    carer: actor, cared: baby,
                    carerLine: $"feeding little {babyName}",
                    caredLine: $"being looked after by {feederName}",
                    actTag: "Nursing", serious: false);
                return;
            }

            // Teaching: the teacher runs Lessongiving; a child watching an adult work runs Workwatching (the
            // child is the actor, the adult the subject). Either way, pair the two humanlikes involved.
            if (driver is JobDriver_Lessongiving)
            {
                Pawn student = OtherPawnTarget(job, actor);
                if (student == null) return;
                RecordTeaching(teacher: actor, student: student);
                return;
            }
            if (driver is JobDriver_Workwatching)
            {
                Pawn adult = OtherPawnTarget(job, actor);
                if (adult == null) return;
                RecordTeaching(teacher: adult, student: actor);
                return;
            }
        }

        private static void RecordTeaching(Pawn teacher, Pawn student)
        {
            if (teacher == null || student == null || teacher == student) return;
            string teacherName = teacher.Name != null ? teacher.Name.ToStringShort : "someone";
            string studentName = student.Name != null ? student.Name.ToStringShort : "the young one";
            CaregivingHooks.Record(
                carer: teacher, cared: student,
                carerLine: $"teaching {studentName} what they know",
                caredLine: $"what {teacherName} has been teaching me",
                actTag: "Teaching", serious: false);
        }

        /// <summary>First humanlike pawn among the job's A/B/C targets that isn't the actor — the person the
        /// care act is aimed at, without assuming a specific TargetIndex.</summary>
        private static Pawn OtherPawnTarget(Job job, Pawn actor)
        {
            Pawn p = TargetPawn(job.targetA, actor);
            if (p != null) return p;
            p = TargetPawn(job.targetB, actor);
            if (p != null) return p;
            return TargetPawn(job.targetC, actor);
        }

        private static Pawn TargetPawn(LocalTargetInfo t, Pawn actor)
        {
            Pawn p = t.HasThing ? t.Thing as Pawn : null;
            return p != null && p != actor && p.RaceProps.Humanlike ? p : null;
        }
    }
}
