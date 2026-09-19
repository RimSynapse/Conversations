using System.Collections.Generic;
using System.Linq;
using Verse;
using RimSynapse.Comps;
using RimSynapse.Models;
using RimSynapse.Conversations;
using RimSynapse.Conversations.Generation;
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

            // Formation rules 1–2 (event, burden) over a synthetic Core comp — register, secret, dedupe.
            yield return new SynapseTestCase("Conversations_FormationFromMemories", () =>
            {
                long nowAbs = Find.TickManager != null ? Find.TickManager.TicksAbs : 1000000L;
                var core = new SynapseCorePawnComp();
                core.AddMemory(new WeightedMemory { summary = "the pirates came through the east wall", memoryType = "EventReflection", weight = 0.9f, baseWeight = 0.9f, absTick = nowAbs - 1000, tags = new List<string> { "Horror" } });
                core.AddMemory(new WeightedMemory { summary = "swapped stories by the fire", memoryType = "EventReflection", weight = 0.2f, baseWeight = 0.2f, absTick = nowAbs - 2000 });
                core.AddMemory(new WeightedMemory { summary = "a dull chore a season ago", memoryType = "social", weight = 0.1f, baseWeight = 0.1f, absTick = nowAbs - 900000 });

                var agenda = new SynapseConversationAgendaComp();
                var trace = new List<string>();
                AgendaFormation.FormFromMemories(core, agenda, 100, nowAbs, 0.7f, null, trace);

                var ev = agenda.points.FirstOrDefault(p => p.subjectSummary.Contains("east wall"));
                Assert.True(ev != null, "the strongest fresh event forms a point");
                Assert.Equal(PointRegister.DeepTalk, ev.register, "a heavy event is deep talk");
                Assert.True(ev.secret, "a Horror-tagged memory is secret");
                Assert.True(ev.salience > 0.9f, "a fresh event gets the recency bonus");
                Assert.False(agenda.points.Any(p => p.subjectSummary.Contains("dull chore")), "a trivial memory is not a burden");
                Assert.True(agenda.points.Count(p => p.subjectSummary.Contains("east wall")) == 1, "event and burden dedupe on the same memory");

                int before = agenda.Count;
                AgendaFormation.FormFromMemories(core, agenda, 200, nowAbs, 0.7f, null, null);
                Assert.True(agenda.Count >= before, "a second pass never removes points");
                Assert.True(agenda.points.Count(p => p.subjectSummary.Contains("east wall")) == 1, "a second pass does not duplicate");

                Assert.Equal(PointRegister.ChitChat, AgendaFormation.RegisterFor(new WeightedMemory { weight = 0.3f }, 0.8f), "a light memory in a good mood is chit-chat");
                Assert.Equal(PointRegister.DeepTalk, AgendaFormation.RegisterFor(new WeightedMemory { weight = 0.45f }, 0.2f), "a moderate memory in a low mood turns deep");
                return $"trace=[{string.Join("; ", trace)}]";
            });

            // Caregiving rule (#64): a Caregiving memory becomes a warm, audience-ANYONE point (you may
            // thank your carer), deep only when the care was serious. A non-caregiving memory is ignored here.
            yield return new SynapseTestCase("Conversations_FormationCaregiving", () =>
            {
                long nowAbs = Find.TickManager != null ? Find.TickManager.TicksAbs : 1000000L;
                var core = new SynapseCorePawnComp();
                core.AddMemory(new WeightedMemory { summary = "how Vale patched me up when I was in a bad way", memoryType = "Caregiving", weight = 0.7f, baseWeight = 0.7f, absTick = nowAbs - 500, subjectPawnIds = new List<string> { "VALE" }, tags = new List<string> { "Caregiving", "Tend", "cared-for" } });
                core.AddMemory(new WeightedMemory { summary = "tending Rook's scratches", memoryType = "Caregiving", weight = 0.35f, baseWeight = 0.35f, absTick = nowAbs - 400, subjectPawnIds = new List<string> { "ROOK" }, tags = new List<string> { "Caregiving", "Tend", "carer" } });
                core.AddMemory(new WeightedMemory { summary = "an ordinary chat", memoryType = "social", weight = 0.5f, baseWeight = 0.5f, absTick = nowAbs - 300 });

                var agenda = new SynapseConversationAgendaComp();
                var trace = new List<string>();
                AgendaFormation.FormCaregiving(core, agenda, 100, nowAbs, trace);

                var pt = agenda.points.FirstOrDefault(p => p.subjectSummary.Contains("patched me up"));
                Assert.True(pt != null, "a caregiving memory forms a point");
                Assert.Equal(AudienceKind.Anyone, pt.audience, "caregiving is audience-Anyone — you can thank your carer");
                Assert.Equal(PointRegister.DeepTalk, pt.register, "serious care (weight >= 0.6) is deep talk");
                Assert.False(agenda.points.Any(p => p.subjectSummary.Contains("ordinary chat")), "a non-caregiving memory is not formed by this rule");

                // Only one per pass; a second pass does not duplicate the same memory.
                int before = agenda.Count;
                AgendaFormation.FormCaregiving(core, agenda, 200, nowAbs, null);
                Assert.True(agenda.points.Count(p => p.subjectSummary.Contains("patched me up")) == 1, "no duplicate on a second pass");
                Assert.True(agenda.Count >= before, "a second pass never removes points");
                return $"trace=[{string.Join("; ", trace)}]";
            });

            // Decay & prune (§3): weak, spent and stale points go, and their subjects are retired.
            yield return new SynapseTestCase("Conversations_AgendaDecayAndRetire", () =>
            {
                var agenda = new SynapseConversationAgendaComp();
                agenda.Add(new TalkingPoint { subjectMemId = "fresh", subjectSummary = "s", salience = 0.9f, formedTick = 900 });
                agenda.Add(new TalkingPoint { subjectMemId = "weak", subjectSummary = "s", salience = 0.05f, formedTick = 900 });
                agenda.Add(new TalkingPoint { subjectMemId = "stale", subjectSummary = "s", salience = 0.8f, formedTick = 0 });
                var spent = new TalkingPoint { subjectMemId = "spent", subjectSummary = "s", salience = 0.8f, formedTick = 900, audience = AudienceKind.Only, audiencePawnId = "A" };
                spent.MarkTold("A");
                agenda.Add(spent);

                int pruned = AgendaFormation.DecayAndPrune(agenda, 1000, retentionTicks: 500);
                Assert.Equal(3, pruned, "weak, stale and spent points are pruned");
                Assert.Equal(1, agenda.Count, "the fresh point survives");
                Assert.True(agenda.points[0].salience < 0.9f && agenda.points[0].salience > 0.8f, "surviving salience decayed by one pass");
                Assert.True(agenda.IsRetired("weak") && agenda.IsRetired("stale") && agenda.IsRetired("spent"), "pruned subjects are retired");
                Assert.False(agenda.IsRetired("fresh"), "a live subject is not retired");
                return "decay ok";
            });

            // Link bank (rule 5): the authored pairs resolve; same-role pairs never do.
            yield return new SynapseTestCase("Conversations_LinkBankPairs", () =>
            {
                Assert.True(LinkBank.FindDef(LinkRole.Visitor, LinkRole.Resident) != null, "visitor->resident openers exist");
                Assert.True(LinkBank.FindDef(LinkRole.Resident, LinkRole.Visitor) != null, "resident->visitor openers exist");
                Assert.True(LinkBank.FindDef(LinkRole.Resident, LinkRole.NewCitizen) != null, "resident->new citizen openers exist");
                Assert.True(LinkBank.FindDef(LinkRole.NewCitizen, LinkRole.Resident) != null, "new citizen->resident openers exist");
                Assert.True(LinkBank.FindDef(LinkRole.Guest, LinkRole.Resident) != null, "guest->resident openers exist");
                Assert.True(LinkBank.FindDef(LinkRole.Visitor, LinkRole.Visitor) == null, "same-role pairs have no link");
                Assert.True(LinkBank.FindDef(LinkRole.None, LinkRole.Resident) == null, "None never links");
                string line = LinkBank.PickLine(LinkBank.FindDef(LinkRole.Visitor, LinkRole.Resident));
                Assert.True(!string.IsNullOrEmpty(line), "a picked opener is a real line");
                return $"sample=\"{line}\"";
            });

            // Point → beat (step 3): kind / topic key / tone are derived purely from the point.
            yield return new SynapseTestCase("Conversations_BeatKeysFromPoint", () =>
            {
                var mem = new TalkingPoint { subjectMemId = "123_ab", subjectSummary = "s", register = PointRegister.DeepTalk };
                Assert.Equal("memory", AgendaBeatBuilder.Kind(mem), "a bare memId is a memory point");
                Assert.Equal("event:123_ab", AgendaBeatBuilder.TopicKeyFor(mem), "memory points keep the resolver's event key scheme");
                Assert.Equal(BeatTone.Heartfelt, AgendaBeatBuilder.ToneFor(mem.register), "deep talk is heartfelt");

                var link = new TalkingPoint { subjectMemId = "link:RimSynapse_Link_Visitor_Resident:Thing_Human1", subjectSummary = "What's new around here?" };
                Assert.Equal("link", AgendaBeatBuilder.Kind(link), "a prefixed key yields its prefix");
                Assert.Equal(link.subjectMemId, AgendaBeatBuilder.TopicKeyFor(link), "synthetic points already carry a prefixed key");
                Assert.Equal(BeatTone.Casual, AgendaBeatBuilder.ToneFor(link.register), "chit-chat is casual");
                Assert.Equal("rumor", AgendaBeatBuilder.Kind(new TalkingPoint { subjectMemId = "rumor:12:ab" }), "rumor prefix");
                Assert.Equal("memory", AgendaBeatBuilder.Kind(new TalkingPoint()), "no key defaults to memory");
                return "beat keys ok";
            });

            // Point-keyed pool (step 3): keyed by (speaker, listener, point), bounded, popped exactly once,
            // with a self-healing in-flight guard.
            yield return new SynapseTestCase("Conversations_PointPoolAndPendingGuard", () =>
            {
                var wc = new SynapseConversationsWorldComponent(Find.World);
                PreGeneratedConversation Conv(string a, string b, string pt) => new PreGeneratedConversation { initiatorId = a, recipientId = b, pointId = pt, initiatorStatement = "x", recipientResponse = "y", generatedAtTick = 0 };

                wc.AddPointPreGen(Conv("A", "B", "p1"));
                wc.AddPointPreGen(Conv("A", "C", "p1"));
                wc.AddPointPreGen(Conv("A", "B", "p1")); // duplicate
                Assert.Equal(2, wc.PointPreGenCount, "same point to two listeners is two entries; a duplicate is not");
                Assert.True(wc.HasPooledPoint("A", "B", "p1") && wc.HasPooledPoint("A", "C", "p1"), "both keys present");
                Assert.False(wc.HasPooledPoint("B", "A", "p1"), "the key is directional");
                Assert.False(wc.HasPooledPoint("A", "B", null), "null point never matches");

                var popped = wc.PopPooledPoint("A", "B", "p1");
                Assert.True(popped != null && popped.recipientId == "B", "pop returns the exact entry");
                Assert.True(wc.PopPooledPoint("A", "B", "p1") == null, "an entry pops exactly once");
                Assert.Equal(1, wc.PointPreGenCount, "the other listener's entry remains");

                string key = AgendaPregeneration.PoolKey("A", "B", "p2");
                Assert.True(wc.TryMarkPending(key, 100), "first claim succeeds");
                Assert.False(wc.TryMarkPending(key, 200), "a live claim blocks a second");
                wc.ClearPending(key);
                Assert.True(wc.TryMarkPending(key, 300), "cleared claim can be retaken");
                Assert.True(wc.TryMarkPending(key, 300 + SynapseConversationsWorldComponent.PendingTimeoutTicks), "a stale claim self-heals after the timeout");
                return $"pool={wc.PointPreGenCount} pending={wc.PendingPointGenCount}";
            });

            // Trigger serve → consume (step 4) on two real spawned colonists, with a hand-built pooled
            // exchange (no LLM): lines land in the pair record, the point is consumed for that listener, and
            // an Only-audience point that has reached its target leaves the agenda.
            yield return new SynapseTestCase("Conversations_TriggerServesAndConsumes", () =>
            {
                var map = Find.CurrentMap;
                Assert.True(map != null, "no map available");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(c => c.RaceProps.Humanlike).Take(2).ToList();
                Assert.True(colonists.Count == 2, "needs two spawned colonists");
                Pawn a = colonists[0], b = colonists[1];
                var wc = Find.World.GetComponent<SynapseConversationsWorldComponent>();
                var mc = map.GetComponent<SynapseConversationsMapComponent>();
                var agenda = a.TryGetComp<SynapseConversationAgendaComp>();
                Assert.True(wc != null && agenda != null, "world component and agenda comp present (injection)");

                var point = new TalkingPoint { subjectMemId = "test:serve", subjectSummary = "a test subject", salience = 0.99f,
                    audience = AudienceKind.Only, audiencePawnId = b.ThingID, formedTick = Find.TickManager.TicksGame };
                Assert.True(agenda.Add(point), "test point goes on the agenda");
                var conv = new PreGeneratedConversation
                {
                    initiatorId = a.ThingID, recipientId = b.ThingID, pointId = point.id, topicDefName = "test:serve",
                    initiatorStatement = "test line one", recipientResponse = "test line two",
                    lines = new List<PooledLine> { new PooledLine(a.ThingID, "test line one"), new PooledLine(b.ThingID, "test line two"), new PooledLine(a.ThingID, "test line three") },
                    generatedAtTick = Find.TickManager.TicksGame, generatedAtAbsTick = Find.TickManager.TicksAbs
                };
                wc.AddPointPreGen(conv);
                Assert.True(wc.HasPooledPoint(a.ThingID, b.ThingID, point.id), "pooled for the pair");

                var popped = wc.PopPooledPoint(a.ThingID, b.ThingID, point.id);
                int now = Find.TickManager.TicksGame;
                AgendaTrigger.Serve(a, b, point, agenda, popped, wc, mc, now);

                var record = SynapseConversationsWorldComponent.GetOrStartConversation(wc, a, b, now);
                Assert.True(record.messages.Count > 0 && record.messages.Last().message == "test line one", "first line landed in the pair record");
                Assert.True(point.Told(b.ThingID), "the point is consumed for the listener");
                Assert.True(point.FullyTold, "an Only point told to its target is spent");
                Assert.True(agenda.Find(point.id) == null, "a spent point left the agenda");
                Assert.True(agenda.IsRetired("test:serve"), "its subject is retired");
                Assert.False(wc.HasPooledPoint(a.ThingID, b.ThingID, point.id), "the pooled entry was consumed");
                Assert.True(mc == null || mc.IsInPlayback(a), "remaining lines are drip-feeding");
                return $"{a.LabelShort} -> {b.LabelShort}: served, told, spent";
            });

            // Propagation (step 5, #36): the rumor mill as one rule over the agenda graph.
            yield return new SynapseTestCase("Conversations_PropagationDerivesRumors", () =>
            {
                var told = new SynapseConversationAgendaComp();
                var first = new TalkingPoint { subjectMemId = "100_ab", subjectSummary = "the pirates came through the east wall", salience = 0.8f, register = PointRegister.DeepTalk };

                var d = AgendaPropagation.Derive("T", first, told, 10, roll: 0.5f);
                Assert.True(d != null, "a first-hand memory point propagates");
                Assert.Equal(PointProvenance.Heard, d.provenance, "the derived point is a rumor");
                Assert.Equal("T", d.sourcePawnId, "source is the teller");
                Assert.True(d.salience > 0.47f && d.salience < 0.49f, "salience is scaled by the spread factor");
                Assert.False(d.Accepts("T"), "a rumor is never retold to its source");
                Assert.True(d.Accepts("U"), "a rumor may be retold to others");
                Assert.True(AgendaPropagation.Derive("T", first, told, 11, 0.5f) == null, "already held: no duplicate rumor");

                // Chain: the listener tells a third pawn; salience decays again; source moves on.
                var third = new SynapseConversationAgendaComp();
                var dd = AgendaPropagation.Derive("L", d, third, 20, 0.5f);
                Assert.True(dd != null && dd.sourcePawnId == "L", "a rumor re-propagates with the new teller as source");
                Assert.True(dd.salience > 0.28f && dd.salience < 0.30f, "chains decay geometrically");
                var faint = new TalkingPoint { subjectMemId = "faint", subjectSummary = "s", salience = 0.15f };
                Assert.True(AgendaPropagation.Derive("T", faint, new SynapseConversationAgendaComp(), 1, 0.5f) == null, "a faint point dies rather than spreading");

                // Secrets resist.
                var secret = new TalkingPoint { subjectMemId = "200_cd", subjectSummary = "s", salience = 0.9f, secret = true };
                Assert.True(AgendaPropagation.Derive("T", secret, new SynapseConversationAgendaComp(), 1, 0.5f) == null, "a secret usually is not re-spread");
                var leaked = AgendaPropagation.Derive("T", secret, new SynapseConversationAgendaComp(), 1, 0.05f);
                Assert.True(leaked != null && leaked.secret, "when a secret does spread it stays secret");

                // Audience carries over; openers and small talk are not rumor material.
                var about = new TalkingPoint { subjectMemId = "bond:Thing_Human9", subjectSummary = "s", salience = 0.7f, audience = AudienceKind.Not, audiencePawnId = "Thing_Human9" };
                var da = AgendaPropagation.Derive("T", about, new SynapseConversationAgendaComp(), 1, 0.5f);
                Assert.True(da != null && da.audience == AudienceKind.Not && da.audiencePawnId == "Thing_Human9", "never say it TO them, even second-hand");
                Assert.False(AgendaPropagation.Propagates(new TalkingPoint { subjectMemId = "link:X:Y", subjectSummary = "s", audience = AudienceKind.Only, audiencePawnId = "Y" }), "an opener is not a rumor");
                Assert.False(AgendaPropagation.Propagates(new TalkingPoint { subjectMemId = "activity:cooking", subjectSummary = "s" }), "small talk is not a rumor");
                Assert.True(AgendaPropagation.Propagates(new TalkingPoint { subjectMemId = "rumor:1:ab", subjectSummary = "s" }), "a rumor is rumor material");
                return $"first={first.salience:F2} heard={d.salience:F2} chain={dd.salience:F2}";
            });

            // Exchange counts (step 6 v1): ordered-pair tallies, both directions summed for the clique read.
            yield return new SynapseTestCase("Conversations_ExchangeCounts", () =>
            {
                var wc = new SynapseConversationsWorldComponent(Find.World);
                wc.RecordExchange("A", "B"); wc.RecordExchange("A", "B"); wc.RecordExchange("B", "A");
                wc.RecordExchange(null, "B");
                Assert.Equal(2, wc.TellCount("A", "B"), "A told B twice");
                Assert.Equal(1, wc.TellCount("B", "A"), "B told A once");
                Assert.Equal(3, wc.ExchangeCount("A", "B"), "mutual exchange sums both directions");
                Assert.Equal(0, wc.ExchangeCount("A", "C"), "strangers have none");
                Assert.Equal(2, wc.exchangeCounts.Count, "null ids are ignored");
                return "counts ok";
            });
        }
    }
}
