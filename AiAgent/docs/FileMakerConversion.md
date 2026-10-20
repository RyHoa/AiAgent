# FileMaker Pro → ASP.NET Core + React: conversion roadmap

The goal is to use the agent to convert FileMaker Pro applications into:
- **ASP.NET Core Web API** with **EF Core** and **SQL Server**;
- a **React + Vite + TypeScript** front end;
- tests with **xUnit** (API) and **Vitest** (front end).

The agent reads the FileMaker design from a **DDR (Database Design Report) XML export**. It's meant to run long sessions on a **workplace chatbot** that needs no login, so progress has to survive across chat sessions.

This document covers what is in place now (Phase 1) and what comes next. The general design of the agent is in [ProofOfConcept.md](ProofOfConcept.md).

## Using it today

### 1. Turn on the conversion guide
Add the guide to your machine-specific settings in `Config/appsettings.local.json`, which git ignores:
```json
{
  "Agent": {
    "WorkspacePath": "C:\\path\\to\\conversion-workspace",
    "ExtraPromptFiles": [ "filemaker-conversion.md" ]
  }
}
```
`Prompts/filemaker-conversion.md` is appended to the persona just before the acknowledge line. It takes the persona from about 5,500 to about 8,100 characters. Leave `ExtraPromptFiles` empty for normal coding work.

### 2. Set up the workspace
```
conversion-workspace/
  fm-source/          ← put the DDR XML export(s) here; the AI is told to treat this folder as read-only
  MIGRATION.md        ← created and kept up to date by the AI: the progress checklist
  docs/fm/            ← one short spec per FileMaker table, layout and script, written by the AI from the DDR
  src/Api/            ← ASP.NET Core Web API (EF Core, SQL Server)
  src/Web/            ← React + Vite + TypeScript
  tests/Api.Tests/    ← xUnit tests
```

### 3. Work phase by phase
Typical tasks, one at a time:
1. "Start the FileMaker migration: create the MIGRATION.md checklist from the DDR in fm-source."
2. "Write the docs/fm spec for the Contacts table."
3. "Scaffold the solution."
4. "Create the EF Core entities and DbContext for Contacts and Orders."
5. API endpoints, then React pages, then tests.

### How progress carries across sessions
The AI is told that **`MIGRATION.md` is the single source of truth**, because chat history may be lost:
- it reads the file at the start of every task;
- it updates it after every finished item;
- it records the mapping decisions it made, such as "the FullName calculation became a computed property".

A new chat session can therefore pick up where the last one stopped.

## What Phase 1 added

### Commands the AI can run
`Agent:AllowedCommands` now also includes:

| Purpose | Commands |
|---|---|
| Scaffolding | `dotnet new webapi`, `dotnet new sln`, `dotnet new console`, `dotnet new xunit` |
| Solution and packages | `dotnet sln add`, `dotnet add package`, `dotnet add reference` |
| Front end | `npm create vite@latest`, `npm install`, `npm ci`, `npm run build`, `npm run lint`, `npm test` |

Some commands are deliberately **not** allowed:
- **plain `dotnet new`**: that would also allow `dotnet new install`, which installs template packages;
- **`npm run dev`**: it starts a server that never exits.

`CommandTimeoutSeconds` went from 120 to 300, because `npm install` and first restores are slow.

### `npm` / `npx` on Windows
`npm` and `npx` are `.cmd` scripts, which can't be started by name without a shell. `run_command` now looks the program up on `PATH`, trying `.exe`, then `.cmd`, then `.bat` in each folder.

**Safety rule:** Windows runs `.cmd`/`.bat` files through `cmd.exe`, which acts on `& | < > ^ % ! " ( )` even *inside* an argument. For those programs, any argument containing one of these characters, or a line break, is refused. Normal npm arguments (`vite@latest`, `--template react-ts`, package names) don't need them.

### Persona and prompts
- **Never check work with a command that keeps running** (`npm run dev`, or `dotnet run` on a web app); use builds and tests instead.
- **New `Agent:ExtraPromptFiles` setting:** lists extra prompt files from `Prompts/` to append to the persona (the `{{EXTRA_INSTRUCTIONS}}` placeholder).
- **New `Prompts/filemaker-conversion.md`** covers:
  - **Inputs:** `fm-source/` is read-only, and DDRs are read with `search_files` plus paged `read_file`.
  - **Progress:** the `MIGRATION.md` checklist.
  - **The target layout.**
  - **How FileMaker maps to the new app:**

    | FileMaker | Becomes |
    |---|---|
    | Table | EF Core entity |
    | Relationship graph | Foreign keys and navigation properties (table occurrences are not tables) |
    | Calculation field | Computed property or SQL computed column |
    | Summary field | Aggregate query |
    | Auto-enter and validation options | Defaults and validation |
    | Value list | Enum or lookup table |
    | Layout | React page |
    | Portal | Child grid |
    | Script | API endpoint or service |
    | Privilege set | Roles |
    | Container field | Blob storage |

  - **The workflow order**, with build and test checks after each step.

### Search
`search_files` also skips the front-end build folders `dist`, `.vite` and `coverage`, as well as `node_modules`.

### More forgiving reply parsing
When writing long file contents (a 4,000-character `MIGRATION.md`), the AI sometimes closed its JSON object one brace too early and then added more, like `…"}},"message":""}`. That made the whole reply invalid, and the retries repeated the same mistake.

`AgentResponse.TryParse` now reads the **first complete JSON object** and ignores anything after it. The request before the stray text is complete and valid, so it gets used. Truly invalid JSON is still rejected with a specific error: raw line breaks or unescaped quotes inside strings, a missing `action`, or truncated JSON.

## Security notes
- **Downloads from the internet:**
  - `npm install <package>` and `dotnet add package` download code. npm packages can run **install scripts** on your machine.
  - `npm create vite@latest` downloads and runs the Vite scaffolder.
  - Keep `Agent:RequireApproval` on (`true`) for conversion work, and read each `run_command` before answering `y`.
- **`fm-source/` read-only:** this is an instruction in the guide, not yet enforced in code. Phase 5 adds the code-level check. Until then, keep a copy of the DDR outside the workspace.
- **Unchanged protections:** the workspace path guard, the workspace-folder guard, running without a shell, and the allow-list all still apply.

## Roadmap

| Phase | What | Status |
|---|---|---|
| 1 | **Foundation**: npm support, allow-list, conversion guide, `MIGRATION.md` convention, docs | Done |
| 2 | **DDR tool** `fm_ddr(section, name?)` | Next |
| 3 | **Workplace chatbot** set-up and multi-session runs | Planned |
| 4 | **Data migration** | Planned |
| 5 | **Safety** hardening | Planned |
| 6 | **End-to-end verification** of the generated app | Planned |

### Phase 2: DDR tool
A read-only tool, so no approval is needed. It parses the DDR XML once and returns compact text that fits in one tool result (`MaxReadChars`). That's much faster and more reliable than paging through megabytes of XML with `read_file`.

Sections:
- `overview`: files, table counts, layout counts, script counts
- `tables` / `table <name>`: fields with types, calculation formulas, auto-enter and validation options
- `relationships`: the relationship graph, mapping table occurrences to base tables, with the join fields
- `layouts` / `layout <name>`: the fields shown, portals, buttons and the scripts they run
- `scripts` / `script <name>`: the script steps as text
- `value_lists`, `privileges`

### Phase 3: Workplace chatbot
- Set its selectors and "reply finished" signal in `Browser:Selectors`. That's config only, if the site behaves like a standard chat page.
- Optionally start a **new chat per phase**, with each new chat starting by reading `MIGRATION.md`. That keeps every conversation short and reliable.

### Phase 4: Data migration
- Turn FileMaker record exports (CSV or Excel) into seed or import code.
- `dotnet ef` migrations and a database update. This needs the EF tool installed (`dotnet tool install dotnet-ef`) and specific allow-list entries, such as `dotnet ef migrations add` and `dotnet ef database update`.

### Phase 5: Safety
- `Agent:ReadOnlyPaths` (for example `["fm-source"]`), enforced by the write tools, so the source of truth can't be changed even by mistake.
- Optionally an allow-list of npm and NuGet packages for `npm install` and `dotnet add package`.

### Phase 6: Verification
- Playwright end-to-end tests of the generated React app against the API.
- Compare each layout's fields and portals in `docs/fm/` against the pages that were built.

## Verified so far
Phase 1 checks:
- **Offline: 86 of 86 tool checks pass.** This includes:
  - `npm run build` starting through `npm.cmd`;
  - `&`, `|` and `%` refused inside arguments for `.cmd` programs;
  - `npm run dev` not allowed;
  - "not found on PATH" for missing programs;
  - search skipping `dist/`, `.vite/` and `coverage/`.
- **Offline: 9 of 9 persona checks pass.** The guide is inserted before the acknowledge line, no placeholder is left, and a missing guide file gives a clear error.
- **Offline: 11 of 11 parser checks pass,** including the AI's real "closed too early" reply, which is now accepted with its full content intact.
- **Live** (logged-out ChatGPT, scratch workspace, one session each, approval off):

  | Session | Result |
  |---|---|
  | `dotnet new webapi -o src/Api` then `dotnet build` | Pass. The project was created and the build succeeded |
  | `npm create vite@latest src/Web -- --template react-ts`, `npm install`, `npm run build` | Pass. These ran through `npm.cmd`; 27 packages, 0 vulnerabilities, and `src/Web/dist` was produced. `create-vite` asked no interactive questions with stdin closed |
  | With the guide on, "start the migration" from a sample DDR (2 tables, 1 layout with a portal, 1 script, 1 value list) | Pass. The AI read the DDR and created `MIGRATION.md` (a checklist across all 6 phases, covering every table, layout, script and value list). It re-read the checklist, then began phase 1 by writing `docs/fm/Contacts.md` and `docs/fm/Orders.md`. `fm-source/` was unchanged |

- **Finding:** with the guide on, the AI spends its first few messages on `MIGRATION.md` and the DDR before acting, as instructed. Logged-out ChatGPT's limit of about 6 messages then stops it, so **real conversions need the workplace chatbot (phase 3)**. The phase 2 DDR tool will also cut down the messages spent reading the DDR.
