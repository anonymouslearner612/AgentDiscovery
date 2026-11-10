# Shadow AI Agent Discovery: Phase 1

Finds AI agents built with coding frameworks on a Windows endpoint, including ones nobody registered
("shadow AI"). The design is in [CLAUDE.md](CLAUDE.md) (section "Design"), together with the coding rules; this README
explains what is built, how to run it, and how the code is organised.

## Status: all Phase 1 plan steps are built

| Plan step (design Section 11) | What exists | Verified how |
|---|---|---|
| 1. Detection library | Rules for all 14 Python frameworks, profiler, capability and risk classifier | Unit tests |
| 2. Scanner on given roots | `ShadowAgents scan`: candidates, pre-check, SQLite cache, JSON lines, Job Object, background priority | Unit + end-to-end tests, live runs |
| 3. Machine-wide discovery | NTFS master-file-table indexer (`scan` with no folder), folder-walk fallback, loose-file rules | Fallback run live; **file-table path not run live** (needs admin) |
| 4. Continuous mode | `ShadowAgents watch`: USN journal reader, change batcher, cron scheduler, scanner launcher, change-only outbox | Unit tests, live baseline; **journal path not run live** (needs admin) |
| 5. Running state | WMI process events, command-line → project matching, proxy correlation, observed models | Unit tests + live run with a real Python agent and a proxy event |
| 6. More languages | TS/JS (7 frameworks), C# via Roslyn (4 frameworks), n8n workflows | Unit + end-to-end tests |
| 7. Hardening | Hostile-input tests, journal reset/wrap → new baseline, non-NTFS fallback, scan telemetry, pause on battery or load | Unit tests |

Not done, because they need time or machines rather than code: the 7-day soak test, the 1M-file
performance run, the comparison against ai-surface on its corpus, and a run as administrator/SYSTEM
(which exercises the NTFS file table and the change journal).

## Run it in VS Code

1. Open the `AgentDiscovery` folder in VS Code (File > Open Folder). The C# Dev Kit extension is needed.
2. Open **Run and Debug** (Ctrl+Shift+D), pick a configuration, press **F5**:
   - *scan: my user profile*, *scan: every drive*, *watch*. Progress prints in the TERMINAL panel;
     results are saved in the `results` folder.
3. Set breakpoints by clicking left of a line number. Start reading at `src/ShadowAgents/Program.cs`.

Tasks (Terminal > Run Task): `build` (Ctrl+Shift+B), `test`, `publish`.

## Folder layout

```
AgentDiscovery/
  src/ShadowAgents/            the one program: ShadowAgents.exe (one namespace: ShadowAgents)
    Program.cs                 START HERE: the "scan" and "watch" commands
    Settings.cs                every tunable value (limits, budgets, schedules), hard-coded
    Contracts.cs               the job file and output record shapes (design Section 10)
    Scanner/                   "scan": finds candidates, reads files, writes JSON lines
    Watcher/                   "watch": journal, schedules, processes, proxy events, inventory
    Detection/                 reads source text and finds agents (Python, TS/JS, C#, n8n)
    Windows/                   Windows API calls: NTFS, change journal, Job Object, power and CPU
  tests/ShadowAgents.Tests/    all tests (137)
  ShadowAgents.slnx            solution file (opens in VS Code and Visual Studio)
```

```
ShadowAgents watch  (always running; in production this logic moves into the endpoint agent)
   │  reads the NTFS change journal, watches process starts, reads proxy events, runs schedules
   │  writes a job file and starts ──►  ShadowAgents scan --job ... (separate process, background
   │                                       priority, Job Object; reads files, detects agents)
   │  ◄── reads its JSON lines output  ◄── one JSON line per finding + a summary
   ▼
inventory.json  ──►  outbox.jsonl  (only new / changed / removed findings, for the backend)
```

### Inside Detection/

```
Detection/
  SourceDetector.cs          picks the language detector from the file extension; shared result types
  SourceCode.cs              shared: which characters are code / string / comment, bracket matching
  Python*.cs                 PythonSource, PythonValueReader, PythonAgentDetector, notebooks + CrewAI YAML
  JavaScript*.cs             JavaScriptSource, JavaScriptValueReader, JavaScriptAgentDetector
  CSharpAgentDetector.cs     Roslyn syntax trees, no compilation
  N8n.cs                     workflow parser and the SQLite reader
  *FrameworkRules.cs         framework rules as data, package-name and display-name catalogs
  Profiling.cs               capabilities, risk flags, confidence, secret filter, model providers
```

Every language detector follows the same five steps:

1. **Pre-check**: is any framework name in the file at all? Most files stop here.
2. **Imports**: which frameworks does the file really import (not just mention in a comment)?
3. **Constructors**: find calls such as `Agent(` or `new Agent({`, find where each call ends, split its arguments.
4. **Profile**: read name, description, model and tools from the arguments, following variables to their assignments.
5. **Finish**: attach tools defined elsewhere in the file, merge wrappers (`AgentExecutor(agent=...)`), classify capabilities.

## Build, test, run from a terminal

Targets **.NET Framework 4.6.2** (x64). Building requires the .NET SDK and the .NET Framework 4.6.2 targeting pack.

```powershell
dotnet build ShadowAgents.slnx
dotnet test ShadowAgents.slnx

# Creates publish\ with ShadowAgents.exe and its DLLs (copy the whole folder; .NET Framework 4.6.2 is built into Windows)
dotnet publish src/ShadowAgents -c Release

# Scan one folder, or every fixed drive (results saved in the "results" folder, progress on screen)
publish\ShadowAgents.exe scan C:\Users\alice\code
publish\ShadowAgents.exe scan

# Watch continuously (Ctrl+C to stop)
publish\ShadowAgents.exe watch
```

There are no other options: limits, budgets and schedules are constants in `Settings.cs`. Run **as administrator**
(or as SYSTEM, like the endpoint agent) to use the NTFS file table and change journal; without that it
falls back to walking folders and to scheduled scans, and says so in the output.
### Scanner output

One JSON object per line. `recordType` is `finding` or `summary`; the last line is the summary, which
includes `listingMethods` (for example `C:: master-file-table` or `D:: folder-walk (access denied ...)`),
the work done, and whether the scan stopped early. Findings follow Section 10 of the design doc, plus
`environment` on Installed records and `evidence[].cell` for notebooks. Exit codes: 0 done, 1 error,
2 bad arguments, 3 stopped early (Ctrl+C).

### Watcher files (in the state folder)

| File | What |
|---|---|
| `outbox.jsonl` | Changes for the backend: `{"change":"new|changed|removed","at":...,"record":{...}}` |
| `inventory.json` | Every current finding, including Running state |
| `telemetry.jsonl` | Cost of every scan: reason, duration, files read, bytes, cache hits |
| `watcher-state.json` | Journal positions per drive, last scan times |
| `scan-cache.db` | The scanner's cache |
| `proxy-events.jsonl` | **Input** from Titanium Proxy: `{"processId":1234,"url":"https://api.openai.com/...","body":"..."}` |

When the watcher moves into the endpoint agent, Titanium's request handler should call
`WatcherService.OnProxyRequest(processId, url, body)` directly instead of writing the file, and the
backend uplink should replace `ChangeOutbox`.

## Decisions made while building (not spelled out in the design doc)

- **Python environments are read, not skipped.** `.venv`, `venv`, `env` and conda folders are recognised
  by `pyvenv.cfg`, `conda-meta` or `python.exe` + `Lib`. Their `*.dist-info` folder names give the
  Installed state; their files are never read.
- **Per-user app and cache folders are skipped**, such as `AppData\Local\Programs`, `.npm`, `.cursor` and
  `.vscode-server`. They hold thousands of JS/Python files that ship with tools and are never hand-written agents.
- **Machine-wide scans keep a loose source file only under a user profile or inside a project**
  (a manifest folder or git repository). Folder scans (`scan <folder>`) keep everything.
- **Capabilities come from tool names only, not the description.** Generic prompt words ("help the user")
  matched almost every agent.
- **Nested manifests stay separate projects** (monorepos); lock files are ignored when a direct manifest
  exists in the same folder (they list dependencies of dependencies).
- **Wrappers are merged:** `AgentExecutor(agent=...)` and `ClaudeSDKClient(options=...)` are one agent.
- **Vercel AI SDK `generateText`/`streamText` and `Microsoft.Extensions.AI` `ChatOptions` count as agents
  only with tools attached**, as the design doc says.
- **The watcher only removes findings after a complete scan.** A scan that crashed or hit its time budget
  never deletes anything from the inventory.
- **Processes running code from skipped folders** (Program Files, `node_modules`, `bin`, ...) never trigger
  a scan: those are tools, not projects.
- **Owner** is the profile folder name (`C:\Users\<name>`) until open question 6 is settled.
- **Bump `ScanCache.DetectionRulesVersion`** whenever rules change, so cached results are discarded.

## Adding or changing a framework rule

1. Edit or add a rule in `Detection/PythonFrameworkRules.cs` or `Detection/JavaScriptFrameworkRules.cs`.
   Rules are data: module roots, constructor names, and which argument holds the name, description,
   model and tools. (C# rules are code in `Detection/CSharpAgentDetector.cs`.)
2. Add package names to `PackageCatalog` and the display name to `FrameworkCatalog` (both in `Detection/FrameworkRules.cs`).
3. Add a positive test and, if the framework shares a constructor name such as `Agent`, an ambiguity test.
4. Bump `ScanCache.DetectionRulesVersion`.

## New to C#? What you will see in this code

- The code avoids compact syntax on purpose: no `? :`, `??`, `?.`, `=>` lambdas, `[..]` ranges or `[ ]` collection
  expressions. Nullable checking is off, so any reference (such as a `string`) may be `null`; the code checks with `== null`.
- `public string Name { get; set; }`: a property. `{ get; }` alone means it can only be set in the constructor.
- `new FrameworkRule { Key = "crewai", ... }`: create an object and set properties in one statement.
- `Nullable<int> Cell`: a number that can also be "no value" (`null`), used where a plain `int` cannot be empty.
- `List<string>`, `Dictionary<string, int>`, `HashSet<string>`: list, key→value map, set without duplicates.
- `out int bytesRead`: the method writes a second result into this variable.
- `using FileStream stream = ...;`: the file is closed automatically at the end of the block.
- `ModelReference` overrides `Equals` so two references to the same model count as one (used to avoid duplicates).
- `static readonly Regex X = new Regex(..., RegexOptions.Compiled, timeout)`: a regular expression built once, with a time limit so hostile input cannot hang the scan.
- `[DllImport("kernel32.dll")]` (in `src/ShadowAgents/Windows/NtfsVolume.cs`): calls a Windows C function.
