# Inspirations: Game MCP Architecture for RimSynapse Chat

This document outlines the refactoring guidelines for **RimSynapse Chat** using the Model Context Protocol (MCP).

---

## 1. What Stays the Same
- **UI & Controls**: In-game windows, scroll boxes, input fields, and character portraits rendering.
- **Audio Output**: Voice reproduction and playback pipelines remain in C# to ensure low-latency audio delivery.

---

## 2. What Changes (The MCP Shift)
- **Decoupled Conversations**: Instead of packaging complex local logs inside C# models and sending them in simple text requests, register a tool allowing the conversational LLM to query chat logs.
- **Dynamic Dialogue Options**: Expose dialogue execution triggers as tools.

---

## 3. Proposed MCP Tools for Chat
- `get_chat_history`: Takes a colonist name and returns a list of recent dialogue exchanges between the player and that colonist.
- `get_colonist_interests`: Queries what a colonist likes, their relationships, or what topics they are willing to talk about.

---

## 4. LLM Narrative Workflow
1. The player clicks John and opens the chat panel.
2. The Chat LLM queries `get_chat_history("John")` and `get_colonist_interests("John")`.
3. It learns John was insulted recently and likes *Sculpting*.
4. The Chat LLM responds as John in character, saying: *"I don't really want to talk, but I saw that new sculpture you made..."*
