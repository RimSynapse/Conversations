using System.Collections.Generic;
using Verse;

namespace RimSynapse.Conversations
{
    /// <summary>Which kind of outsider a line belongs to. Outsiders are pawns the colony has NO standing
    /// relationship with (Core.MayConverse == false), so they never join the LLM conversation system — they
    /// get cheap authored barks instead (#52).</summary>
    public enum OutsiderRole
    {
        /// <summary>A hostile faction's leader, personally on the field — staging and command.</summary>
        RaidLeader,
        /// <summary>Rank-and-file attackers — impatience, greed, ideological disdain for the colony.</summary>
        RaidMember,
        /// <summary>A caravan trader or their guards — here to deal, softer rhetoric.</summary>
        Trader,
        /// <summary>A non-hostile, non-trading visitor passing through.</summary>
        Visitor
    }

    /// <summary>
    /// Authored ambient lines for an OUTSIDER (#52) — the cheap, deterministic, no-LLM half of the
    /// conversation system for pawns that never converse with the colony (raiders, passing traders,
    /// unaffiliated visitors). Matched by <see cref="role"/>, optionally narrowed to a faction
    /// (<see cref="factionDef"/>) and/or an ideology meme (<see cref="ideoMeme"/>). The most specific
    /// matching def wins; a role-only def is the generic fallback. LLM-generated flavor is layered on
    /// separately at world-gen (hybrid); this def is the always-present baseline.
    /// </summary>
    public class OutsiderChatterDef : Def
    {
        public OutsiderRole role;

        /// <summary>Optional: restrict to this faction (defName). Null/empty = any faction of the role.</summary>
        public string factionDef;

        /// <summary>Optional: restrict to outsiders whose ideoligion carries this meme (defName). Null/empty =
        /// any. Ignored without the Ideology DLC.</summary>
        public string ideoMeme;

        /// <summary>The authored barks. One is picked per utterance.</summary>
        public List<string> lines = new List<string>();
    }
}
