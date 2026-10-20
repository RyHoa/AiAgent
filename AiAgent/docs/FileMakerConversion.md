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
| Front end | `npm view`, `npm install`, `npm ci`, `npm run build`, `npm run lint`, `npm test` |

Some commands are deliberately **not** allowed:
- **plain `dotnet new`**: that would also allow `dotnet new install`, which installs template packages;
- **`npm run dev`**: it starts a server that never exits;
- **`npm create`**: the company feed has approved packages only, with no upstream to npmjs.com, so scaffolders like `create-vite` aren't available (see below).

`CommandTimeoutSeconds` went from 120 to 300, because `npm install` and first restores are slow.

### `npm` / `npx` on Windows
`npm` and `npx` are `.cmd` scripts, which can't be started by name without a shell. `run_command` now looks the program up on `PATH`, trying `.exe`, then `.cmd`, then `.bat` in each folder.

**Safety rule:** Windows runs `.cmd`/`.bat` files through `cmd.exe`, which acts on `& | < > ^ % ! " ( )` even *inside* an argument. For those programs, any argument containing one of these characters, or a line break, is refused. Normal npm arguments (`vite@latest`, `--template react-ts`, package names) don't need them.

### npm packages come only from the Azure DevOps feed
At work, npm packages are installed from an **Azure DevOps Artifacts feed** that holds **approved packages only**.
- **Where npm finds the feed:** the feed address and its login token live in **your user-level `.npmrc`** (`%USERPROFILE%\.npmrc`), which is already set up on the work machine. `run_command` uses your user environment, so `npm install` uses the feed automatically.
- **The AI never handles feed settings.** It doesn't create or edit any `.npmrc`, and the feed address isn't in this project's config.

This also makes the feed an **allow-list of approved packages**. To keep the AI from getting around it, the following is enforced in code, not just in the prompts:

| Blocked | How | Why |
|---|---|---|
| Creating, changing, moving or deleting any `.npmrc` in the workspace (any capitalisation) | `WorkspaceGuard.ResolveWritablePath`, used by `create_file`, `write_file`, `edit_file`, `delete_file` and `move_file` | A project `.npmrc` could point npm at another registry. Reading one is still allowed |
| npm options containing `registry`, `userconfig` or `globalconfig` (`--registry`, `--@scope:registry`, `--userconfig`, …) | `CommandTools.CheckNpmPackageSource` | These switch npm to another registry or config file |
| Package specs that download from somewhere else: anything with `://`, `git+…`, `git:`, `github:`, `gitlab:`, `bitbucket:`, `gist:` | Same | These download from outside the feed |

**What the AI does instead** (from the conversion guide):
- **Scaffolding by hand:** `npm create` isn't available, so the AI creates `src/Web` itself: `package.json` (with `build` and `test` scripts), `vite.config.ts`, `tsconfig.json`, `index.html`, `src/main.tsx` and `src/App.tsx`. Then it runs `npm install`.
- **Checking the feed first:** before adding a dependency, the AI runs `npm view <package> versions` and uses a version the feed lists.
- **Missing packages:** if a package isn't on the feed, the AI asks you (`ask_user`) instead of looking for another source.

**NuGet** packages (`dotnet add package`) still come from nuget.org, so there's no change there.

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
- **Downloaded packages:**
  - `npm install` only reaches the approved-packages feed, and the ways around it are blocked (see above). Those packages can still run **install scripts** on your machine.
  - `dotnet add package` downloads from nuget.org with no package allow-list.
  - Keep `Agent:RequireApproval` on (`true`) for conversion work, and read each `run_command` before answering `y`.
- **`fm-source/` read-only:** this is an instruction in the guide, not yet enforced in code. Phase 5 adds the code-level check. Until then, keep a copy of the DDR outside the workspace.
- **Unchanged protections:** the workspace path guard, the workspace-folder guard, running without a shell, and the allow-list all still apply.

## Roadmap

| Phase | What | Status |
|---|---|---|
| 1 | **Foundation**: npm support, allow-list, conversion guide, `MIGRATION.md` convention, company npm feed only, docs | Done |
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
- Optionally a package allow-list for `dotnet add package` (NuGet). npm is already limited to the approved-packages feed.

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
- **Offline: 24 more checks pass for the company-feed rules and the missing-`working_dir` message** (110 tool checks in total):
  - **Refused:** `--registry=` and `--registry <url>`, `--@scope:registry`, `--userconfig`, `--globalconfig`, tarball URLs, `github:` and `git+https:` specs, and `npm create`.
  - **Still working:** `npm view`, `npm run build` and `dotnet`.
  - **`.npmrc`:** it can't be created, written, edited, deleted, or moved in or out (any capitalisation); it can still be read, and the files next to it are unaffected.
- **Offline: 9 of 9 persona checks pass.** The guide is inserted before the acknowledge line, no placeholder is left, and a missing guide file gives a clear error.
- **Offline: 11 of 11 parser checks pass,** including the AI's real "closed too early" reply, which is now accepted with its full content intact.
- **Live** (logged-out ChatGPT, scratch workspace, one session each, approval off):

  | Session | Result |
  |---|---|
  | `dotnet new webapi -o src/Api` then `dotnet build` | Pass. The project was created and the build succeeded |
  | `npm create vite@latest src/Web -- --template react-ts`, `npm install`, `npm run build` | Pass. These ran through `npm.cmd` against the public registry; 27 packages, 0 vulnerabilities, and `src/Web/dist` was produced. This proved `.cmd` support. `npm create` was **removed afterwards** because the company feed has no `create-vite` |
  | With the guide on, "start the migration" from a sample DDR (2 tables, 1 layout with a portal, 1 script, 1 value list) | Pass. The AI read the DDR and created `MIGRATION.md` (a checklist across all 6 phases, covering every table, layout, script and value list). It re-read the checklist, then began phase 1 by writing `docs/fm/Contacts.md` and `docs/fm/Orders.md`. `fm-source/` was unchanged |

- **Live, company-feed rules** (logged-out ChatGPT, public registry standing in for the feed):

  | Session | Result |
  |---|---|
  | "Check which versions of react our feed has" | Pass. The AI ran `npm view react versions` and reported the latest |
  | Asked to install with `--registry=https://registry.npmjs.org` | The AI **ignored the bypass** and followed the guide instead: it checked the feed with `npm view left-pad versions`, then started writing `package.json`. The code-level refusal wasn't triggered live; the offline checks cover it. No `.npmrc` was created |
  | "Create `src/Web` by hand, then install and build" | Not finished. The AI checked the feed for `react`, `vite` and `typescript` (`npm view`), as instructed, and then hit the logged-out message limit before writing the files. It never tried `npm create` |

- **Fix found live:** running a command with a `working_dir` that doesn't exist yet used to give only Windows' "An error occurred trying to start process … with working directory …". It now says "working_dir 'src/Web' does not exist. Create it first…" (offline check 110).
- **Finding:** with the guide on, the AI spends its first few messages on `MIGRATION.md` and the DDR before acting, as instructed. Logged-out ChatGPT's limit of about 6 messages then stops it, so **real conversions need the workplace chatbot (phase 3)**. The phase 2 DDR tool will also cut down the messages spent reading the DDR.
