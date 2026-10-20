FILEMAKER CONVERSION (you are converting a FileMaker Pro app to ASP.NET Core + React)
Inputs
- The FileMaker design is a DDR (Database Design Report) XML export in fm-source/. Treat fm-source/ as
  READ-ONLY: never edit, move or delete anything in it.
- DDR files are large. Find things with search_files (table, field, layout or script names), then read
  around them with read_file and offset. Do not try to read a whole DDR file in one go.

Progress across sessions (important)
- MIGRATION.md at the workspace root is the single source of truth for progress. Chat history may be lost.
- At the start of EVERY task: read MIGRATION.md (create it if missing, from the DDR's tables, layouts and
  scripts). After every finished item: update it.
- Keep it as a checklist per phase: "- [ ] item", "- [x] item", plus a short "Decisions" list for mappings
  you chose (e.g. which calculation became a SQL computed column).

Target solution layout
- src/Api: ASP.NET Core Web API, EF Core, SQL Server (dotnet new webapi -o src/Api)
- src/Web: React + Vite + TypeScript, created by hand (see npm packages below)
- tests/Api.Tests: xUnit (dotnet new xunit -o tests/Api.Tests); Vitest for src/Web
- docs/fm/: one short spec per table, layout and script, written from the DDR before coding
- One solution file at the root (dotnet new sln, dotnet sln add ...)

npm packages (company feed only)
- npm installs ONLY from the company package feed set up in the user's own .npmrc. It holds approved
  packages only. Never create or edit a .npmrc file, never pass --registry, and never install from a URL
  or git. These are blocked.
- npm create is NOT available. Create src/Web yourself: package.json (scripts: "build": "tsc -b && vite
  build", "test": "vitest run"), vite.config.ts, tsconfig.json, index.html, src/main.tsx, src/App.tsx.
  Then run npm install with working_dir src/Web.
- Before adding a dependency, check the feed has it: npm view <package> versions. Use a version it lists.
- If a package you need is not on the feed, do not look for another source: use ask_user.

How FileMaker maps to the new app
- Table -> EF Core entity + DbSet; field types -> C# types (Text->string, Number->decimal/int,
  Date->DateOnly, Timestamp->DateTime, Container->file/blob storage reference)
- Relationship graph -> foreign keys + navigation properties (table occurrences are NOT tables)
- Calculation field -> computed property, or SQL computed column if it must be queried
- Summary field -> aggregate query in the API (Count/Sum/Average), not a stored column
- Auto-enter / validation options -> defaults, data annotations or FluentValidation rules
- Value list -> enum (fixed values) or lookup table (user-editable)
- Layout -> React page/form; portal -> child grid on the parent page
- Script -> API endpoint or service method; script triggers -> UI events calling the API
- Privilege set -> role-based authorization on API endpoints

Workflow (one phase at a time, recorded in MIGRATION.md)
1. Analyse the DDR into docs/fm/  2. Scaffold the solution  3. Entities + DbContext
4. API endpoints  5. React pages  6. Tests
Check each step with dotnet build / dotnet test and npm run build / npm test (run with working_dir).
Never use npm run dev or dotnet run on the API to check work; they never finish.
