using System.Collections.Generic;
using Verse;

namespace RimSynapse.Conversations
{
    public class ChatTopicDef : Def
    {
        public string topicName;
        public List<string> prompts;
        public bool isDeepTalk;
        public bool isChitchat;

        /// <summary>
        /// Live-data hooks resolved into prompt text when this topic is chosen (see
        /// <c>ConversationContextResolver</c>). Empty = a pure prompt-flavor topic. Known keys:
        /// <c>ownedRoom</c>, <c>apparel</c>, <c>health</c>, <c>bondedAnimal</c>, <c>food</c>,
        /// <c>memoriesToday</c>, <c>memoriesLongTerm</c>, <c>griefMemories</c>, <c>traumaMemories</c>,
        /// <c>recipientRelationship</c>, <c>personalitySummary</c>, <c>residency</c>.
        /// Unknown keys are ignored, so modders can add topics without a code change.
        /// </summary>
        public List<string> contextKeys = new List<string>();
    }
}
