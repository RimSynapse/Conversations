using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using RimSynapse.Comps;
using RimSynapse.Models;
using RimSynapse.Conversations.Comps;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>
    /// Formation — the psychology hook (#60 Phase 1 §2). Turns a pawn's state into the few things they WANT
    /// to say. Runs on a cadence per pawn (see the map component's sweep) and on demand from debug.
    /// The rules are the ported branches of <see cref="ConversationBeatResolver"/> (event / burden /
    /// activity / observation) plus the new ones (bond shift, link, colony rumor). Selection is pure C#;
    /// nothing here touches the LLM.
    /// </summary>
    public static class AgendaFormation
    {
        // ── Register threshold (§9, settled provisionally — tune from in-game review) ────────────
        /// <summary>Memory weight/salience at or above which a point is DeepTalk on its own.</summary>
        public const float DeepThreshold = 0.6f;
        /// <summary>Below this mood, a moderately weighty memory (≥ <see cref="LowMoodDeepWeight"/>) turns deep too.</summary>
        public const float LowMood = 0.35f;
        public const float LowMoodDeepWeight = 0.4f;

        // ── Salience ──────────────────────────────────────────────────────────────────────────────
        public const float RecencyBonus = 0.2f;          // an event from the last day is fresher in the mouth
        public const float BurdenMinWeight = 0.4f;       // a trivial memory is not a burden
        public const float ActivitySalience = 0.25f;
        public const float LinkSalience = 0.15f;
        public const float RumorSalience = 0.35f;
        public const float AmbientSalience = 0.08f;
        public const float BondShiftThreshold = 15f;     // |Δtrust| to notice (warmth axis is #72, not in 0.10 yet)
        public const float BondShiftDeep = 30f;

        // ── Decay (§3) ────────────────────────────────────────────────────────────────────────────
        /// <summary>Per formation pass (~1h). 0.92^24 ≈ 0.14, so a 0.9 point is spent in roughly a day.</summary>
        public const float DecayPerPass = 0.92f;
        public const float PruneSalience = 0.05f;

        public const int RecentEventTicks = 180000;      // ~3 in-game days (ported from the resolver)
        public const long FreshEventTicks = 60000;       // within the last day
        public const float LinkRadius = 12f;
        public const int MaxRumorsPerVisitor = 2;

        private static readonly string[] SecretTags = { "Trauma", "Betrayal", "Horror" };

        // Activities nobody strikes up a conversation about (ported from the resolver, #44).
        private static readonly string[] MundaneActivities =
            { "idle", "lying down", "sleeping", "wandering", "standing", "going", "waiting", "meditating" };

        // ═════════════════════════════════════════════════════════════════════════════════════════
        // Entry point
        // ═════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// One full pass for a pawn: decay + prune, then every applicable rule. <paramref name="nearby"/> is
        /// the candidate listener set for links (the map's spawned pawns; distance-filtered here). Returns a
        /// human-readable trace for the debug action / log.
        /// </summary>
        public static string FormFor(Pawn pawn, int nowTick, IReadOnlyList<Pawn> nearby)
        {
            var agenda = pawn?.TryGetComp<SynapseConversationAgendaComp>();
            if (agenda == null) return "no agenda comp";

            var trace = new List<string>();
            bool conversant = RimSynapse.SynapseCoreProviders.MayConverse(pawn);
            LinkRole role = LinkBank.RoleOf(pawn);
            long nowAbs = Find.TickManager != null ? Find.TickManager.TicksAbs : 0L;
            float mood = pawn.needs?.mood?.CurLevelPercentage ?? 0.5f;

            int pruned = DecayAndPrune(agenda, nowTick, RetentionTicks());
            if (pruned > 0) trace.Add($"pruned {pruned}");

            var core = pawn.TryGetComp<SynapseCorePawnComp>();
            if (conversant && core != null)
            {
                FormFromMemories(core, agenda, nowTick, nowAbs, mood, pawn, trace);
                FormActivity(core, agenda, nowTick, trace);
            }
            if (conversant) FormBondShifts(pawn, agenda, nowTick, trace);

            // Outsiders (visitor / trader / guest) arrive carrying what the road says about this place.
            if (role == LinkRole.Visitor || role == LinkRole.Trader || role == LinkRole.Guest)
            {
                int seeded = SeedColonyRumors(pawn, agenda, nowTick);
                if (seeded > 0) trace.Add($"rumors {seeded}");
            }

            FormLinks(pawn, role, agenda, nowTick, nearby, trace);

            if (conversant && agenda.IsEmpty) FormAmbient(pawn, agenda, nowTick, trace);

            return trace.Count > 0 ? string.Join("; ", trace) : "nothing new";
        }

        public static int RetentionTicks()
            => Mathf.RoundToInt((RimSynapseConversationsMod.Settings?.conversationHistoryHours ?? 24f) * 2500f);

        // ═════════════════════════════════════════════════════════════════════════════════════════
        // Decay
        // ═════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Salience falls each pass; a point below threshold, spent, or older than the retention
        /// window is pruned and its subject retired so it does not reform on the next pass.</summary>
        public static int DecayAndPrune(SynapseConversationAgendaComp agenda, int nowTick, int retentionTicks)
        {
            if (agenda == null || agenda.IsEmpty) return 0;
            foreach (var p in agenda.points) p.salience *= DecayPerPass;
            return agenda.Prune(p => p.salience < PruneSalience || p.FullyTold || p.AgeTicks(nowTick) > retentionTicks);
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        // Rules 1–2: Event, Burden (from Core memories)
        // ═════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Event + Burden rules. Pure over the comps so it is testable without a map;
        /// <paramref name="self"/> may be null (only used to scope audience away from the memory's subject).</summary>
        public static void FormFromMemories(SynapseCorePawnComp core, SynapseConversationAgendaComp agenda,
            int nowTick, long nowAbs, float mood, Pawn self, List<string> trace)
        {
            if (core?.memories == null || core.memories.Count == 0) return;

            // Rule 1 — Event: a significant EventReflection they lived through. One per pass: the strongest
            // fresh one, so the agenda fills over a few hours rather than all at once.
            var ev = core.memories
                .Where(m => m != null && m.memoryType == "EventReflection" && !string.IsNullOrEmpty(m.summary))
                .Where(m => m.isLongTerm || nowAbs - m.absTick <= RecentEventTicks)
                .Where(m => !Known(agenda, MemId(m)))
                .OrderByDescending(m => Base(m) + (nowAbs - m.absTick < FreshEventTicks ? RecencyBonus : 0f))
                .FirstOrDefault();
            if (ev != null)
            {
                float sal = Mathf.Clamp01(Base(ev) + (nowAbs - ev.absTick < FreshEventTicks ? RecencyBonus : 0f));
                var point = PointFromMemory(ev, sal, RegisterFor(ev, mood), nowTick, self);
                if (agenda.Add(point)) trace?.Add($"event \"{Short(ev.summary)}\" {point.register} {sal:F2}");
            }

            // Rule 2 — Burden: the weightiest thing on them, always deep. Dedupes against the event above.
            var burden = core.memories
                .Where(m => m != null && !string.IsNullOrEmpty(m.summary) && m.weight >= BurdenMinWeight)
                .Where(m => !Known(agenda, MemId(m)))
                .OrderByDescending(m => m.isLongTerm).ThenByDescending(m => m.salience).ThenByDescending(m => m.weight)
                .FirstOrDefault();
            if (burden != null)
            {
                float sal = Mathf.Clamp(burden.weight * 0.9f, 0.3f, 1f);
                var point = PointFromMemory(burden, sal, PointRegister.DeepTalk, nowTick, self);
                if (agenda.Add(point)) trace?.Add($"burden \"{Short(burden.summary)}\" {sal:F2}");
            }
        }

        public static PointRegister RegisterFor(WeightedMemory m, float mood)
        {
            float b = Base(m);
            if (m.isLongTerm || b >= DeepThreshold) return PointRegister.DeepTalk;
            if (mood < LowMood && b >= LowMoodDeepWeight) return PointRegister.DeepTalk;
            return PointRegister.ChitChat;
        }

        public static bool IsSecret(WeightedMemory m, PointRegister register)
        {
            if (m.tags != null && m.tags.Any(t => SecretTags.Contains(t))) return true;
            return register == PointRegister.DeepTalk && Base(m) >= 0.85f;
        }

        private static TalkingPoint PointFromMemory(WeightedMemory m, float salience, PointRegister register, int nowTick, Pawn self)
        {
            var point = new TalkingPoint
            {
                subjectMemId = MemId(m),
                subjectSummary = m.summary,
                salience = salience,
                register = register,
                secret = IsSecret(m, register),
                formedTick = nowTick
            };
            // A memory about exactly one other pawn is not something you say TO that pawn.
            if (m.subjectPawnIds != null && m.subjectPawnIds.Count == 1)
            {
                var other = ResolveOnMap(self?.Map, m.subjectPawnIds[0]);
                if (other != null && other != self)
                {
                    point.audience = AudienceKind.Not;
                    point.audiencePawnId = other.ThingID;
                }
            }
            return point;
        }

        private static float Base(WeightedMemory m) => m.salience > 0f ? m.salience : m.weight;

        private static string MemId(WeightedMemory m)
        {
            m.EnsureMemId();
            return m.memId;
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        // Rule 3: Activity (folds #43)
        // ═════════════════════════════════════════════════════════════════════════════════════════

        public static void FormActivity(SynapseCorePawnComp core, SynapseConversationAgendaComp agenda, int nowTick, List<string> trace)
        {
            string job = RecentActivity(core);
            if (job == null) return;
            string key = "activity:" + job.Replace(' ', '_');
            if (Known(agenda, key)) return;
            var point = new TalkingPoint
            {
                subjectMemId = key,
                subjectSummary = $"the {job} they've been busy with",
                salience = ActivitySalience,
                register = PointRegister.ChitChat,
                formedTick = nowTick
            };
            if (agenda.Add(point)) trace?.Add($"activity \"{job}\"");
        }

        /// <summary>First non-mundane activity from Core's roll-up, duration stripped (ported from the resolver).</summary>
        public static string RecentActivity(SynapseCorePawnComp core)
        {
            string summary = core?.GetRecentJobsSummary();
            if (string.IsNullOrEmpty(summary)) return null;
            foreach (var rawPart in summary.Split(','))
            {
                string job = System.Text.RegularExpressions.Regex.Replace(rawPart, @"\s*\([^)]*\)", "").Trim();
                if (string.IsNullOrEmpty(job)) continue;
                string lower = job.ToLowerInvariant();
                if (MundaneActivities.Any(m => lower == m || lower.StartsWith(m + " "))) continue;
                return lower;
            }
            return null;
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        // Rule 4: Bond shift (Psychology socialNetwork)
        // ═════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>A notable TRUST change toward a third pawn since the last baseline → a point about them,
        /// audience-restricted so it is never said TO them. First sighting only records the baseline.
        /// Keyed on trust alone: it's the "do they rely on each other" axis and is present on Psychology 0.10.
        /// (The warmth "do they like each other" axis is the relationship-compass work #72, not yet in 0.10 —
        /// when it lands, add |Δwarmth| back here and restore the warmed-to/cooled-on directions.)</summary>
        public static void FormBondShifts(Pawn pawn, SynapseConversationAgendaComp agenda, int nowTick, List<string> trace)
        {
            var psych = pawn.TryGetComp<RimSynapse.Psychology.Comps.SynapsePawnComp>();
            if (psych?.socialNetwork == null || psych.socialNetwork.Count == 0) return;

            foreach (var kv in psych.socialNetwork)
            {
                string otherId = kv.Key;
                var rec = kv.Value;
                if (rec == null || string.IsNullOrEmpty(otherId)) continue;

                if (!agenda.bondTrust.TryGetValue(otherId, out float baseTrust))
                {
                    agenda.bondTrust[otherId] = rec.trust;
                    continue;
                }

                float dT = rec.trust - baseTrust;
                float magnitude = Mathf.Abs(dT);
                if (magnitude < BondShiftThreshold) continue;

                // Re-baseline whether or not the point survives the cap, so one shift yields one point.
                agenda.bondTrust[otherId] = rec.trust;

                var other = ResolveOnMap(pawn.Map, otherId);
                if (other == null) continue; // can't scope the audience → don't risk saying it to their face
                string key = "bond:" + other.ThingID;
                if (Known(agenda, key)) continue;

                string direction = dT > 0 ? "come to rely on" : "stopped trusting";
                var point = new TalkingPoint
                {
                    subjectMemId = key,
                    subjectSummary = $"how they've {direction} {other.LabelShort} lately",
                    salience = Mathf.Clamp(magnitude / 50f, 0.2f, 0.9f),
                    register = magnitude >= BondShiftDeep ? PointRegister.DeepTalk : PointRegister.ChitChat,
                    audience = AudienceKind.Not,
                    audiencePawnId = other.ThingID,
                    formedTick = nowTick
                };
                if (agenda.Add(point)) trace?.Add($"bond {direction} {other.LabelShort} ({magnitude:F0})");
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        // Rule 5: Link (outsider pairs; folds #52 / #53)
        // ═════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>For the nearest pawn of a role this pawn's role has openers for, seed one authored link
        /// point aimed only at them. At most one per pass; the cap will displace it if real points exist.</summary>
        public static void FormLinks(Pawn pawn, LinkRole role, SynapseConversationAgendaComp agenda, int nowTick, IReadOnlyList<Pawn> nearby, List<string> trace)
        {
            if (role == LinkRole.None || nearby == null || pawn.Map == null) return;
            float r2 = LinkRadius * LinkRadius;
            Pawn best = null; float bestD = float.MaxValue; ConversationLinkDef bestDef = null;

            for (int i = 0; i < nearby.Count; i++)
            {
                var other = nearby[i];
                if (other == null || other == pawn || other.Map != pawn.Map || !other.Spawned) continue;
                float d = (other.Position - pawn.Position).LengthHorizontalSquared;
                if (d > r2 || d >= bestD) continue;
                LinkRole theirs = LinkBank.RoleOf(other);
                if (!LinkBank.IsLinkPair(role, theirs)) continue;
                var def = LinkBank.FindDef(role, theirs);
                if (def == null) continue;
                best = other; bestD = d; bestDef = def;
            }
            if (best == null) return;

            string key = $"link:{bestDef.defName}:{best.ThingID}";
            if (Known(agenda, key)) return;
            string line = LinkBank.PickLine(bestDef);
            if (line == null) return;
            var point = new TalkingPoint
            {
                subjectMemId = key,
                subjectSummary = line,
                salience = LinkSalience,
                register = PointRegister.ChitChat,
                audience = AudienceKind.Only,
                audiencePawnId = best.ThingID,
                formedTick = nowTick
            };
            if (agenda.Add(point)) trace?.Add($"link {role}->{LinkBank.RoleOf(best)} to {best.LabelShort}: \"{line}\"");
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        // Rule 6: Colony rumor (outsiders, at arrival)
        // ═════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Once per visit: 1–2 Heard points randomised from Core's world-level event ledger. No
        /// source pawn — the road told them. Capped so they don't arrive as a newspaper.</summary>
        public static int SeedColonyRumors(Pawn pawn, SynapseConversationAgendaComp agenda, int nowTick)
        {
            if (agenda == null || agenda.rumorsSeeded) return 0;
            agenda.rumorsSeeded = true;

            var wc = Find.World?.GetComponent<RimSynapse.SynapseCoreWorldComponent>();
            var events = wc?.shortTermEvents;
            if (events == null || events.Count == 0) return 0;

            var pool = events
                .Where(e => e != null && !string.IsNullOrEmpty(e.description))
                .OrderByDescending(e => e.eventType == ShortTermEventType.GlobalEvent) // what the road would carry
                .ThenByDescending(e => e.gameTick)
                .Take(10)
                .ToList();
            int added = 0;
            while (pool.Count > 0 && added < MaxRumorsPerVisitor)
            {
                var ev = pool.RandomElement();
                pool.Remove(ev);
                string key = $"rumor:{ev.gameTick}:{(ev.description.GetHashCode() & 0x7fffffff):x}";
                if (Known(agenda, key)) continue;
                var point = new TalkingPoint
                {
                    subjectMemId = key,
                    subjectSummary = "something they heard on the road about this place: " + ev.description,
                    salience = RumorSalience,
                    register = PointRegister.ChitChat,
                    provenance = PointProvenance.Heard,
                    sourcePawnId = null,
                    formedTick = nowTick
                };
                if (agenda.Add(point)) added++;
            }
            return added;
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        // Rule 8: Ambient (last resort)
        // ═════════════════════════════════════════════════════════════════════════════════════════

        public static void FormAmbient(Pawn pawn, SynapseConversationAgendaComp agenda, int nowTick, List<string> trace)
        {
            var weather = pawn.Map?.weatherManager?.curWeather;
            string key = "ambient:" + (weather?.defName ?? "nothing");
            if (Known(agenda, key)) return;
            var point = new TalkingPoint
            {
                subjectMemId = key,
                subjectSummary = weather != null ? $"the {weather.label.ToLowerInvariant()} outside" : "nothing much in particular",
                salience = AmbientSalience,
                register = PointRegister.ChitChat,
                formedTick = nowTick
            };
            if (agenda.Add(point)) trace?.Add("ambient");
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════════════════════════

        private static bool Known(SynapseConversationAgendaComp agenda, string subjectId)
            => agenda.HasSubject(subjectId) || agenda.IsRetired(subjectId);

        /// <summary>Find a spawned pawn by either id scheme in use across the suite (ThingID or unique load id).</summary>
        public static Pawn ResolveOnMap(Map map, string id)
        {
            if (map == null || string.IsNullOrEmpty(id)) return null;
            var all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p.ThingID == id || p.GetUniqueLoadID() == id) return p;
            }
            return null;
        }

        private static string Short(string s) => s == null ? "" : (s.Length <= 40 ? s : s.Substring(0, 37) + "...");
    }
}
