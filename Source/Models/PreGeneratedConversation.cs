using System.Collections.Generic;
using Verse;

namespace RimSynapse.Conversations
{
    public class PreGeneratedConversation
    {
        public string initiatorId;
        public string recipientId;
        public string initiatorStatement;
        public string recipientResponse;
        public string topicDefName;
        public bool isContinuation;
        public int generatedAtTick;
        public float trustOffset;
        public float familiarityOffset;
        public float affinityOffset;
    }

    public static class PreGeneratedConversationCache
    {
        private static readonly Dictionary<string, PreGeneratedConversation> cache = new Dictionary<string, PreGeneratedConversation>();

        private static string GetKey(string idA, string idB)
        {
            return idA.CompareTo(idB) < 0 ? $"{idA}_{idB}" : $"{idB}_{idA}";
        }

        public static void Store(string idA, string idB, PreGeneratedConversation conv)
        {
            if (string.IsNullOrEmpty(idA) || string.IsNullOrEmpty(idB) || conv == null) return;
            cache[GetKey(idA, idB)] = conv;
        }

        public static PreGeneratedConversation Pop(string idA, string idB)
        {
            if (string.IsNullOrEmpty(idA) || string.IsNullOrEmpty(idB)) return null;
            string key = GetKey(idA, idB);
            if (cache.TryGetValue(key, out var conv))
            {
                cache.Remove(key);
                return conv;
            }
            return null;
        }

        public static bool Has(string idA, string idB)
        {
            if (string.IsNullOrEmpty(idA) || string.IsNullOrEmpty(idB)) return false;
            return cache.ContainsKey(GetKey(idA, idB));
        }

        public static void Clear()
        {
            cache.Clear();
        }
    }
}
