using System.Collections.Generic;
using System.Linq;
using RimSynapse.Conversations;
using RimSynapse.Conversations.Comps;
using RimAgentic.Testing;

namespace RimSynapse.Conversations.Tests
{
    /// <summary>
    /// Talking-point agenda substrate (#60 Phase 1 step 1): the container's cap / prioritise / dedupe
    /// rules and a point's audience rules. Deterministic — bare comps, no map, no pawns.
    /// </summary>
    [SynapseTestSet]
    public static class AgendaCases
    {
        private static TalkingPoint Pt(string memId, float salience, string summary = null)
            => new TalkingPoint { subjectMemId = memId, subjectSummary = summary ?? ("about " + memId), salience = salience };

        public static IEnumerable<SynapseTestCase> All()
        {
            // Cap & prioritise (§3): only the top-N by salience survive, strongest first.
            yield return new SynapseTestCase("Conversations_AgendaCapsByPriority", () =>
            {
                var agenda = new SynapseConversationAgendaComp();
                foreach (var s in new[] { 0.3f, 0.1f, 0.5f, 0.2f, 0.4f }) agenda.Add(Pt("m" + s, s));
                Assert.Equal(SynapseConversationAgendaComp.MaxPoints, agenda.Count, "agenda caps at MaxPoints");
                Assert.False(agenda.HasSubject("m0.1"), "weakest point is dropped over cap");
                Assert.Equal("m0.5", agenda.points[0].subjectMemId, "strongest point is first");
                Assert.True(agenda.points.All(p => !string.IsNullOrEmpty(p.id)), "every point gets an id");

                bool kept = agenda.Add(Pt("weak", 0.05f));
                Assert.False(kept, "a point weaker than everything on a full agenda is rejected");
                Assert.Equal(SynapseConversationAgendaComp.MaxPoints, agenda.Count, "rejection does not grow the agenda");

                bool strong = agenda.Add(Pt("strong", 0.9f));
                Assert.True(strong, "a stronger point displaces the weakest");
                Assert.False(agenda.HasSubject("m0.2"), "the displaced point was the weakest");
                return $"agenda=[{string.Join(",", agenda.points.Select(p => p.subjectMemId + ":" + p.salience))}]";
            });

            // Dedupe (§3 match): same subject collapses to the more salient point, never duplicates.
            yield return new SynapseTestCase("Conversations_AgendaDedupesBySubject", () =>
            {
                var agenda = new SynapseConversationAgendaComp();
                agenda.Add(Pt("raid", 0.5f, "the raid"));
                Assert.False(agenda.Add(Pt("raid", 0.3f, "the raid again")), "weaker duplicate is rejected");
                Assert.Equal(1, agenda.Count, "weaker duplicate does not add");
                Assert.True(agenda.Add(Pt("raid", 0.8f, "the raid, but it matters more now")), "stronger duplicate replaces");
                Assert.Equal(1, agenda.Count, "stronger duplicate does not grow the agenda");
                Assert.Equal(0.8f, agenda.points[0].salience, "the surviving point is the stronger one");
                Assert.False(agenda.Add(new TalkingPoint { subjectMemId = "x", salience = 1f }), "a point with no subject text is refused");
                return "dedupe ok";
            });

            // Audience rules (§1/§3): Only/Not, never retell a rumor to its source, per-listener told.
            yield return new SynapseTestCase("Conversations_TalkingPointAudienceRules", () =>
            {
                var open = Pt("m", 0.5f);
                Assert.True(open.Accepts("A") && open.Accepts("B"), "Anyone admits everyone");
                Assert.False(open.Accepts(null), "null listener is never admitted");

                var only = new TalkingPoint { subjectMemId = "m", subjectSummary = "s", audience = AudienceKind.Only, audiencePawnId = "A" };
                Assert.True(only.Accepts("A"), "Only admits its target");
                Assert.False(only.Accepts("B"), "Only excludes others");
                Assert.False(only.FullyTold, "untold Only point is not spent");
                only.MarkTold("A"); only.MarkTold("A");
                Assert.Equal(1, only.toldIds.Count, "MarkTold is idempotent");
                Assert.True(only.FullyTold, "Only point told to its target is spent");

                var not = new TalkingPoint { subjectMemId = "m", subjectSummary = "s", audience = AudienceKind.Not, audiencePawnId = "X" };
                Assert.False(not.Accepts("X"), "Not excludes its target (don't say it TO them)");
                Assert.True(not.Accepts("Y"), "Not admits everyone else");
                Assert.False(not.FullyTold, "open-audience points are never spent by exhaustion");

                var rumor = new TalkingPoint { subjectMemId = "m", subjectSummary = "s", provenance = PointProvenance.Heard, sourcePawnId = "T" };
                Assert.False(rumor.Accepts("T"), "a rumor is never retold to its source");
                Assert.True(rumor.Accepts("U"), "a rumor may be retold to others");
                Assert.True(rumor.Told("U") == false && !rumor.Told(null), "told-status is per listener");
                return "audience ok";
            });
        }
    }
}
