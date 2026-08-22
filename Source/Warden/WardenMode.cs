namespace RimSynapse.Conversations.Warden
{
    /// <summary>
    /// The four warden activities that Conversations#42 upgrades into spoken exchanges. Recruitment
    /// covers both "reduce resistance" and "attempt recruit" (vanilla routes both through
    /// InteractionWorker_RecruitAttempt). Conversion, enslavement and suppression are Ideology-DLC
    /// activities — their vanilla interactions never fire without the DLC, so those modes degrade to
    /// silence on their own.
    /// </summary>
    public enum WardenMode
    {
        Recruitment,
        Conversion,
        Enslavement,
        Suppression
    }

    public static class WardenModeExtensions
    {
        /// <summary>Coercive modes crush rather than persuade: they must never plant warm familiarity
        /// (issue #42 social-effects requirement). Persuasive modes may build a little rapport.</summary>
        public static bool IsCoercive(this WardenMode mode)
            => mode == WardenMode.Enslavement || mode == WardenMode.Suppression;

        /// <summary>Short label used in memory summaries and the conversation-history topic tag.</summary>
        public static string Label(this WardenMode mode)
        {
            switch (mode)
            {
                case WardenMode.Recruitment: return "Recruitment";
                case WardenMode.Conversion:  return "Conversion";
                case WardenMode.Enslavement: return "Enslavement";
                case WardenMode.Suppression: return "Suppression";
                default:                     return "Warden";
            }
        }
    }
}
