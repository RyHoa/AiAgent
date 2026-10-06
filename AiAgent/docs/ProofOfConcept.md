# AiAgent: Proof of Concept

A .NET 10 console app that uses Playwright to drive a web-based AI chatbot in Microsoft Edge as the "brain" of a local AI agent. On startup it sends a persona prompt that describes the available tools and requires JSON replies. The app then parses each reply, runs the requested tool, and sends the result back until the task is complete.

> **Scope:** This is a proof of concept. It uses concrete classes only (no interfaces) and opens a fresh browser session on every startup (no login handling). Structure gets added once the loop works end to end.

> **Heads-up:** Automating an AI provider's web UI may conflict with that provider's terms of use, and the page's HTML changes often. Fine for experimenting, but see "Next steps" for moving to an API.

## How it works

```
Console task ──► Persona + task ──► AI chat (Edge via Playwright)
                                          │
                                   JSON reply (last ```json block)
                                          ▼
                              Parse ──► action?
                     ┌────────────────────┼───────────────────┐
                   tool               complete/error       ask_user
                     │                     │                  │
             Run tool (workspace-     Print message,     Ask in console,
             guarded) → tool_result   wait next task     send answer
                     └──────────► send back to AI chat ◄──────┘
```

## Implementation steps

### 1. Project setup
- Add NuGet packages: `Microsoft.Extensions.Configuration.Json` and `Microsoft.Extensions.Configuration.Binder`. (`Microsoft.Playwright` is already referenced.)
- In `AiAgent.csproj`, copy `Config\**` and `Prompts\**` to the output folder (`CopyToOutputDirectory="PreserveNewest"`).
- Move the draft `appsettings.json` from `bin/Debug/net10.0/Config/` into `AiAgent/Config/`. Right now it only exists in the build output and will be lost on a clean build. While moving it, rename `ChatGptUrl` to `AiChatUrl`.

### 2. Configuration: `Config/appsettings.json`
```json
{
  "Agent": {
    "WorkspacePath": "C:\\Users\\Ryan Hoang\\Desktop\\Test",
    "MaxIterations": 25,
    "MaxReadChars": 8000,
    "LogRawReplies": true
  },
  "Browser": {
    "AiChatUrl": "https://chatgpt.com/",
    "Headless": false,
    "ResponseTimeoutSeconds": 120,
    "Selectors": {
      "PromptBox": "textarea[name=\"prompt\"]",
      "SendButton": "button[aria-label=\"Send message\"]",
      "AssistantMessage": "li[data-message-role=\"assistant\"]",
      "CompletedMessage": "li[data-message-role=\"assistant\"][data-message-complete]"
    }
  }
}
```
`AiChatUrl` and `Selectors` are the only provider-specific values. Pointing the agent at a different AI chat site means changing just these, with no recompile. The values above are examples for the current target site.

### 3. Persona prompt: `Prompts/persona.md`
Load it at startup, replace `{{WORKSPACE}}`, and send it on its own right after the page loads. The AI acknowledges with `{"action":"complete","message":"ready"}`.

````text
You are a concise developer assistant working inside a local workspace at {{WORKSPACE}}.
You cannot see files directly; you work ONLY by requesting tools, one per reply.

TOOLS
- list(path)                 → files/folders in a directory (relative to workspace)
- read_file(path, offset?)   → file text, truncated; use offset to read further
- search_files(query)        → files + lines containing the text
- ask_user(question)         → ask the human a clarifying question

RESPONSE FORMAT (mandatory)
Every reply MUST end with exactly one ```json block in this shape:
```json
{
  "thought": "short reasoning",
  "action": "tool" | "complete" | "error",
  "tool": "read_file",
  "arguments": { "path": "src/Program.cs" },
  "message": "final answer or error text (for complete/error)"
}
```
Rules:
- One tool call per reply. Omit "tool"/"arguments" when action is complete/error.
- Paths are relative to the workspace. Never access anything outside it.
- Tool results come back to you as:
  {"tool_result": {"tool": "...", "ok": true, "output": "..."}}
- If ok is false, recover with another tool call or return action "error".
- If you reply without valid JSON you will be asked again (max 3 times).
````

### 4. Browser wrapper: `AiChatBrowser.cs`
- `StartAsync()`:
  - `Playwright.CreateAsync()`
  - `Chromium.LaunchAsync(new() { Channel = "msedge", Headless = false })`
  - `NewPageAsync()`
  - `GotoAsync(AiChatUrl)`
  - wait for the `PromptBox` selector
- `SendAsync(string text)` returns `string`:
  1. Count the completed assistant messages (`CompletedMessage`).
  2. `FillAsync(PromptBox, text)`, then click `SendButton`. Pressing Enter is ignored if the page hasn't finished loading its scripts.
  3. Wait until the completed-message count increases. The site adds `data-message-complete` when a reply is finished.
  4. Stability check: poll the last message's `InnerTextAsync()` until it is unchanged for about 1 second.
  5. Return that text.

### 5. Reply parsing: `AgentResponse.cs`
- `record AgentResponse(string? Thought, string Action, string? Tool, JsonElement? Arguments, string? Message)`
- `static bool TryParse(string reply, out AgentResponse? result)`:
  1. Take the content of the last ```json fence.
  2. If there is none, take the outermost `{ ... }`.
  3. Deserialize with `PropertyNameCaseInsensitive = true`.
- If parsing fails, send: *"Reply with only the JSON object in the required format."* Allow up to 3 retries.

> Note: `InnerText` of a rendered code block may include a "Copy code" label or the word `json` as a header. Strip those before parsing if needed.

### 6. Tools: `Tools.cs` (static class)
- `string Execute(string tool, JsonElement args)` is a `switch` on the tool name. It returns the `tool_result` JSON string.
- Path guard: `var full = Path.GetFullPath(Path.Combine(workspace, path));`. Reject the path unless `full` starts with the workspace root (case-insensitive, with a trailing separator).
- `list`: `Directory.EnumerateFileSystemEntries`, output as paths relative to the workspace.
- `read_file`: read the file, apply `offset`, and truncate to `MaxReadChars`. Add a note like `"truncated, next offset = N"`.
- `search_files`: plain substring search over text files. Cap the output at about 50 matches and show `path:line: text`.
- `ask_user`: print the question and return `Console.ReadLine()`.
- Wrap each tool in try/catch and return `ok:false` with the exception message.

### 7. Agent loop: `Program.cs`
1. Load the config and start `AiChatBrowser`.
2. Read a task from the console.
3. Send `"TASK: " + task` (the persona was already sent at startup).
4. Loop up to `MaxIterations`:
   - Parse the reply, retrying if needed.
   - `tool`: run it and send the `tool_result`.
   - `complete` / `error`: print the message and go back to step 2.
5. Print each exchange (thought, tool and arguments, result preview) so you can watch it work.

## Testing the POC
1. Run `dotnet build` and then `dotnet run`. Edge opens on the AI chat site.
2. Create a test workspace folder with a few files.
3. Happy path: ask *"Summarize what Program.cs does"*. Expect `list` → `read_file` → `complete`.
4. Path guard: ask it to read `..\..\Windows\win.ini`. Expect `ok:false`, then the AI recovers or returns `error`.
5. Search: ask *"Which files mention Console?"*. Expect a `search_files` call.
6. Retry path: reply with something odd mid-task (or let a long answer run) and confirm the "JSON only" retry kicks in.

## Known POC limitations
- Selectors and completion detection are brittle and break when the AI site changes its UI.
- With no login, anonymous use may show popups or limit usage or models.
- The prompt box has a length limit, so large tool outputs must be truncated.
- In long chats the AI can drift from the JSON format.

## Next steps (after the POC works)
**Reliability**
- Re-send a short protocol reminder every N turns, and on every parse failure.
- Better completion detection: watch for the send button to come back, and use a `MutationObserver` with a quiet period.
- Paste long text through the clipboard (`page.Keyboard.InsertTextAsync`) instead of `FillAsync`.
- Handle popups and dismiss them on startup (cookie banners, "stay logged out", and so on).

**Session and login**
- Use a persistent Edge profile (`LaunchPersistentContextAsync` with a dedicated `userDataDir`) so you stay logged in.
- Show a "log in, then press Enter" prompt the first time.

**More tools (with approval)**
- `write_file`, `create_file`, `delete_file`, `run_command`.
- Ask for console y/n approval before each of these.
- `run_command`: set a timeout, use the workspace as the working directory, and capture and cap stdout/stderr.

**Structure (once stable)**
- Pull out an `IAiChatBackend` so you can swap the browser backend for an AI provider's API. An API supports native tool calling and removes the scraping and terms-of-use issues.
- Use per-site selector profiles in config so you can switch between AI chat sites by name.
- Create one class per tool plus a registry, and generate the `TOOLS` section of the persona from it so the prompt and code stay in sync.
- Use `Microsoft.Extensions.Hosting` for DI, logging and config.

**Observability and safety**
- Save every exchange to a transcript file (JSONL) for debugging prompts.
- Add a token/character budget per task and a stop command (for example, typing `stop`).
- Limit file reads to an allow-list of extensions and skip `bin/`, `obj/`, `.git/`.

