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
    }
}
