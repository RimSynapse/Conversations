using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RimSynapse.Conversations.Generation
{
    /// <summary>
    /// The PURE, Verse-free port of the lenient dialogue-response parsing the game applies to a model reply
    /// (see <c>Patch_Pawn_InteractionsTracker_TryInteractWith.ParseLinesLenient/ExtractJson/CleanLine</c> in
    /// InteractionDialogue.cs). Small local models routinely emit malformed JSON — a stray extra bracket, a
    /// missing closing brace, trailing prose, a name/quote prefix on a line — so the game only needs the
    /// <c>lines</c> array and cleans each line. The game-free Prompt Lab links this file so its parsing
    /// matches the game, not just the prompt.
    ///
    /// Kept behaviourally identical to the game copy on purpose. If the game copy changes, update this too
    /// (the two should be single-sourced onto this file in a follow-up).
    /// </summary>
    public static class LenientDialogueParser
    {
        private class LlmExchangeResponse
        {
            public List<string> lines { get; set; }
        }

        /// <summary>Narrow a reply to the outermost <c>{ ... }</c>, tolerating leading/trailing prose.</summary>
        public static string ExtractJson(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;

            int firstBrace = content.IndexOf('{');
            int lastBrace = content.LastIndexOf('}');

            if (firstBrace != -1 && lastBrace != -1 && lastBrace > firstBrace)
                return content.Substring(firstBrace, lastBrace - firstBrace + 1);

            return content;
        }

        /// <summary>Pull the <c>lines</c> array out of the model's reply, tolerating the malformed JSON small
        /// models routinely emit. Returns null if nothing usable is found.</summary>
        public static List<string> ParseLinesLenient(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;

            // 1) The clean path: a well-formed object.
            try
            {
                var ex = JsonConvert.DeserializeObject<LlmExchangeResponse>(ExtractJson(content));
                if (ex?.lines != null && ex.lines.Count > 0) return ex.lines;
            }
            catch { /* fall through to lenient extraction */ }

            // 2) Grab just the "lines" array, up to its FIRST closing bracket (so "]]" or trailing junk is
            //    ignored). Repair a truncated close by appending one.
            int li = content.IndexOf("\"lines\"", StringComparison.OrdinalIgnoreCase);
            if (li < 0) return null;
            int lb = content.IndexOf('[', li);
            if (lb < 0) return null;
            int rb = content.IndexOf(']', lb);
            string arr = rb > lb ? content.Substring(lb, rb - lb + 1) : content.Substring(lb) + "]";
            try
            {
                var list = JsonConvert.DeserializeObject<List<string>>(arr);
                if (list != null && list.Count > 0) return list;
            }
            catch { /* give up — caller falls back */ }
            return null;
        }

        /// <summary>Small local models often ignore "no names/labels" and prefix a line with the speaker's
        /// name or wrap it in quotes. Strip those so only the spoken words remain.</summary>
        public static string CleanLine(string line, string nameA, string nameB)
        {
            if (line == null) return null;
            string s = line.Trim();
            if (s.Length >= 2 && s.StartsWith("- ")) s = s.Substring(2).Trim();
            // Unwrap a fully-quoted line.
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '“') && (s[s.Length - 1] == '"' || s[s.Length - 1] == '”'))
                s = s.Substring(1, s.Length - 2).Trim();
            // Strip a leading "Name", "Name:", or "Name," for either speaker.
            foreach (var name in new[] { nameA, nameB })
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (s.StartsWith(name, StringComparison.OrdinalIgnoreCase) && s.Length > name.Length)
                {
                    char sep = s[name.Length];
                    if (sep == ':' || sep == ',' || sep == ' ' || sep == '-')
                        s = s.Substring(name.Length).TrimStart(':', ',', ' ', '-').Trim();
                }
            }
            return s;
        }
    }
}
