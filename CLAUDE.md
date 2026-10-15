# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is
A .NET 10 console app (proof of concept) that drives a **web AI chatbot in Microsoft Edge via Playwright** and turns it into a local coding agent.
- At startup it sends a persona prompt describing a JSON protocol and a set of "local commands".
- For each task, the AI replies with JSON. The app runs the requested tool in a sandboxed workspace folder and sends the result back, repeating until the AI says `complete`.

It currently targets chatgpt.com (logged out). The real goal is a workplace chatbot, so **keep naming provider-neutral** (`AiChat*`, `AiChatUrl`), never "ChatGpt".

[AiAgent/docs/ProofOfConcept.md](AiAgent/docs/ProofOfConcept.md) is the design doc and record of verified test results. Update it whenever tools, the protocol or the config change.

## Commands
```
dotnet build AiAgent.slnx
dotnet run --project AiAgent          # opens Edge, sends the persona, then prompts "Task (blank to exit):"
```
- **Build fails with "file is locked by AiAgent (pid)":** a running agent holds `bin/Debug/net10.0/AiAgent.exe`. Close it, or compile-check elsewhere with `dotnet build AiAgent -o <other dir>`.
- **Config:** `AiAgent/Config/appsettings.json` and `AiAgent/Prompts/persona.md` are copied to the output folder on build. Edit the source copies, not the ones under `bin/`.
- **Machine-specific settings** (such as the real `Agent:WorkspacePath`) go in `AiAgent/Config/appsettings.local.json`:
  - It's git-ignored and loaded after `appsettings.json`, so its values override.
  - The committed `appsettings.json` keeps the placeholder `C:\path\to\workspace`.
  - Keep personal paths out of committed files.
- **Browser:** Playwright uses the **installed** Edge (`Channel = "msedge"`), so there is no `playwright install` step.
- **No test project.** Tools can be exercised without a browser by calling `ToolDispatcher.Execute(toolName, JsonElement args, AgentSettings)` directly. Its return value is the exact `tool_result` JSON the AI would receive. For approval or `ask_user` paths, feed answers with `Console.SetIn`.

## Architecture (the flow across files)
1. **`Program.cs`:**
   - Loads config.
   - Fills the `{{WORKSPACE}}` and `{{ALLOWED_COMMANDS}}` placeholders in `persona.md`.
   - Sends the persona once; the AI acknowledges with `{"action":"complete","message":"ready"}`.
   - Then loops over console tasks. `RunTaskAsync` sends `TASK: …` and gets a reply:
     - `action:"tool"`: the reply goes to `ToolDispatcher.Execute`, and the result JSON is sent back with a "(Current task: … continue)" reminder (`taskAnchor`). Without that reminder, the logged-out model treats the last tool result as the whole task.
     - `action:"complete"` / `"error"`: the task ends.
2. **`AiChatBrowser.cs`:**
   - `SendAsync`: fills the prompt box and clicks Send. It verifies the box emptied and re-clicks up to 3 times, because the page ignores clicks while it is still settling.
   - Waits until the count of `CompletedMessage` (assistant message with `data-message-complete`) goes up.
   - `ReadLastReplyAsync`: returns the last assistant message's `InnerText` once it has been stable for 1 second.
   - All site-specific selectors live in `Browser:Selectors` in config. Pointing at a different chat site should be a config change, not a code change.
3. **`AgentResponse.cs`:** `TryParse(reply, out result, out error)`.
   - Accepts a ```json fence, raw JSON, or text before the JSON.
   - Rendered code blocks lose their fence and gain "json" / "Copy code" labels, so it tries every `{` up to the last `}`.
   - `error` is fed back to the AI. In `Program.SendAndParseAsync`, a failed parse first **re-reads the page** (after a 2s delay, in case the reply was still rendering), then retries the AI up to 3 times.
   - The persona, the acknowledge line and `RetryPrompt` must all state the **same** format rule: exactly one JSON object, last in the reply, fence optional. Conflicting wording previously made the AI complain about contradictions.
4. **`Tools/`** (namespace `AiAgent.Tools`, `internal static` classes):
   - **`ToolDispatcher.Execute`** is the single entry point:
     1. Asks for approval when needed.
     2. Unpacks the JSON arguments (`GetString`, `RequireString`, `GetInt`, `GetBool`; tool methods only get plain values).
     3. Uses a `switch` to call the tool.
     4. Wraps the result as `{"tool_result":{"tool","ok","output"}}`.
   - **Every tool returns `ToolOutput`.** Use `ToolOutput.Success(text)`, and **throw** for "couldn't do it": the `catch` in `Execute` turns that into `ok:false` with the message, which the AI reads. So messages must tell the AI how to recover. `run_command` instead returns `new ToolOutput(exitCode == 0, output)`, so a failed build still carries its compiler errors.
   - **Classes ending in `Tools` hold AI-callable tools** (`FileTools`, `CommandTools`, `UserTools`). `WorkspaceGuard` is the shared helper.

### Adding or changing a tool: four places must agree
1. The `switch` in `ToolDispatcher.Execute`.
2. The `AllToolNames` string (used in the unknown-tool error).
3. `ToolsRequiringApproval`, for anything that changes files or runs programs. If the tool takes new argument names, add them to the list `AskForApproval` previews.
4. The **LOCAL COMMANDS** list in `Prompts/persona.md`. The AI only uses what's listed there.

## Safety boundaries (don't weaken these)
- **`WorkspaceGuard.ResolvePath`:** every path must resolve inside `Agent:WorkspacePath`. Paths outside it are rejected.
- **`WorkspaceGuard.ResolveSubfolder`:** folder tools also reject the workspace folder itself (`.`, `""`, `x/..`), so it can never be deleted or renamed. This is enforced in code, not just in the persona.
- **`run_command`:**
  - Runs **without a shell**: shell operators are rejected, and `ProcessStartInfo.ArgumentList` is used.
  - Only commands with a token-prefix match in `Agent:AllowedCommands` are allowed. Don't add a bare `"dotnet new"`, because that would also allow `dotnet new install`.
  - Path arguments are checked by the guard.
  - Stdin is closed.
  - There's a timeout that kills the whole process tree.
  - Build servers are disabled via environment variables, so leftover processes don't keep the output pipe open.
- **Approval:** `Agent:RequireApproval` (default `true`) prompts `Allow? (y/n)` on the console. An empty answer counts as no.

## Conventions
- **POC:** no interfaces, DI or login/persistent-profile handling unless asked. Keep classes concrete and simple.
- **New code:** plain `class`es with full method bodies. No records, no `=>` members, no target-typed `new(...)`. Existing code isn't rewritten.

## Live testing against logged-out ChatGPT
- After about 6 messages in one chat, a sign-up popup blocks the page. The symptom is a Playwright timeout waiting for the Send button or for a reply.
- For live checks:
  - run **one fresh app session per tool or scenario** (pipe the task, then a blank line, into stdin);
  - use a build copy with a short `ResponseTimeoutSeconds` (around 45) and `RequireApproval: false`;
  - keep `LogRawReplies: true` to see exactly what was parsed.
- For live runs, point `Agent:WorkspacePath` at a disposable folder of sample files, and leave it as you found it.
