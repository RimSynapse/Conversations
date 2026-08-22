# RimSynapse - Conversations: Player's Guide

RimSynapse - Conversations makes your colonists actually talk. Instead of the vanilla speech-bubble icons, your pawns hold real, written-out conversations with each other — shaped by who they are, what just happened to them, and how they feel about the person they are talking to. Every line is written on the fly by a Large Language Model through RimSynapse Core, so no two colonies sound the same. This guide covers what you will see and hear in game, how each feature behaves, and every setting you can adjust. It is for players; there is a separate guide for modders and developers.

## Dynamic pawn-to-pawn conversations

Whenever two colonists socialise in vanilla RimWorld, this mod quietly takes over that moment and turns it into a spoken conversation. The pawns still socialise on the game's normal schedule — you do not trigger anything manually — but instead of a floating icon, one of them says an actual line out loud and the other answers.

Each conversation is written by an AI, and it is grounded in the real state of the two pawns at that moment. The mod feeds the writer who these colonists are (their traits and personality), how they currently feel (mood and recent thoughts), what they have been doing, and their opinion of each other. If you also run RimSynapse - Psychology, their deeper traits — trust, familiarity, temperament, and personality type — are woven in as well, so a wary introvert and a warm extravert sound genuinely different.

Not every social attempt becomes a full conversation. A colonist who is introverted, in a low mood, or dislikes the other pawn is less likely to start talking or to answer back. Colonists who like each other talk more readily. When a pawn declines to engage, they simply stay quiet — no line is spoken — and the would-be speaker picks up a small "was ignored" feeling, or occasionally respects the other's quietness instead.

## What colonists talk about

In version 0.9 the mod stops colonists from trading generic small talk and instead has them talk about concrete, real things happening in your colony. Before any line is written, the mod picks exactly one specific thing for the conversation to be about, plus each speaker's angle on it, and only then asks the AI to phrase it. This is why colonists now sound like they are living in your colony rather than reading from a script.

What gets chosen depends on the pawn's real situation, roughly in this order of preference:

- A concrete event the colonist actually lived through recently — a raid, a hunt gone wrong, a death, a wedding, a mad animal. These are the richest topics. Deep talks almost always reach for one; casual chats often do.
- If it is a deep talk and there is no standout event, the heaviest thing quietly weighing on that colonist — a long-held memory or burden.
- Otherwise, something concrete from their day right now: the work they have been busy with, or a pressing need such as being hungry, exhausted, in pain, or in a dark mood. If nothing is pressing, they fall back to an observation like the weather.

Crucially, a colonist only speaks first-hand about events they were actually part of. If one pawn lived through something the other did not, the conversation is framed as one of them recounting it and the other hearing it fresh and reacting — a colonist will not claim a memory that is not theirs. When both were present, they reminisce together.

The recipient's reaction is coloured by how they feel about the speaker: warmly if they like them, curtly if they do not, and shaded by their own current state. The mod also avoids repeating a pair's most recent topics, so the same two colonists do not rehash the same ordeal back-to-back.

## How conversations play out on screen

A conversation is written as a whole back-and-forth in a single step, then delivered line by line. The first line appears immediately above the speaker's head; the remaining lines follow one at a time, about every four seconds, as long as the two pawns stay close together (within roughly eight tiles). If they wander apart, the conversation simply ends there — as it would in real life. Exchanges run anywhere from a single remark to a dozen lines, sized to the situation.

Each spoken line is also saved to the pair's chat history and planted as a small memory for both participants, so conversations leave a trace you can read back later.

## Speech bubbles

Spoken lines appear as small speech bubbles above the speaker, with a little pointer aimed at their head and a slight sideways lean toward whoever they are talking to. By design, bubbles are only drawn while the game is running at normal (1x) speed. If you speed the game up, the bubbles disappear and the vanilla interaction icons stay hidden too — the mod assumes you are fast-forwarding and would not be reading dialogue anyway. Slow back to 1x and conversations become visible again. The conversations themselves still happen at higher speeds and still affect relationships; you just do not see the text.

You can restyle the bubble background in the mod settings — its red, green, and blue tint and its transparency — to make it darker, lighter, or more see-through to taste.

## Deep talks versus chit-chat

The mod distinguishes the two kinds of vanilla social interaction and treats them differently.

Chit-chat is light and casual: everyday chatter about the day, a chore, a need, a bit of gossip, or a passing observation. It builds familiarity steadily and nudges opinion a little.

Deep talks are heartfelt and go for weightier ground: a defining event, a grief, a fear, a belief, a regret. They reach for the most significant thing a colonist is carrying, are written in a more raw and open tone, and bond the two pawns more strongly than casual chatter does — opening up to someone draws them closer.

You can turn individual conversation topics on or off in the settings, and each topic is tagged as either Deep Talk or Chitchat so you know which kind you are enabling or silencing.

## How conversations shape relationships

Every conversation has a social aftermath, and in 0.9 that outcome is worked out consistently by the mod rather than guessed by the AI. Based on who is talking and how the conversation went, a colonist's trust, familiarity, and warmth toward the other shift by a small amount.

Friendly conversations between colonists who already like each other build the most goodwill; a warm heart-to-heart can leave a genuine mood lift. Talks between colonists who dislike each other build far less, and can even cool the relationship. Coercive exchanges (see warden conversations below) never warm anyone up — they erode trust and leave resentment. These are the same modest, steady shifts vanilla social interactions already produce, so relationships evolve naturally over time rather than swinging wildly from a single chat.

Colonists nearby can also overhear. The closest other colonist within earshot picks up what was said as a faint "overheard" memory. Earshot reaches about eight tiles but shrinks with noise — nearby mining, plant-cutting, deconstructing, harvesting, repairing, combat, or a running generator each cut the range down. Walls block sound entirely, so a conversation in one room is not overheard from another unless both pawns are outdoors.

## Warden conversations

When Ideology-optional warden work happens, it too plays out as a spoken exchange. If a warden reduces a prisoner's resistance or attempts recruitment, tries to convert them to your colony's ideoligion, breaks their will toward enslavement, or suppresses a slave, you will now hear that scene rather than just watch a number tick.

These exchanges are grounded in the specific prisoner's real situation — how much resistance and will they have left, how comfortable their captivity is, how they were captured, and, for conversion, the actual clash between their beliefs and your colony's ideoligion. A recruitment plays as a negotiation where the prisoner names the concrete things that would actually win them over. Enslavement and suppression are cold and coercive — threats and reminders of who holds power, not friendly chat.

The tone carries into the aftermath: a persuasive recruitment or conversion can build a little rapport, while coercive enslavement and suppression never do — they leave resentment instead.

Important: warden conversations are flavour only. They never change the vanilla recruitment, conversion, resistance, will, or certainty rolls. Whether a prisoner is recruited or converted is decided entirely by vanilla RimWorld exactly as it always was; the conversation just gives that outcome a voice.

Recruitment and resistance-reduction exchanges work in the base game. Conversion, enslavement, and suppression are Ideology DLC activities, so those three only occur if you have Ideology installed; without it they simply never come up.

You can switch warden conversations off entirely, and set how often they occur, in the settings.

## Load-adaptive conversations

Conversations are the lowest-priority thing the AI does, and in 0.9 the mod protects your game's smoothness by backing off automatically when the AI cannot keep up. A big colony with lots of chatter, a slower AI model, or a high game speed all show up the same way: a growing backlog of AI requests. When that backlog gets too deep, or Core is already throttling the AI to keep up with more important work (like the storyteller), conversations quietly stop being written.

When this happens you lose nothing you would have seen — remember, bubbles only show at 1x anyway — and relationships keep evolving normally, because the trust, familiarity, and opinion shifts are still applied even when no dialogue is written. In other words, the social life of your colony continues; you just are not billed for text you could not have watched. As soon as the backlog clears, full spoken conversations resume on their own.

Two settings control this behaviour: an on/off switch for adaptive shedding (on by default), and how deep the AI backlog is allowed to get before conversations start yielding.

## Environmental chit-chat triggers

Beyond ordinary socialising, talkative colonists sometimes comment out loud on their surroundings. Two situations trigger this:

- Walking into darkness — stepping from a lit area into a dim, shadowy one prompts a remark about the gloom.
- Walking into the freezer — stepping into freezing cold prompts a complaint about the chill or the frozen food.

Only naturally chatty colonists do this: those with a high Social skill, the Kind trait, or (with RimSynapse - Psychology) a high extraversion. There needs to be another colonist within a few tiles to talk to, and each colonist can only set off an environmental comment about once every four in-game hours, so it stays occasional rather than constant.

## The Dialogue History viewer

Every colonist carries a chat log you can open. Select a colonist and click the Dialogue History button on their inspect pane to open a messaging-style window. (Non-colony pawns who count as residents have the same button.)

The window has two tabs:

- Conversations — a contacts list down the left of everyone this colonist has recently talked with, and a chat feed on the right laid out like a messaging app, with each speaker's portrait, name, timestamps, and day separators. Click a contact to read that thread. Warden exchanges show up here too.
- Overheard — everything this colonist overheard other people say nearby, with who said it and to whom.

How far back the history goes is tied to Core's chat-history retention setting; older messages naturally fade out beyond that window.

## Storyteller chat (moved to Core)

If you are looking for the window where you chat directly with your AI storyteller, it is no longer part of Conversations. As of 0.9 the player-to-storyteller chat window and its toolbar button live in RimSynapse - Core instead. Conversations now handles only pawn-to-pawn dialogue. Nothing is lost — the storyteller chat is simply reached through Core now, where it can respond to whichever RimSynapse storyteller you are actually running.

## Settings reference

Open the settings under Mod Settings → "RimSynapse - Conversations". The following options are available:

- Chat History Retention — how many in-game hours of pawn chat history is kept before it fades (6 to 168 hours). This is shared with Core and also governs how far back the Dialogue History viewer reaches.
- AI Conversation Cooldown — the minimum in-game time before the same pair of colonists will generate another AI conversation (0 to 12 hours; default 1 hour). Vanilla pawns try to socialise constantly, so this throttle keeps the chatter — and the small memories each chat plants — at a sane pace. Set it to 0 to get a conversation on every single vanilla interaction.
- Warden conversations (recruit / convert / enslave / suppress) — turns warden work into spoken exchanges. On by default. Purely flavour; never changes the vanilla roll.
- Warden Conversation Cooldown — the minimum in-game time before the same warden and prisoner will generate another warden conversation (0 to 12 hours; default 2 hours). Warden work repeats very often, so this keeps the exchanges occasional. Only shown when warden conversations are enabled.
- Speech Bubble Aesthetics — sliders for the bubble background's red, green, and blue tint, plus its transparency (alpha), letting you darken, lighten, or make the bubble more see-through.
- Adjust Chat Topics — a checklist of every conversation topic, each labelled Deep Talk or Chitchat. Untick a topic to stop colonists ever bringing it up. Useful if you want to avoid heavy themes like grief, trauma, or mortality, or trim the small-talk topics you find repetitive.
- Experimental: Pre-seed conversations — an off-by-default experimental option that pre-writes conversations in the background and serves them instantly. It is off because pre-written lines can reference stale context (the weather, or a pawn who has since gone indoors). With it off, every conversation is written fresh and current.
- Open Encyclopedia — a button that opens the in-game RimSynapse reference window.

A few behaviours run on sensible defaults and are not exposed as on-screen sliders, but are worth knowing about:

- Adaptive conversation shedding — on by default. Lets conversations quietly stop generating when the AI is overloaded, while relationships keep evolving (see Load-adaptive conversations above).
- Conversation backlog limit — how deep the AI's request backlog may get before conversations start yielding to more important work. Lower means conversations back off sooner and cost less under load; higher keeps them generating longer.
- Event retelling — on by default. Lets a colonist's recent significant events be retold, uniquely, to different colonists, so a single dramatic incident ripples across the colony as several different conversations rather than one.

## Requirements and compatibility

RimSynapse - Conversations is a companion mod and requires RimSynapse - Core, which must be loaded before it. It needs Core v0.9.0 to match this version. Conversations also needs a working AI backend configured in Core (that is where you connect and manage the AI model that writes the dialogue). It supports RimWorld 1.5 and 1.6.

Everything else is optional:

- RimSynapse - Psychology (optional) — when present, colonists naturally bring their deeper traits, core memories, traumas, and trust levels into everyday conversations, and their personality type influences how readily they talk.
- Royalty DLC (optional) — enables noble tone and psycast flavour.
- Ideology DLC (optional) — required specifically for the convert, enslave, and suppress warden conversations, and enables faith debates and proselytising flavour. Recruitment conversations work without it.
- Biotech DLC (optional) — enables xenotype and mechanitor flavour.
- Anomaly DLC (optional) — enables paranoid void flavour.

None of the DLCs are required to use the mod; each simply unlocks extra flavour when installed. Saves and settings carry over unchanged from earlier versions.
