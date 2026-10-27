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

## How the pieces fit together
Each file has one job, and together they make one round trip with the AI. In the order a message travels:

| File | Job | In one line |
|---|---|---|
| `AgentPrompts.cs` | **What we say** to the AI | Builds the text we send |
| `AiChatBrowser.cs` | **How we talk** to the website | Types into Edge, clicks Send, reads the reply off the page |
| `AgentResponse.cs` | **Understanding what it said** | Turns the reply text into a C# object, or explains why it couldn't |
| `AgentSession.cs` | **Running the conversation** | Uses the other three, plus the tools, in a loop until the task is done |
| `SessionLog.cs` / `SessionHistory.cs` | **Remembering the conversation** | Saves every step to a chat file as it happens, and replays a saved chat after a crash or restart |

### How one step flows
```
AgentSession.RunTaskAsync("Read favorite.txt")
   │
   ├─ AgentPrompts.TaskMessage(task)        → "TASK: Read favorite.txt\n\n(Reminder: ...)"
   │
   ├─ AiChatBrowser.SendAsync(text)         → types it into the AI chat, clicks Send,
   │                                          waits, returns the reply text from the page:
   │                                          "ChatGPT said: {"action":"tool","tool":"read_file",...}"
   │
   ├─ AgentResponse.TryParse(replyText)     → AgentResponse { Action="tool", Tool="read_file",
   │                                          Arguments={"path":"favorite.txt"} }
   │
   ├─ ToolDispatcher.Execute(...)           → {"tool_result":{"tool":"read_file","ok":true,"output":"blue"}}
   │
   └─ AgentPrompts.ToolResultMessage(...)   → result + "(Current task: ... continue)"  → loop again
```

### Each file in a bit more detail
- **`AgentPrompts.cs`: the words.**
  - Every piece of text the program sends, apart from `persona.md`: the task message, the tool-result message, the "your JSON was invalid" message, and the "unknown action" message.
  - It also loads `persona.md` and fills in its placeholders.
  - It doesn't send anything, just builds strings. To change what the AI is told, edit this file (and keep it consistent with `persona.md`).
- **`AiChatBrowser.cs`: the hands and eyes on the web page.**
  - The only file that knows about Playwright, Edge and HTML selectors.
  - It opens the page, fills the prompt box, clicks Send (clicking again if the click was ignored), waits until the reply is marked finished, and reads its text.
  - It knows nothing about JSON or tools; it just moves text in and out of the page. Pointing the agent at another chat site, such as a workplace chatbot, mostly means changing the selectors in config.
  - `DisposeAsync` closes Edge and Playwright's background driver process. `Program.cs` creates the browser with `await using`, so this runs automatically when the app exits, even after an error.
- **`AgentResponse.cs`: the reader.**
  - Defines the shape of a reply (`thought`, `action`, `tool`, `arguments`, `message`).
  - `TryParse` finds the JSON in the messy page text (labels like "ChatGPT said:", "Copy code", prose before the JSON) and converts it. If it can't, it explains why, such as an unescaped line break.
  - It doesn't touch the browser or run anything.
- **`AgentSession.cs`: the conductor.**
  - The only file that ties everything together: it sends the persona at startup, then for each task loops through send → parse → run tool → send result, until the AI says `complete` or `error`.
  - It also handles problems: a reply with no JSON gets a page re-read, then a retry; it stops after `MaxIterations`; it prints `[tool]`, `[result]`, `[complete]` to the console.

- **`SessionLog.cs` / `SessionHistory.cs` / `ChatMenu.cs`: the memory.** See "Session history and resume" below.
  - `SessionLog` writes each step of the chat to a file the moment it happens.
  - `SessionHistory` lists saved chats, turns one back into a readable transcript, and deletes chats safely.
  - `ChatMenu` is the startup menu: open a previous chat, start a new one, or delete one.

`Program.cs` only does start-up: it loads the config, shows the chat menu, creates the browser and the `AgentSession`, and reads tasks from the console. The tools themselves are in `Tools/` (see step 6).

### Why split them up this way
Each part can change without touching the others:
- Rewording a prompt only touches `AgentPrompts`.
- A new chat website only touches `AiChatBrowser` (and the selectors in config).
- A change to the reply format only touches `AgentResponse`.
- Changing how the loop works only touches `AgentSession`.

## Implementation steps

### 1. Project setup
- Add NuGet packages: `Microsoft.Extensions.Configuration.Json` and `Microsoft.Extensions.Configuration.Binder`. (`Microsoft.Playwright` is already referenced.)
- In `AiAgent.csproj`, copy `Config\**` and `Prompts\**` to the output folder (`CopyToOutputDirectory="PreserveNewest"`).
- Move the draft `appsettings.json` from `bin/Debug/net10.0/Config/` into `AiAgent/Config/`. Right now it only exists in the build output and will be lost on a clean build. While moving it, rename `ChatGptUrl` to `AiChatUrl`.

### 2. Configuration: `Config/appsettings.json`
```json
{
  "Agent": {
    "WorkspacePath": "C:\\path\\to\\workspace",
    "MaxIterations": 25,
    "MaxReadChars": 8000,
    "LogRawReplies": true,
    "RequireApproval": true,
    "AllowedCommands": [ "dotnet build", "dotnet test", "dotnet run" ],
    "CommandTimeoutSeconds": 120,
    "MaxCommandOutputChars": 4000
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
**Machine-specific settings** go in `Config/appsettings.local.json`. It's optional and git-ignored, and it's loaded after `appsettings.json`, so any value in it overrides. For example, your real workspace:
```json
{ "Agent": { "WorkspacePath": "C:\\path\\to\\your\\workspace" } }
```
The committed `appsettings.json` keeps the placeholder path, so personal paths never get committed.

`AiChatUrl` and `Selectors` are the only provider-specific values. Pointing the agent at a different AI chat site means changing just these, with no recompile. The values above are examples for the current target site.

### 3. Persona prompt: `Prompts/persona.md`
Load it at startup, replace `{{WORKSPACE}}`, and send it on its own right after the page loads. The AI acknowledges with `{"action":"complete","message":"ready"}`.

See [`Prompts/persona.md`](../Prompts/persona.md) for the full text. It covers:
- **How local commands work.** The tools are called "local commands" and are described as *not* the AI's built-in tools. Without this, the AI tends to look for them in its own tool list and reply that they're "unavailable".
- **The command list:**
  - Reading: `list`, `read_file`, `search_files`, `ask_user`
  - Changing files: `create_file`, `write_file`, `edit_file`, `delete_file`, `move_file`
  - Changing folders: `create_folder`, `delete_folder`, `move_folder`, plus the rule that the workspace folder itself can never be deleted, renamed or moved
  - Building and testing: `run_command` (the allowed commands are filled in from config through `{{ALLOWED_COMMANDS}}`), plus a rule to run code after writing it and fix any errors
- **Rules for writing code:**
  - Read a file before changing it, and prefer `edit_file` for small changes.
  - `old_text` must appear exactly once in the file.
  - Content is escaped inside the JSON string.
  - If the user denies a change, don't retry the same change.
- **The JSON reply format and rules,** plus an example exchange showing a request and a reply. The format is one rule: exactly one JSON object as the last thing in the reply. A ```json block and short text before it are optional.
- **A closing instruction** to acknowledge with `ready`.

Each task is also sent with a one-line reminder of the JSON protocol (`TaskMessage` in `AgentPrompts.cs`).

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
- `static bool TryParse(string reply, out AgentResponse? result, out string? error)`:
  1. Take the content of the last ```json fence, if there is one.
  2. Otherwise try each `{` up to the last `}` until one deserializes. A rendered code block loses its fence and gains "json" / "Copy code" labels, so this step handles that, and also handles text before the JSON.
  3. Deserialize with `PropertyNameCaseInsensitive = true`. A JSON object without `"action"` doesn't count.
  4. If nothing parses, `error` explains why. It prefers the error from the candidate that starts like a JSON object (`{"...`), for example `'0x0A' is invalid within a JSON string`, which means a real line break inside a string. The other possible messages are "the JSON object has no "action" field" and "no JSON object was found in the reply".
- **One format rule everywhere.** The persona, the acknowledge line and the retry message all say the same thing: *exactly one JSON object as the last thing in the reply; a ```json block is optional; short text before it is optional*. (These used to disagree: "end with a json block" vs "reply with only the JSON object". When a retry happened, the AI would complain that the instructions contradicted each other.)
- **If parsing fails, re-read the page first.** The app waits 2 seconds and calls `AiChatBrowser.ReadLastReplyAsync()` again before asking the AI for anything. A reply can be read while ChatGPT is still rendering it: for example only "ChatGPT said:" without its JSON block yet. That gave "no JSON object was found" even though the page showed valid JSON. The re-read fixes that without bothering the AI. With `LogRawReplies` on, the second read is logged as `[raw reply (read again)]`.
- **If it still fails,** up to 3 retries. Each one sends the AI the actual parser error, plus how to fix it:
  > Your last reply did not contain a valid JSON object (parser error: …). Send your reply again with exactly one valid JSON object in the required shape as the last thing in it. Inside JSON strings, write line breaks as \n, quotes as \" and backslashes as \\.

  In a live test the AI deliberately sent a real line break inside `"content"`. It got this message and fixed the escaping on the next reply, without complaining about contradictions.

### 6. Tools: `Tools/` folder
All in namespace `AiAgent.Tools`, as `internal static` classes. A class name ending in "Tools" holds tools the AI can call.

| File | What's in it |
|---|---|
| `ToolDispatcher.cs` | `Execute`, the entry point called by `AgentSession`. It holds `AllToolNames` and `ToolsRequiringApproval`, wraps results as JSON, and has the private approval prompt (`AskForApproval`) and argument helpers (`GetString`, `RequireString`, `GetInt`, `GetBool`) |
| `FileTools.cs` | the file and folder tools listed below |
| `CommandTools.cs` | `run_command` and its private helpers |
| `UserTools.cs` | `ask_user` |
| `WorkspaceGuard.cs` | the path checks every tool uses to stay inside the workspace |
| `ToolOutput.cs` | what every tool returns: `Ok` and `Output`, created with `ToolOutput.Success(text)`, `ToolOutput.Failure(text)` or `new ToolOutput(ok, text)` |

- `ToolDispatcher.Execute(string tool, JsonElement args, AgentSettings settings)`:
  1. Asks for approval when it's needed.
  2. Unpacks the JSON arguments.
  3. Uses a `switch` on the tool name to call the tool.
  4. Returns the `tool_result` JSON string, using the tool's own `Ok` and `Output`.

  The tool methods only receive plain values, never JSON. Every tool returns a `ToolOutput`:
  - Most tools return `ToolOutput.Success(...)` and **throw** when they can't do the job (path outside the workspace, missing file, and so on). The `catch` in `Execute` turns that into `ok:false` with the exception message.
  - `run_command` returns `new ToolOutput(exitCode == 0, output)`. A failed build gives `ok:false` together with the compiler errors, so the AI can fix them.
- **Path guard** (`WorkspaceGuard.ResolvePath`): `var full = Path.GetFullPath(Path.Combine(workspace, path));`. The path is rejected unless `full` starts with the workspace root (case-insensitive, with a trailing separator).
- **Workspace folder guard** (`WorkspaceGuard.ResolveSubfolder`): used by the folder tools. It also rejects a path that resolves to the workspace folder itself (`.`, an empty path, `src/..`, and so on), so the working directory can never be deleted, renamed or replaced.
- `list`: `Directory.EnumerateFileSystemEntries`, output as paths relative to the workspace.
- `read_file`: read the file, apply `offset`, and truncate to `MaxReadChars`. Add a note like `"truncated, next offset = N"`.
- `search_files`: plain substring search over text files. Cap the output at about 50 matches and show `path:line: text`.
- `ask_user`: print the question and return `Console.ReadLine()`.
- `create_file(path, content)`: creates a new file and any missing parent folders. Fails if the file already exists.
- `write_file(path, content)`: replaces the whole file, or creates it.
- `edit_file(path, old_text, new_text)`:
  - Replaces exactly one occurrence. It fails if `old_text` is missing or appears more than once, and the message tells the AI how to fix its request.
  - Matching ignores CRLF vs LF differences, and the file's original line endings are kept.
- `delete_file(path)`: deletes files only. Passing a folder gets "Use delete_folder for folders".
- `move_file(path, new_path)`: moves or renames a file. Both paths must be inside the workspace. Use `move_folder` for folders.
- `create_folder(path)`: creates the folder and any missing parents.
- `delete_folder(path, recursive?)`:
  - Deletes an **empty** folder by default. A folder with anything in it is refused unless `"recursive": true`, which deletes it and everything inside. `recursive` accepts JSON `true` or the string `"true"`.
  - Refuses the workspace folder itself, paths outside the workspace, and files ("Use delete_file for files").
- `move_folder(path, new_path)`: renames or moves a folder, creating any missing parents of `new_path`. This is the "edit folder" tool. It refuses if:
  - either path is the workspace folder itself;
  - `new_path` is outside the workspace;
  - `new_path` already exists;
  - `new_path` is inside the folder being moved;
  - `path` is a file ("Use move_file for files").
- `run_command(command, working_dir?)`: lets the AI build, run and test the code it writes.
  - **Allowed commands:** only commands that start with an entry in `Agent:AllowedCommands` run. The defaults are `dotnet build`, `dotnet test` and `dotnet run`.
  - **No shell:** the program is started directly, so `&&`, `|`, `>` and similar are rejected.
  - **Workspace only:** it runs in the workspace, or in `working_dir` inside it. Absolute paths and `..` arguments must also point inside the workspace.
  - **No keyboard input:** stdin is closed, so programs that wait for input don't hang.
  - **Timeout:** a command that runs longer than `CommandTimeoutSeconds` (default 120) is stopped, along with any processes it started.
  - **Output:** stdout and stderr are returned together, with workspace paths made relative. Output longer than `MaxCommandOutputChars` (default 4000) is shortened, keeping the first quarter and the end, where errors usually are.
  - **Result:** `ok` is `true` only when the exit code is 0, so the AI can tell a failed build from a successful one.
  - **`--project` with a `.cs` file is rejected** with "To run a single file use: dotnet run File.cs". MSBuild's own error for that mistake (MSB4025) is confusing, and the AI got stuck on it in testing.
  - **Build servers are turned off** (`MSBUILDDISABLENODEREUSE`, `UseSharedCompilation=false`). Otherwise a leftover build process can keep the output stream open after `dotnet build` finishes, so the command never seems to end.
- **Approval:** when `Agent:RequireApproval` is `true` (the default), every tool in `ToolsRequiringApproval` shows its arguments in the console first, then asks `Allow? (y/n)`. A "no" sends `ok:false` with "The user denied this action".
  - The tools that ask: `create_file`, `write_file`, `edit_file`, `delete_file`, `move_file`, `create_folder`, `delete_folder`, `move_folder` and `run_command`. The read-only tools (`list`, `read_file`, `search_files`, `ask_user`) never ask.
  - Content previews are capped at 20 lines.
  - A recursive `delete_folder` adds the line `recursive: true (deletes the folder AND everything inside it)`, so you can see that the whole folder will go.
- Wrap each tool in try/catch and return `ok:false` with the exception message.

### 7. Agent loop: `Program.cs`, `AgentSession.cs`, `AgentPrompts.cs`
- **`Program.cs`** (start-up only):
  1. Loads the config (`appsettings.json` + optional `appsettings.local.json`) and checks the workspace exists.
  2. Shows the chat menu (`ChatMenu`): open a previous chat, start a new one, or delete one.
  3. Starts `AiChatBrowser`, opens the chat file (`SessionLog`), and creates an `AgentSession`.
  4. Sends the persona. When reopening a chat, it replays the previous conversation and offers to continue an unfinished task.
  5. Reads tasks from the console. A normal exit records `session_end`; a crash records a `crash` event and then fails as before.
- **`AgentSession.cs`** (one conversation with the AI):
  1. `SendPersonaAsync`: sends the persona once at startup.
  2. `RunTaskAsync`: sends the task, then loops up to `MaxIterations`:
     - parse the reply, re-reading the page or retrying the AI if needed (`SendAndParseAsync`);
     - `tool`: run it and send back the `tool_result` together with the current task;
     - `complete` / `error`: print the message and return to the console prompt.
  3. Prints each exchange (thought, tool and arguments, result preview) so you can watch it work.
- **`AgentPrompts.cs`:** every piece of text the program sends to the AI, apart from `persona.md`:
  - `LoadPersona`
  - `TaskMessage` (includes the protocol reminder)
  - `ToolResultMessage` (includes the current-task reminder)
  - `ParseRetryMessage`
  - `UnknownActionMessage`

  It's kept in one place so it stays consistent with the persona's format rule. The session-history messages live here too: `ContinueTaskMessage`, `ReplayPartMessage` and `ReplayDoneMessage`.

### 8. Session history and resume: `SessionLog.cs`, `SessionHistory.cs`, `ChatMenu.cs`
The workplace chatbot starts a new, empty chat on every visit, and its chat URL never changes. So after a crash or restart, the app has to keep the history **itself** and **rebuild the AI's context** in the new chat.

**The chat file** (`SessionLog`):
- **One chat is one JSON Lines file:** one event per line, written and flushed to disk immediately, so a crash loses at most the step in progress.
- **Events:**
  - `session_start` (workspace, chat site)
  - `task_start` (`continued: true` when continuing an unfinished task)
  - `sent` / `reply` (the raw text)
  - `tool_call` (tool, thought, arguments)
  - `tool_result` (nested JSON)
  - `task_end` (complete / error / stopped, plus the message)
  - `resumed`
  - `session_end`
  - `crash`
- **The persona and replay messages** are logged with `"persona": true` / `"replay": true`, so they're never replayed again.
- **Reopening a chat appends to the same file,** so one file holds the whole conversation across any number of restarts.
- **The file can be opened in an editor while the app runs.** Use an editor that allows shared access.

**Where the files go:** `AiAgent/Sessions/<workspace name>-<hash of the workspace path>/<yyyy-MM-dd_HHmmss>.jsonl`.
- That's in the project folder, found by walking up from `bin/` to `AiAgent.csproj`, not in `bin/`, so a clean or rebuild doesn't delete chats.
- **`AiAgent/Sessions/` is git-ignored.** Chats contain code, file contents and personal paths, and must stay on the local machine.
- **`Agent:SessionsPath`** (absolute, or relative to the project folder) moves them elsewhere.
- A published exe with no project folder above it uses `<exe folder>/Sessions`.
- **Caveat:** don't set `WorkspacePath` to this repo itself, or the AI's file tools could read the chat files.

**The startup menu** (`ChatMenu`):
```
Chats for this workspace:
  1) 27 Oct 14:02  "Create the Contacts entity…"   3 tasks  UNFINISHED
  2) 26 Oct 09:40  "Start the FileMaker migration" 5 tasks  ended normally

Enter a number to open that chat, N (or Enter) for a new chat, or D and a number to delete a chat (e.g. D2):
```
- **Chats are listed most recently used first,** titled by their first task.
- **Statuses:**
  - `UNFINISHED`: a task was running when the app stopped;
  - `ended normally`: exited with a blank task;
  - `closed without exiting`: the window was closed at the `Task:` prompt.
- **`D<n>`:** asks for confirmation, then deletes only that chat file (`SessionHistory.DeleteSession` refuses anything outside the workspace's sessions folder). A chat open in another running copy can't be deleted.
- **With no chats yet,** the app goes straight to a new chat.

**Reopening a chat** (`AgentSession.ResumeAsync`):
1. The persona is sent as usual.
2. `SessionHistory.BuildReplay` turns the chat file into a transcript of the whole conversation: every task, every AI step with its thought and arguments, every result, and every outcome.
   - Raw traffic (persona, retries, earlier replays) is skipped.
   - Tool outputs and file contents (`content`, `new_text`, `old_text`) longer than `Agent:ResumeMaxResultChars` (1500) are shortened with "(shortened; re-read the file if you need it)". The files on disk are the real current state.
   - Where a task was interrupted, the transcript says "(UNFINISHED: the app stopped here)".
3. The transcript is sent in parts of at most `Agent:ResumeChunkChars` (6000), each as "PREVIOUS CONVERSATION (part i of n)… reply ok", then a closing message.
4. If a task was unfinished, the app asks `Continue the unfinished task "…"? (y/n)`. On `y`, the task is sent with `ContinueTaskMessage` ("carry on from the steps already done instead of starting over").

**Limit (next update):** this replays the **whole** chat. That's fine for short and medium chats. Very long chats need many replay messages, and the chatbot may forget the oldest parts. The next update adds a size-limited **tiered replay**: a short log-built summary of every task, the most recent tasks in full, and the unfinished task always in full.

## Testing the POC
1. Run `dotnet build` and then `dotnet run`. Edge opens on the AI chat site.
2. Create a test workspace folder with a few files.
3. Happy path: ask *"Summarize what Program.cs does"*. Expect `list` → `read_file` → `complete`.
4. Path guard: ask it to read `..\..\Windows\win.ini`. Expect `ok:false`, then the AI recovers or returns `error`.
5. Search: ask *"Which files mention Console?"*. Expect a `search_files` call.
6. Retry path: reply with something odd mid-task (or let a long answer run) and confirm the "JSON only" retry kicks in.

### Verified results
Every tool has been checked live through ChatGPT, using **one app session per tool** and a 45-second reply timeout. A fresh session per tool avoids the logged-out sign-up popup, which appears after a few messages and stalls the reply.

| Tool | Live check | Result |
|---|---|---|
| `list` | Listed the workspace root | Pass |
| `read_file` | Read `favorite.txt` and gave back "blue". Paging with `offset` was also checked, with `MaxReadChars` set to 8 | Pass |
| `search_files` | Found `DivideByZeroException` in `src/Calculator.cs` | Pass |
| `ask_user` | Asked for a number, got 21, answered "21 doubled is 42" | Pass |
| `create_file` / `write_file` / `edit_file` | Created `notes.txt`, replaced its content, then read it and edited one word | Pass |
| `move_file` / `delete_file` | Moved `notes.txt` into `archive/`, then deleted it | Pass |
| `create_folder` / `move_folder` | Created `archive`, then renamed it to `old_archive` | Pass |
| `delete_folder` | Deleted an empty folder. For a folder with files, the AI chose `recursive: true` by itself | Pass |
| `run_command` | `dotnet run` on a file, `dotnet build` on a project, and `&&` rejected. In the fix loop it ran `Stats.cs`, read the compile error, edited the file, reran it and got `Average: 5`. It also wrote `Primes.cs` and ran it to verify | Pass |
| Approval | Denying a recursive `delete_folder` left the folder in place, and the AI didn't retry. The prompt showed `recursive: true` | Pass |
| Workspace folder guard | **With the persona rule**, the AI refused to call `delete_folder` on `.` (twice). **With the rule removed** from a test copy of the persona, the AI called `delete_folder {".", recursive: true}` and `move_folder {".", "renamed_workspace"}`. The code refused both ("The workspace folder itself cannot be changed"), and the workspace and its files were untouched. Also checked offline with an empty path, `src/..` and a trailing slash | Pass |

The offline checks call `ToolDispatcher.Execute` directly. They cover:
- the path guard;
- `edit_file` with CRLF, duplicate and missing text;
- `run_command`: blocked commands, shell operators, paths outside the workspace, closed stdin, long output, timeout, and `--project` with a `.cs` file;
- every refusal case for `delete_folder` and `move_folder`.

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
- Show a diff in the approval prompt for `write_file` and `edit_file`, instead of a raw content preview.
- Optionally add more commands to `AllowedCommands`, such as `dotnet new` or `dotnet add package`.

**Structure (once stable)**
- Pull out an `IAiChatBackend` so you can swap the browser backend for an AI provider's API. An API supports native tool calling and removes the scraping and terms-of-use issues.
- Use per-site selector profiles in config so you can switch between AI chat sites by name.
- Describe each tool once, in a single tool table (name, signature, description, needs approval, method to call). Generate the dispatch, `AllToolNames`, `ToolsRequiringApproval` and the persona's tool list from it, so they can't drift apart.
- Use `Microsoft.Extensions.Hosting` for DI, logging and config.

**Session history** (the basics are done; see step 8)
- **Tiered replay for very long chats:** a log-built summary of every task, the most recent tasks in full up to `Agent:ResumeMaxChars`, and the unfinished task always in full.
- `/sessions` and `/show` commands to browse or switch chats during a run. Today the menu is at startup only.
- Automatic cleanup of old chat files.

**Observability and safety**
- Add a token/character budget per task and a stop command (for example, typing `stop`).
- Limit file reads to an allow-list of extensions and skip `bin/`, `obj/`, `.git/`.

