using System.Collections.Generic;
using RimSynapse.Conversations.Generation;
using RimAgentic.Testing;

namespace RimSynapse.Conversations.Tests
{
    /// <summary>
    /// Pure-composer cases (the Prompt Lab prerequisite refactor): assert that <see cref="ThinPromptComposer"/>
    /// and <see cref="IdentityComposer"/> — the Verse-free slice the game-free lab links — produce the expected
    /// prompt text. Deterministic: no pawns, no game state, just primitives in / strings out. These guard the
    /// #44 register behaviour and the beat framing/tone/length rules against silent drift.
    /// </summary>
    [SynapseTestSet]
    public static class PromptComposerCases
    {
        private static ConversationBeat Beat(string subject = "the raid last night", BeatTone tone = BeatTone.Casual,
            BeatFraming framing = BeatFraming.Shared, bool isDeep = false)
        {
            return new ConversationBeat
            {
                subject = subject,
                initiatorStance = "recounting how it went",
                recipientStance = "reacting to it",
                tone = tone,
                framing = framing,
                isDeep = isDeep
            };
        }

        public static IEnumerable<SynapseTestCase> All()
        {
            // A voiced pawn's identity wins outright — verbatim, prefixed "speaks like this:".
            yield return new SynapseTestCase("PromptComposer_VoicedIdentityWins", () =>
            {
                string id = IdentityComposer.Identity("blunt and dry, few words", "Nomad", new List<string> { "Kind" });
                Assert.True(id.Contains("speaks like this: blunt and dry, few words"), "voiceProfile is used verbatim");
                Assert.False(id.Contains("ordinary colonist"), "voiced pawn never falls back");
                return id;
            });

            // Voiceless-with-backstory: anchored to backstory (+traits) and steered to plain speech (#44).
            yield return new SynapseTestCase("PromptComposer_VoicelessAnchorsAndSteers", () =>
            {
                string id = IdentityComposer.Identity(null, "Nomad wanderer", new List<string> { "Kind", "Tough" });
                Assert.True(id.Contains("Nomad wanderer"), "backstory anchors the voiceless pawn");
                Assert.True(id.Contains("Kind, Tough"), "up to two traits are included");
                Assert.True(id.Contains("plain everyday words"), "voiceless pawns are steered to plain speech (#44)");
                Assert.True(id.Contains("not clinical or technical"), "the anti-technobabble steer is present (#44)");
                return id;
            });

            // Voiceless-bare: nothing to go on -> "an ordinary colonist".
            yield return new SynapseTestCase("PromptComposer_VoicelessBareFallsBack", () =>
            {
                string id = IdentityComposer.Identity(null, null, null);
                Assert.True(id.Contains("an ordinary colonist"), "bare voiceless pawn falls back to ordinary colonist");
                return id;
            });

            // Compose: shared framing adds no "hearing it fresh" note; InitiatorTells does (Bill/Lipos guard).
            yield return new SynapseTestCase("PromptComposer_FramingNote", () =>
            {
                var shared = ThinPromptComposer.Compose("Ana", "Bex", "id-a", "id-b", Beat(framing: BeatFraming.Shared), null);
                var tells = ThinPromptComposer.Compose("Ana", "Bex", "id-a", "id-b", Beat(framing: BeatFraming.InitiatorTells), null);
                Assert.False(shared.user.Contains("hearing this for the first time"), "shared beats carry no fresh-hearing note");
                Assert.True(tells.user.Contains("Bex was NOT there"), "InitiatorTells names the recipient as absent");
                Assert.True(tells.user.Contains("hearing this for the first time"), "InitiatorTells adds the fresh-hearing note");
                return "framing note gated on framing";
            });

            // Compose: tone + depth drive the system rules; user opens with the initiator and carries the beat.
            yield return new SynapseTestCase("PromptComposer_ToneDepthAndUserShape", () =>
            {
                var casual = ThinPromptComposer.Compose("Ana", "Bex", "id-a", "id-b", Beat(), null);
                Assert.True(casual.system.Contains("The mood is easy, everyday."), "casual tone rule");
                Assert.True(casual.system.Contains("2 to 4 short lines"), "chit-chat length rule");

                var deepCoercive = ThinPromptComposer.Compose("Ana", "Bex", "id-a", "id-b",
                    Beat(tone: BeatTone.Coercive, isDeep: true), null);
                Assert.True(deepCoercive.system.Contains("cold and controlling"), "coercive tone rule");
                Assert.True(deepCoercive.system.Contains("4 to 8 lines"), "deep length rule");

                Assert.True(casual.user.StartsWith("Ana — id-a."), "user opens with the initiator's name + identity");
                Assert.True(casual.user.Contains("What it's about: the raid last night."), "the concrete subject is carried");
                Assert.True(casual.user.Contains("STARTING with Ana"), "the exchange is told to start with the initiator");
                return "tone/depth/user shape ok";
            });

            // Compose: continuation history is appended only when present.
            yield return new SynapseTestCase("PromptComposer_ContinuationAppends", () =>
            {
                var none = ThinPromptComposer.Compose("Ana", "Bex", "id-a", "id-b", Beat(), null);
                var cont = ThinPromptComposer.Compose("Ana", "Bex", "id-a", "id-b", Beat(), "Ana: hey\nBex: hi");
                Assert.False(none.user.Contains("continue naturally"), "no history -> no continuation note");
                Assert.True(cont.user.Contains("continue naturally"), "history -> continuation note appended");
                Assert.True(cont.user.Contains("Ana: hey"), "the prior lines are included");
                return "continuation gated on history";
            });
        }
    }
}
