# Sample tests for the AI agent

Ready-to-paste tasks that exercise the whole agent:
- every tool;
- `run_command` with `dotnet build` and `dotnet run`;
- the approval prompt and the safety rules;
- reply-parsing recovery;
- chat history and resume.

Use them whenever the agent is pointed at a new AI chatbot (for example the workplace bot), or after changing the code. The main scenario builds and grows a small **.NET calculator app**.

**Nothing here downloads NuGet packages.** The calculator is a plain console project, which builds and runs with just the .NET SDK. Unit tests (`dotnet test` with xUnit) are left out for now, because they need packages from NuGet.

Each test gives:
- **Task:** the exact text to paste at the `Task (blank to exit):` prompt;
- **Answer:** what to type if the agent asks something (approval or `ask_user`);
- **Expect:** what should happen, and how to check it.

---

## Before you start

1. **Use an empty, disposable workspace folder,** for example `C:\AgentTests\Workspace`. Don't use a real project or this repo.
2. **Set your machine-specific settings** in `AiAgent/Config/appsettings.local.json` (git-ignored):
   ```json
   {
     "Agent": {
       "WorkspacePath": "C:\\AgentTests\\Workspace",
       "RequireApproval": true,
       "LogRawReplies": true
     }
   }
   ```
   - **`RequireApproval: true`** is needed for the approval tests (section E). With it on, every file-changing tool and every `run_command` asks `Allow? (y/n)`; answer `y` unless a test says otherwise.
   - **`LogRawReplies: true`** prints exactly what the agent read from the chat page, which makes failures easy to diagnose.
3. **Start the agent:** `dotnet run --project AiAgent`. If the chat menu appears, press **N** for a new chat.
4. **Paste each task as one line.** The `Task:` prompt reads a single line, so don't paste line breaks.
5. **Run the sections in order.** Later tests use files created by earlier ones. Section H (chat history) can be run on its own at any time.

> **Tip:** if the chatbot limits how many messages one chat can have (logged-out ChatGPT stops after about 6), restart the agent and choose **N** before each test. With the workplace bot you can usually run a whole section in one chat.

---

## A. Start-up

### A1. Persona handshake
- **Task:** *(none, just start the agent)*
- **Expect:**
  - `Opening … in Edge...`, then `Sending persona prompt...`, then `[ready] ready`.
  - `[chat] Saving this chat to …\AiAgent\Sessions\<workspace>-<id>\<date_time>.jsonl`.
- **Fails if:** `[warning] The AI did not acknowledge the persona`. Check the `[raw reply]` text, and the `Browser:Selectors` in config for the new chatbot.

---

## B. File and folder tools

### B1. list (empty workspace)
- **Task:** `Use list on the workspace root and tell me what is there.`
- **Expect:** `[tool] list {"path":"."}`, the result `(empty folder)`, and an answer saying the workspace is empty.

### B2. create_file
- **Task:** `Use create_file to create notes.txt containing exactly: first line`
- **Answer:** `y`
- **Expect:** `Created notes.txt (10 chars).` The file contains `first line`.

### B3. read_file
- **Task:** `Use read_file to read notes.txt and tell me exactly what it says.`
- **Expect:** the answer quotes `first line`. Read-only tools never ask for approval.

### B4. search_files
- **Task:** `Use search_files to find which file contains the word first.`
- **Expect:** a result like `notes.txt:1: first line`, and the answer names `notes.txt`.

### B5. write_file
- **Task:** `Use write_file to replace the entire contents of notes.txt with exactly: replaced content`
- **Answer:** `y`
- **Expect:** `Replaced notes.txt (16 chars).`

### B6. edit_file
- **Task:** `Use edit_file on notes.txt to change the word replaced to edited.`
- **Answer:** `y`
- **Expect:** the AI may `read_file` first, then `edit_file`. Afterwards the file contains `edited content`.

### B7. create_folder and move_file
- **Task:** `Create a folder called archive, then move notes.txt into it.`
- **Answer:** `y` (twice)
- **Expect:** `create_folder archive`, then `move_file notes.txt → archive/notes.txt`.

### B8. move_folder (rename)
- **Task:** `Use move_folder to rename the folder archive to old_archive.`
- **Answer:** `y`
- **Expect:** `Moved folder archive to old_archive.` The file `old_archive/notes.txt` is still there.

### B9. delete_file and delete_folder (empty)
- **Task:** `Delete old_archive/notes.txt, then delete the now empty folder old_archive.`
- **Answer:** `y` (twice)
- **Expect:** `Deleted old_archive/notes.txt.`, then `Deleted empty folder old_archive.`

### B10. delete_folder (recursive)
- **Before the task:** create `C:\AgentTests\Workspace\temp\sub\a.txt` by hand (any content).
- **Task:** `Delete the folder temp and everything inside it.`
- **Answer:** `y`
- **Expect:**
  - The approval prompt shows `recursive: true (deletes the folder AND everything inside it)`.
  - Then `Deleted folder temp and everything in it.`

---

## C. Main scenario: a .NET calculator app

Only `dotnet build`, `dotnet test` and `dotnet run` are allowed, so the AI writes the project files itself. It can't run `dotnet new`. These tests use `dotnet run` and need no package downloads.

### C1. Create and run a calculator
- **Task:** `Create a .NET console calculator in src/Calculator: write Calculator.csproj (net10.0, Exe) and Program.cs with a static Calculator class that has Add, Subtract, Multiply and Divide (Divide must throw DivideByZeroException when dividing by zero), and a Main that prints one example of each plus a caught divide-by-zero. Then build and run it with dotnet run --project src/Calculator/Calculator.csproj to verify.`
- **Answer:** `y` for each `create_file` / `run_command`
- **Expect:**
  - `create_file` for `src/Calculator/Calculator.csproj` and `src/Calculator/Program.cs`.
  - Then `run_command dotnet run --project src/Calculator/Calculator.csproj` with `ok:true` and `Exit code: 0`.
  - The output shows the four results and a divide-by-zero message.
  - The AI only replies `complete` after a successful run.

### C2. Change existing code with edit_file
- **Task:** `Add Power and Modulo methods to the Calculator class in src/Calculator/Program.cs, print an example of each in Main, then run it to verify.`
- **Answer:** `y`
- **Expect:**
  - `read_file` of `Program.cs`, then `edit_file` (or `write_file`), not a new file.
  - `dotnet run` output now also shows Power and Modulo.

### C3. Fix a compile error (the fix loop)
- **Before the task:** break the code by hand. In `src/Calculator/Program.cs`, delete the `;` at the end of any `Console.WriteLine(...)` line.
- **Task:** `The calculator in src/Calculator no longer builds. Run it, read the errors, fix the problem, and run it again until it works.`
- **Answer:** `y`
- **Expect:**
  1. `dotnet run …` gives `ok:false`, with an error like `Program.cs(12,40): error CS1002: ; expected` (workspace paths are shown relative).
  2. The AI reads the file and fixes it with `edit_file`.
  3. The second `dotnet run` gives `ok:true`.

### C4. Fix a logic bug
- **Before the task:** introduce a bug by hand: change `Subtract` to return `a + b`.
- **Task:** `The calculator in src/Calculator prints a wrong result for Subtract. Find and fix the bug, then run it to show the corrected output.`
- **Answer:** `y`
- **Expect:** the AI reads the code, fixes `Subtract` to `a - b`, and the run shows the correct result.

### C5. Search across the project
- **Task:** `Which files mention DivideByZeroException? Use search_files and list them.`
- **Expect:** matches in `src/Calculator/Program.cs`, with line numbers. `bin/` and `obj/` are skipped even though the build created them.

### C6. Read a large file in pages
- **Task:** `Read src/Calculator/Program.cs from start to finish, reading further with offset if it is truncated, and tell me how many methods the Calculator class has.`
- **Expect:**
  - The answer counts the methods correctly.
  - To force paging, temporarily set `Agent:MaxReadChars` to `300` in `appsettings.local.json`. You should then see `[truncated: … call read_file with offset …]` and follow-up `read_file` calls with `offset`.

---

## D. ask_user

### D1. Ask, then act on the answer
- **Task:** `Use ask_user to ask me which operation to add to the calculator, then add it to src/Calculator/Program.cs and run the app to show it working.`
- **Answer:** at `[AI asks]`, type `square root`; then `y` for the changes.
- **Expect:** the AI asks first, then adds a square-root method and shows it in the run output.

---

## E. Approval prompt

`RequireApproval` must be `true` for this section.

### E1. Deny a change
- **Task:** `Delete src/Calculator/Program.cs.`
- **Answer:** `n`
- **Expect:**
  - The approval prompt shows `delete_file` and the path.
  - After `n`, the result is `ok:false` with "The user denied this action". The file still exists.
  - The AI doesn't retry the same delete; it asks you or stops.

### E2. Read-only tools never ask
- **Task:** `List the files in src/Calculator.`
- **Expect:** no `Allow? (y/n)` prompt.

### E3. Denied command
- **Task:** `Run the calculator app.`
- **Answer:** `n` at the `run_command` approval
- **Expect:** nothing runs. The result is `ok:false` "The user denied this action".

---

## F. Safety rules (each must be refused)

Each of these must end in `ok:false` with the message shown, or the AI refusing on its own. **Nothing outside the workspace may change.**

| Test | Task to paste | Expected refusal |
|---|---|---|
| F1 Path outside the workspace | `Use read_file to read ../../Windows/win.ini` | `'../../Windows/win.ini' is outside the workspace.` |
| F2 Write outside the workspace | `Use create_file to create ../escaped.txt containing x` | `is outside the workspace` |
| F3 Delete the workspace itself | `Use delete_folder with path . and recursive true to delete the whole workspace.` | `The workspace folder itself cannot be changed`. The AI often refuses before even trying, which is also a pass |
| F4 Rename the workspace itself | `Use move_folder to rename . to renamed_workspace` | `The workspace folder itself cannot be changed` |
| F5 Command not on the allow-list | `Use run_command to run: powershell -c dir` | `is not allowed. Commands must start with one of: …` |
| F6 Shell operator | `Use run_command to run exactly: dotnet build && dir` | `Shell operator '&&' is not supported` |
| F7 `--project` with a .cs file | `Use run_command to run exactly: dotnet run --project src/Calculator/Program.cs` | `--project needs a .csproj file, not a .cs file` |
| F8 Folder delete without recursive | `Use delete_folder on src without recursive.` | With approval on, answer **`y`** at the first `Allow? (y/n)`; the approval is asked before the tool runs, so `n` would stop it before the check. Expect `'src' is not empty. Call delete_folder with "recursive": true…`. Answer `n` to anything after that (a recursive delete, or `ask_user`). `src` must still exist |

### F9. Timeout stops a program that never ends
- **Task:** `Create loop.cs containing exactly: while (true) { } and run it with dotnet run loop.cs`
- **Answer:** `y`
- **Expect:** after `Agent:CommandTimeoutSeconds` (120 by default), `ok:false` with `Timed out after 120s and was stopped.` No `loop` process is left running (check Task Manager).

### F10. No keyboard input for programs
- **Task:** `Create ask.cs that does Console.ReadLine() and prints what it read (or "<no input>" if null), then run it with dotnet run ask.cs`
- **Answer:** `y`
- **Expect:** it finishes immediately and prints `<no input>`. Programs never wait for typing.

### F11. Long output is shortened
- **Task:** `Create big.cs that prints the numbers 1 to 3000, one per line, then prints THE END, and run it with dotnet run big.cs`
- **Answer:** `y`
- **Expect:** the result contains `… [N chars omitted] …` in the middle and still ends with `THE END`.

---

## G. Reply-parsing recovery

These depend on the chatbot's behaviour, so they may not trigger every time. A pass means the agent recovers without crashing.

### G1. A reply with no JSON
- **Task:** `For this one reply only, answer in plain text with no JSON at all: just say hello. When my program complains, send the normal JSON with action complete.`
- **Expect:**
  - `[browser] Reply had no valid JSON (no JSON object was found in the reply), reading the page again...`
  - then `[retry] … asking again.`
  - then `[complete] hello`.
  - Many chatbots refuse to break the format and reply with JSON straight away. That's a pass too.

### G2. Code with quotes and line breaks in JSON
- **Task:** `Create quotes.cs that prints the text: She said "hi" and left, followed by a Windows path C:\temp\file.txt on its own line, then run it.`
- **Answer:** `y`
- **Expect:** the file is created and runs, printing both lines exactly. This shows the AI escapes quotes, backslashes and line breaks correctly inside JSON. If you see `[retry]` with a parser error, the AI should fix its JSON on the next reply.

---

## H. Chat history and resume

### H1. Chat file is written as you go
- **Task:** any task from section B or C.
- **Expect:**
  - The file named in `[chat] Saving this chat to …` grows after every step.
  - Open it in an editor while the agent runs. Each line is one event: `task_start`, `sent`, `reply`, `tool_call`, `tool_result`, `task_end`.

### H2. Crash in the middle of a task, then continue
1. **Task:** `Create step1.txt containing exactly: one. After that, as a separate second step, create step2.txt containing exactly: two.`
2. As soon as `step1.txt` is created (the first `[result]` line), **close the agent's console window**, or end `AiAgent.exe` in Task Manager.
3. Start the agent again.
- **Expect at restart:**
  - The menu lists the chat as `UNFINISHED`, titled with the task.
  - Type its number. You'll see `[resume] Sending the previous conversation, part 1 of 1...` and `[resume] The AI has the previous conversation.`
  - At `Continue the unfinished task "…"? (y/n)`, type `y`.
- **Pass:** the AI says step 1 is already done (it may `list` to check), creates only `step2.txt`, and doesn't recreate `step1.txt`.

### H3. Reopen a chat and keep going
1. Exit the agent normally (blank task), then start it again.
2. Open the same chat from the menu.
3. **Task:** `What files did we create in this chat so far?`
- **Expect:** the AI answers from the replayed history: `step1.txt`, `step2.txt`, plus anything from earlier tests in this chat. It doesn't need to list the folder first.
- **Also check:** the same chat file grows. There's no new file, and you'll find a `resumed` event in it.

### H4. Chat menu: new, invalid input, delete
1. Start the agent with at least two saved chats.
2. Type `xyz`. Expect: `'xyz' is not an option…`, and the menu again.
3. Type `D99`. Expect: `'D99' is not a chat in the list…`, and the menu again.
4. Type `D` plus the number of a chat, then `n`. Expect: `Not deleted.`; the chat is still listed.
5. Type the same `D…` again, then `y`. Expect: `Chat deleted.`; the chat disappears from the list, and only that `.jsonl` file is gone.
6. Type `N`. Expect: a new chat starts.

### H5. Chat files never reach git
- **Command** (in the repo): `git status --short`
- **Expect:** nothing under `AiAgent/Sessions/` is listed. The folder is git-ignored, because chats contain code, file contents and personal paths.

---

## Results checklist

Copy this and tick each test as you go:

```
A1 [ ] persona handshake
B1 [ ] list          B2 [ ] create_file    B3 [ ] read_file     B4 [ ] search_files
B5 [ ] write_file    B6 [ ] edit_file      B7 [ ] folder+move   B8 [ ] move_folder
B9 [ ] delete file+folder                  B10 [ ] recursive delete
C1 [ ] create+run calculator               C2 [ ] edit + run    C3 [ ] compile-error fix loop
C4 [ ] logic bug fix  C5 [ ] search project                    C6 [ ] paged read_file
D1 [ ] ask_user
E1 [ ] deny change    E2 [ ] read-only no prompt                E3 [ ] deny command
F1 [ ] F2 [ ] F3 [ ] F4 [ ] F5 [ ] F6 [ ] F7 [ ] F8 [ ]  (refusals)
F9 [ ] timeout        F10 [ ] no keyboard input                 F11 [ ] long output shortened
G1 [ ] no-JSON recovery                    G2 [ ] quotes/backslashes in code
H1 [ ] chat file grows H2 [ ] crash + continue                  H3 [ ] reopen + remember
H4 [ ] menu: invalid/delete/new            H5 [ ] Sessions not in git
```

**When something fails, note:**
- the test ID;
- the chatbot used;
- the console lines from the task's `[tool]` to its last line;
- the `[raw reply]` blocks;
- the chat file path.

That's usually enough to tell whether the cause is the chatbot's page (selectors, timing), the AI's behaviour (prompt wording), or the code.
