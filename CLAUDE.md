# ShadowAgents: Shadow AI agent discovery

A Windows console program that finds AI agents built with coding frameworks (Python, TS/JS, C#, n8n)
on an endpoint, including unregistered ones ("shadow AI"). The full design is the second half of this
file ("Design"). Overview, folder layout and how to run it: `README.md`.

The code is kept deliberately simple so that an engineer new to C# can maintain it.
These rules add to the base coding standards in `~/.claude/CLAUDE.md`.

## Target framework

- **The requirement is .NET Framework 4.6.2** (`net462`, x64), for the program and the tests. It ships with
  every supported Windows version, so nothing extra has to be installed on endpoints.
- **Use .NET 10 only if something truly cannot be done on .NET Framework 4.6.2**, and say why in the code
  and in this file. Prefer a plain .NET Framework replacement first (for example a loop instead of
  `SearchValues`, a `static readonly Regex` instead of `[GeneratedRegex]`, `Marshal.GetLastWin32Error()`
  instead of `GetLastPInvokeError()`).
- Only use NuGet packages that support `net462` or `netstandard2.0`.
- Building needs the .NET SDK plus the .NET Framework 4.6.2 targeting pack; `dotnet build` and
  `dotnet test` work as usual.

## Commands

```powershell
dotnet build ShadowAgents.slnx
dotnet test ShadowAgents.slnx
dotnet publish src/ShadowAgents -c Release      # -> publish\ (ShadowAgents.exe and its DLLs)

ShadowAgents scan <folder>    # scan one folder
ShadowAgents scan             # scan every fixed drive (best as administrator)
ShadowAgents watch            # keep watching (Ctrl+C to stop)
```

Before saying a change is done, build with no warnings (warnings are errors) and run all tests.

## Keep the project small

- **Never remove a feature** (watcher, NTFS scan, cache, any language detector) to simplify. Simplify
  how it is done instead, and ask before dropping any capability.
- **No new command-line options or config files.** Every tunable value is a constant in
  `src/ShadowAgents/Settings.cs`. Add new values there.
- **Few files.** Put a small new class in the existing file it belongs with. Create a new file only
  for a large, separate piece of logic.
- **One namespace:** `ShadowAgents` for all program code, `ShadowAgents.Tests` for tests.

## Plain C# only (in `src/`)

Write code an average engineer reads at once, even if it is longer.

- **Never start a name with an underscore.** This covers fields, variables, parameters, methods and discards:
  `scanQueue`, not `_scanQueue`; give an unused `out` value a real name instead of `out _`.
- **No `?` in any form:** no `a ? b : c`, `??`, `??=`, `?.`, and no `string?` annotations
  (nullable checking is off). Use `if`/`else` and `== null` checks. For a value type that can be empty,
  write `Nullable<int>`, not `int?`.
- **No `=>`:** no lambdas and no expression-bodied members. Use named methods (for example
  `list.Sort(CompareByPath)`) and normal `{ return ...; }` bodies. Write properties as `{ get { return ...; } }`.
- **No compact syntax:** no ranges or index-from-end (`text[a..b]`, `list[^1]`); use `Substring` and
  `list[list.Count - 1]`. No collection expressions (`[...]`); use `new List<string> { ... }` or
  `new string[] { ... }`. No `with` expressions, records, tuples or deconstruction, switch expressions,
  `is X or Y` / `is not` patterns, `required` / `init`, or LINQ chains.
- Prefer plain loops, early returns and small, well-named helper methods.
- Names say what a value is for (`matchingAgents`, not `x` or `tmp`).
- Comment the non-obvious *why*, not what the code already says.

Tests (`tests/ShadowAgents.Tests`) may keep short xUnit lambdas such as
`Assert.Contains(findings, finding => finding.Name == "PyBot")`.

## When changing detection

- Rules are data in `Detection/PythonFrameworkRules.cs` and `Detection/JavaScriptFrameworkRules.cs`;
  package and display names are in `Detection/FrameworkRules.cs`.
- Add a positive test, plus an ambiguity test when a constructor name such as `Agent` is shared.
- Bump `ScanCache.DetectionRulesVersion` whenever rules change, so cached results are discarded.

---

# Design

Design and implementation plan for Shadow AI Agent discovery in the endpoint agent (Phase 1: coding
frameworks). Written to be picked up later by an engineer who was not part of the original discussion.
Code comments refer to its sections, for example "design doc Section 9" means "9. Performance budgets" below.

- **Status:** Phase 1 built: plan steps 1–7 (see [README.md](README.md) for what was verified live and
  what still needs an administrator run, the soak test and the performance run).
- **Language:** C# on .NET Framework 4.6.2 (see "Target framework" above; .NET 10 only where 4.6.2 cannot work).
- **Phase 1 scope:** AI agents built with coding frameworks. MCP servers come
  next (see [Section 12](#12-phase-2-mcp-servers)).

---

### 1. Problem

The product already has an endpoint agent on each machine, acting as a proxy
through Titanium Proxy. We want it to also find **AI agents that were built or
are running on the endpoint**. That includes agents nobody registered: personal
projects, scripts, notebooks and unapproved apps ("Shadow AI").

Finding a framework installed on the machine is **not enough**. We need to know
whether a real agent was built with it, what that agent can do, and whether it
is running.

### 2. Requirements

| # | Requirement | Notes |
|---|---|---|
| R1 | Find real agent implementations, not just framework dependencies | See the four states in Section 3 |
| R2 | Profile each agent: name, description, model, tools, capabilities, project | Best effort; every field is optional |
| R3 | Shadow discovery | No predefined or user-chosen folders. Must find unknown projects anywhere on the machine. |
| R4 | Low CPU, memory and disk I/O | Budgets in Section 9 |
| R5 | Continuous **and** scheduled discovery | Both, with configurable schedules |
| R6 | C# only | P/Invoke to OS APIs and native C libraries is allowed |

#### Frameworks in scope

Python: LangChain, LangGraph, CrewAI, AutoGen (0.2 / AG2 and 0.4+
`autogen_agentchat`), Semantic Kernel, LlamaIndex, Pydantic AI, OpenAI Agents
SDK, AWS Strands, Claude Agent SDK, Haystack, Google ADK, smolagents.

TypeScript/JavaScript: LangChain.js, LangGraph.js, Vercel AI SDK, Mastra,
OpenAI Agents SDK (JS), LlamaIndex.TS, Claude Agent SDK (TS).

.NET: Semantic Kernel, Microsoft Agent Framework, AutoGen.NET,
Microsoft.Extensions.AI.

Low-code: n8n (local installs).

### 3. The four discovery states

Every finding gets exactly one state. Higher states include the lower ones.

| State | Meaning | Evidence | Typical confidence |
|---|---|---|---|
| **Installed** | The framework package exists in an environment | Package folder in `site-packages`, `node_modules` or the NuGet cache. Folder name only; never scan inside. | High that it's installed; says nothing about agents |
| **Referenced** | A project depends on or imports the framework | Manifest dependency (`pyproject.toml`, `requirements*.txt`, `package.json`, `*.csproj`), or an import line in the project's own source | High |
| **Implemented** | The project builds an agent with the framework | An agent constructor or graph, usually with tools, model or instructions (Section 7) | Medium to high, depending on the rule |
| **Running** | That agent is executing now, or ran recently | A process whose command line points into the project, or proxy traffic from that process to an LLM or MCP endpoint | High |

Only **Implemented** and **Running** findings count as agents. Report
**Installed** and **Referenced** as inventory, for context and trends.

### 4. Key decisions

#### 4.1 Stay with C#, not Rust or Go

- **The cost is in disk I/O, not computation.** The work is listing files,
  reading small text files and running regex. A better design (never walking
  the disk) beats any choice of language.
- **.NET covers what we need:**
  - Compiled regexes (`RegexOptions.Compiled`) with match timeouts, so hostile
    input can't hang the scanner
  - `IndexOf` for a fast "mentions a framework at all" pre-check
  - P/Invoke for NTFS and Job Object APIs, and `TraceEvent` for ETW
- **Roslyn** (`Microsoft.CodeAnalysis.CSharp`) is the only serious parser for
  C# code, which we need for .NET agents. It exists only in .NET.
- Titanium Proxy and the existing agent are .NET, so we share data models,
  build, code signing and team skills.
- **What Rust or Go would really win:** idle memory (roughly 5–15 MB against
  30–60 MB for a .NET process) and startup time. We avoid that cost by design:
  the always-running part lives in the existing agent, and the scanner is a
  short-lived process.
- **If parsing becomes the slow part:** tree-sitter is a C library. Call it
  through P/Invoke rather than switching language.
- **Watch out:** Roslyn does not work with ahead-of-time compilation. On .NET
  Framework the exe is a normal JIT-compiled program, which is what Roslyn needs.

#### 4.2 Watcher inside the agent, scanner as a separate exe

- The **watcher** must run all the time and stay light, so it lives in the
  existing agent, which is already resident.
- The **scanner** does the heavy, bursty work. As a separate process it can run
  at background priority, sit inside a Job Object with CPU and memory caps, be
  killed and resumed safely, and release all its memory when it exits. A crash
  in parsing cannot take down the proxy.

#### 4.3 Never walk the whole filesystem

- Use the NTFS **master file table** to list file *names* (no file opens) for
  the first baseline.
- Use the NTFS **change journal (USN)** to learn about changes afterwards.
- Read file *contents* only for candidate files in candidate project folders.

#### 4.4 Never scan inside dependency folders

`site-packages`, `node_modules`, `.venv`, NuGet caches and similar. A folder
name tells us a package is **Installed**; its contents are never read.

### 5. Architecture

```
+--------------------------- Endpoint Agent (resident) ----------------------------+
|                                                                                  |
|  Titanium Proxy ----> LLM/MCP traffic attributed to process ID ----+             |
|                                                                    |             |
|  Discovery Watcher                                                 v             |
|    - USN journal reader (per NTFS volume) ------> Change batcher -> Work queue    |
|    - Process start events (ETW / WMI) -----------> Runtime correlator            |
|    - Scheduler (cron-like) ---------------------> Work queue                      |
|                                                        |                         |
+--------------------------------------------------------|-------------------------+
                                                         | launches with job file
                                                         v
+---------------- Scanner.exe (on demand, background priority, Job Object) --------+
|  Candidate resolver -> Pre-check -> Framework rules -> Profiler -> JSON output    |
|                          ^                                                       |
|                          +-- SQLite cache (path, size, mtime, hash, results)     |
+----------------------------------------------------------------------------------+
                                                         |
                                                         v
                                   Agent inventory -> product backend
```

#### Components

| Component | Lives in | Job |
|---|---|---|
| Volume indexer | Scanner (first baseline) | Lists every file name on each NTFS volume through the master file table. Keeps candidate paths only (Section 6.1). |
| USN journal reader | Watcher | Reads the change journal from the last saved position. Keeps only changes to candidate file names and maps each to a project root. |
| Change batcher | Watcher | Groups changes per project root and waits (for example 60 s with no further changes) before queueing a scan, so a `git checkout` or editor save storm triggers one scan. |
| Scheduler | Watcher | Scheduled full and incremental scans (Section 8) |
| Runtime correlator | Watcher | Links process starts and proxy traffic to known project roots |
| Candidate resolver | Scanner | Turns a project root into the list of source files to read |
| Pre-check | Scanner | Cheap string search for framework names; drops most files |
| Framework rules | Scanner | Per-framework detection (Section 7) |
| Profiler | Scanner | Extracts name, description, model, tools and capabilities |
| Cache | Scanner | SQLite. Skips unchanged files and stores the last results per file. |

#### Watcher-to-scanner contract

- **Input:** a job file (JSON): a list of project roots or loose files, the
  scan type (`incremental` or `full`), budgets, and the cache path.
- **Output:** JSON lines on stdout, or a results file: one record per finding
  (Section 10), plus a summary record (files read, bytes read, duration, and
  whether the scan stopped early).
- The scanner must be **idempotent and resumable**: if it is killed partway,
  the next run continues from the cache.

### 6. Discovery pipeline

#### 6.1 Stage 0: list candidate files (no file opens)

Enumerate each NTFS volume with `FSCTL_ENUM_USN_DATA` on `\\.\C:`. The USN
records give the file name, file reference number and parent reference number.
Build full paths from the parent map. This needs admin or SYSTEM rights, which
the endpoint agent already has.

Keep a path only if its file name matches a candidate pattern **and** it isn't
under a skipped folder (Section 6.6).

**Manifests (project markers):**
`pyproject.toml`, `requirements*.txt`, `Pipfile`, `Pipfile.lock`, `poetry.lock`,
`uv.lock`, `setup.py`, `setup.cfg`, `environment.yml`, `package.json`,
`*.csproj`, `*.fsproj`, `packages.config`, `Directory.Packages.props`,
`langgraph.json`, `crew.yaml` / `agents.yaml` / `tasks.yaml` (CrewAI
scaffolds), `.mcp.json`.

**Installed markers (folder names only):**
`site-packages/<pkg>` and `dist-packages/<pkg>` for `langchain*`,
`langgraph*`, `crewai*`, `autogen*`, `semantic_kernel`, `llama_index*`,
`pydantic_ai*`, `agents` (OpenAI Agents SDK: confirm through the
`openai_agents-*.dist-info` folder), `strands*`, `claude_agent_sdk`,
`haystack*`, `google/adk`, `smolagents`; `node_modules/<pkg>` for
`langchain`, `@langchain/*`, `ai`, `@ai-sdk/*`, `@mastra/*`, `@openai/agents`,
`llamaindex`, `@anthropic-ai/claude-agent-sdk`;
`%USERPROFILE%\.nuget\packages\microsoft.semantickernel*`,
`microsoft.agents.ai*`, `autogen*`, `microsoft.extensions.ai*`.

**Loose source files:** `*.py`, `*.ipynb`, `*.ts`, `*.js`, `*.mjs`, `*.cs`
under user profile folders, even when no manifest is nearby. Shadow agents
are often single scripts or notebooks.

**n8n:** `%USERPROFILE%\.n8n\database.sqlite` (and the same path under any
other user profile).

#### 6.2 Stage 1: Installed

Record each installed-marker folder as `{framework, version (from the
dist-info or package.json), environment path}`. Don't read anything below it.

#### 6.3 Stage 2: Referenced → project roots

1. Parse each manifest found. If it declares an in-scope framework, its folder
   becomes a **project root**.
2. Group loose source files by folder. Their nearest `.git` folder, or the
   folder itself, becomes a **loose root**.
3. Merge nested roots: a manifest inside another root's tree belongs to the
   nearest root.

#### 6.4 Stage 3: Implemented

For each project or loose root:

1. List source files with the right extensions, skipping folders from Section
   6.6. Cap each file at **1 MB** and each root at **5,000 files**, and report
   when a cap was hit.
2. Check the cache. If path, size and modified time are unchanged, reuse the
   stored result without reading the file.
3. **Pre-check:** read the file and look for any framework import root with
   `SearchValues<string>`. No hit means the file is done.
4. Run the framework rules (Section 7) and the profiler.
5. Merge results across the project's files: tools defined in one file and
   models defined in another still belong to the same project.

For `.ipynb` files, parse the JSON and join the code cells (`cell_type ==
"code"`) into one text, keeping a cell-to-line map for evidence.

#### 6.5 Stage 4: Running

- **Process starts:** ETW `Microsoft-Windows-Kernel-Process` (ProcessStart) via
  `Microsoft.Diagnostics.Tracing.TraceEvent`, or WMI `Win32_ProcessStartTrace`
  as a fallback. Get the command line from WMI `Win32_Process`.
- **Matching:** for `python`, `pythonw`, `py`, `uv`, `poetry`, `node`, `npx`,
  `tsx`, `bun`, `deno` and `dotnet` processes, resolve the script or project
  path from the command line, combined with the working folder where it's
  known. If the path is under a known project root, mark that project's agents
  **Running**. Then queue a scan of that root if it has never been scanned:
  this catches agents in folders the indexer missed.
- **Jupyter:** a `jupyter`/`ipykernel` process means notebooks may be live.
  Link it to the notebook folder from the kernel's connection file or command
  line where possible.
- **Proxy correlation:** Titanium sees traffic to LLM provider hosts
  (`api.openai.com`, `api.anthropic.com`, `*.openai.azure.com`,
  `generativelanguage.googleapis.com`, `bedrock-runtime.*.amazonaws.com`,
  local `:11434` Ollama, and so on). Tie each connection to its process ID, and
  the process to its project, then record the **models actually requested**
  (from the request body where the proxy can read it).
- **Recently ran:** keep `last_seen_running` for each agent so short-lived
  scripts aren't missed between checks.

#### 6.6 Always-skipped folders

`node_modules`, `site-packages`, `dist-packages`, `.venv`, `venv`, `env`
(only when it contains `pyvenv.cfg`), `__pycache__`, `.git`, `.hg`, `.svn`,
`.tox`, `.nox`, `dist`, `build`, `target`, `bin`, `obj`, `.next`, `.nuxt`,
`.cache`, `.turbo`, `.idea`, `.vs`, `.vscode`, `.pytest_cache`,
`.mypy_cache`, `.ruff_cache`, `%USERPROFILE%\.nuget`, `AppData\Local\Temp`,
`$Recycle.Bin`, `System Volume Information`, `Windows`, `Program Files*`
(installed apps are a separate discovery stream), and conda `pkgs`.

**Test code:** keep scanning `test`/`tests` folders but flag the findings
`is_test: true` instead of dropping them. Test code still shows that someone
is building agents.

### 7. Detection and profiling rules

#### 7.1 Rule model

Each framework rule has:

```text
FrameworkRule
  Key                 e.g. "crewai"
  Languages           python | ts/js | csharp
  ImportPatterns      regex: proves the file references the framework
  AgentConstructors   regex anchors: the start of an agent definition
  NameArgs            argument names that give the agent's name
  DescriptionArgs     argument names that give its description or instructions
  ModelArgs           argument names that give its model or model client
  ToolArgs            argument names that give its tool list
  ToolDecorators      decorators or attributes that define tools
```

**Standard extraction steps (port these from ai-surface's
`agent_frameworks.py`):**

1. Find a constructor anchor such as `Agent(`. Then find its matching closing
   bracket with a **bracket matcher that skips string literals**, capped at
   16 KB of scanning and 256 attempts per file. This guards against hostile
   input; don't use one big multiline regex.
2. Inside the constructor body only, pull keyword arguments with simple
   single-line regexes, for example `\brole\s*=\s*['"]([^'"\n]{1,200})['"]`.
3. **Agent name**, in priority order: the `name=` / `role=` argument, then the
   assigned variable name (`support_agent = ...`), then
   `<framework>@<file>:<line>`.
4. **Tools:** read the list in the `tools=` argument. If the argument is a
   variable name, follow it to a `NAME = [ ... ]` assignment in the same
   file. Add functions marked with tool decorators (`@tool`,
   `@function_tool`, `@agent.tool`, `@kernel_function`, `[KernelFunction]`).
5. **Model:** if the `model=` / `llm=` / `model_client=` argument is a string,
   use it. If it's a variable, follow it to its assignment in the same file
   (`llm = ChatOpenAI(model="gpt-4o")`) and take the model argument there
   (`model`, `model_name`, `model_id`, `deployment_name`, `ai_model_id`,
   `modelId`). Also accept provider-prefixed strings like `"openai:gpt-4o"`.
6. **Description:** take the first non-empty `DescriptionArgs` value. Cap it at
   1,000 characters and collapse whitespace. If it's not a literal, record
   `"<dynamic>"`.

#### 7.2 Signatures by framework

> These APIs change often. **Check every row against the current version of
> each framework before implementing.** Rows marked *(ai-surface)* are proven
> rules ported from ai-surface 1.1.1.

##### Python

| Framework | Import roots | Agent constructors | Name | Description | Model | Tools |
|---|---|---|---|---|---|---|
| LangChain *(ai-surface)* | `langchain`, `langchain_core`, `langchain_community` | `AgentExecutor`, `initialize_agent`, `create_react_agent`, `create_openai_tools_agent`, `create_tool_calling_agent`, **add `create_agent`** (LangChain v1) | variable name | `prompt=`, `system_prompt=` | `llm=`, `model=` → follow `Chat*(model=...)` | `tools=` |
| LangGraph | `langgraph` | `StateGraph(`, `MessageGraph(`, `create_react_agent(` from `langgraph.prebuilt` | variable name / `name=` | `prompt=`, `state_modifier=` | `model=` | `tools=`, `ToolNode([...])` |
| CrewAI *(ai-surface)* | `crewai` | `Agent(` (also `@agent` methods in `@CrewBase` classes) | `role=` | `goal=` + `backstory=` | `llm=` | `tools=` |
| CrewAI scaffold | YAML | `config/agents.yaml` top-level keys | key | `role`, `goal`, `backstory` | `llm` | from the matching `@agent` method |
| AutoGen 0.2 / AG2 *(ai-surface)* | `autogen` | `AssistantAgent(`, `UserProxyAgent(`, `ConversableAgent(` | `name=` | `system_message=`, `description=` | `llm_config=` → `"model"` key | `register_function(...)` |
| AutoGen 0.4+ | **`autogen_agentchat`, `autogen_ext`, `autogen_core`** (ai-surface's `autogen` root doesn't match these) | `AssistantAgent(`, `CodeExecutorAgent(` | `name=` | `system_message=`, `description=` | `model_client=` → `*ChatCompletionClient(model=...)` | `tools=` |
| Semantic Kernel | `semantic_kernel` | `ChatCompletionAgent(`, `AzureAIAgent(`, `OpenAIAssistantAgent(` | `name=` | `instructions=`, `description=` | `service=` → `*ChatCompletion(ai_model_id=...)` | `plugins=`, `@kernel_function` |
| LlamaIndex | `llama_index` | `FunctionAgent(`, `ReActAgent(`, `AgentWorkflow(`, `OpenAIAgent.from_tools(` | `name=` | `description=`, `system_prompt=` | `llm=` | `tools=`, `FunctionTool.from_defaults(fn=...)` |
| Pydantic AI *(ai-surface)* | `pydantic_ai` | `X = Agent(` | `name=` / variable | `system_prompt=`, `instructions=` | first positional string or `model=` | `tools=`, `@X.tool`, `@X.tool_plain` |
| OpenAI Agents SDK *(ai-surface)* | `from agents import Agent/Runner/function_tool/...` (never a bare `import agents`) | `X = Agent(` | `name=` | `instructions=` | `model=` | `tools=`, `handoffs=`, `@function_tool` |
| AWS Strands *(ai-surface)* | `strands` | `X = Agent(` | variable / `name=` | `system_prompt=` | `model=` → `BedrockModel(model_id=...)` | `tools=`, `@tool` |
| Claude Agent SDK *(ai-surface)* | `claude_agent_sdk` | `ClaudeSDKClient(`, `query(` with `ClaudeAgentOptions(` | variable | `system_prompt=` | `model=` | `allowed_tools=`, `mcp_servers=` |
| Google ADK | `google.adk` | `Agent(`, `LlmAgent(` | `name=` | `description=`, `instruction=` | `model=` | `tools=` |
| smolagents | `smolagents` | `CodeAgent(`, `ToolCallingAgent(` | `name=` / variable | `description=` | `model=` → `*Model(model_id=...)` | `tools=`, `@tool` |
| Haystack | `haystack` | `Agent(` (2.x) | variable | `system_prompt=` | `chat_generator=` | `tools=` |

**Ambiguity:** `Agent(` is shared by CrewAI, Pydantic AI, OpenAI Agents,
Strands, Google ADK and Haystack. Decide which one it is from the **imports in
the same file**. If several match, pick the import that names `Agent`
explicitly (`from crewai import Agent`).

##### TypeScript / JavaScript

Imports are matched as quoted package names, so `"ai"` doesn't match
`"openai"`: `(?:from|require|import)\s*\(?\s*['"](?:PKG)(?:/[^'"]*)?['"]`.

| Framework | Packages | Agent constructors | Name / description / model / tools |
|---|---|---|---|
| LangChain.js *(ai-surface)* | `langchain`, `@langchain/*` | `AgentExecutor`, `createToolCallingAgent`, `initializeAgentExecutorWithOptions`, `createAgent` | variable; `prompt`; `llm`/`model` → `new Chat*({ model })`; `tools` |
| LangGraph.js *(ai-surface)* | `@langchain/langgraph` | `new StateGraph`, `createReactAgent` | variable; `prompt`; `llm`; `tools` |
| Vercel AI SDK *(ai-surface)* | `ai`, `@ai-sdk/*` | `generateText(`, `streamText(` **with `tools:`** (without tools it's an LLM call, not an agent), `new Agent(` / `Experimental_Agent` | variable; `system`; `model: openai('gpt-4o')`; `tools` |
| Mastra *(ai-surface)* | `@mastra/core` | `new Agent({`, `createAgent(` | `name`; `instructions`; `model`; `tools` |
| OpenAI Agents JS *(ai-surface)* | `@openai/agents` | `new Agent({` | `name`; `instructions`; `model`; `tools` |
| LlamaIndex.TS *(ai-surface)* | `llamaindex` | `OpenAIAgent`, `ReActAgent`, `agent(` | variable; `systemPrompt`; `llm`; `tools` |
| Claude Agent SDK TS *(ai-surface)* | `@anthropic-ai/claude-agent-sdk` | `query({`, `ClaudeSDKClient`, `createSdkMcpServer(` | variable; `systemPrompt`; `model`; `allowedTools` |

##### C# (.NET) with Roslyn

Parse with `CSharpSyntaxTree.ParseText` (syntax only: no compilation, no
NuGet restore). Walk with a `CSharpSyntaxWalker`.

| Framework | Package / namespace | Agent definition | Name / description / model / tools |
|---|---|---|---|
| Semantic Kernel | `Microsoft.SemanticKernel*` | `new ChatCompletionAgent { ... }`, `Kernel.CreateBuilder()` with `.Add*ChatCompletion(...)` | `Name`; `Instructions` / `Description`; `modelId:` / `deploymentName:` argument; methods marked `[KernelFunction]`, `Plugins.AddFromType<T>()` |
| Microsoft Agent Framework | `Microsoft.Agents.AI*` | `.CreateAIAgent(...)`, `new ChatClientAgent(...)` | `name:`; `instructions:`; model from the chat client; `tools:` (`AIFunctionFactory.Create(...)`) |
| AutoGen.NET | `AutoGen*` | `new AssistantAgent(...)`, `new OpenAIChatAgent(...)` | `name:`; `systemMessage:`; `modelName:`; `functionMap` |
| Microsoft.Extensions.AI | `Microsoft.Extensions.AI` | `IChatClient` with `ChatOptions { Tools = ... }` | Counts as an agent **only when tools are attached**; otherwise it's an LLM call |

##### n8n

- **Store:** `%USERPROFILE%\.n8n\database.sqlite`, table `workflow_entity`,
  columns `name`, `active`, `nodes` (JSON). Open **read-only** with
  `Microsoft.Data.Sqlite` (`Mode=ReadOnly`), because n8n may be running.
  Self-hosted n8n on Postgres is out of scope for the endpoint.
- **Agent:** a node of type `@n8n/n8n-nodes-langchain.agent` (check the
  current type names).
- **Profile:** workflow `name` is the project and the node `name` is the agent.
  Description comes from the node's system message parameter. Model comes from
  the connected `lmChat*` node's model parameter. Tools are the nodes connected
  to the agent's tool input (for example `toolHttpRequest`, `toolCode`,
  `mcpClientTool`, `toolWorkflow`). Running = workflow `active == true`.

#### 7.3 Capabilities

Group tool names (and the description) into capability categories with
keyword rules. Start from ai-surface's tool-name rules
(`_classify_tools`) and extend them:

| Capability | Example tool-name tokens |
|---|---|
| `web_access` | search, browse, fetch, http, scrape, crawl, url |
| `email_messaging` | email, mail, send_message, slack, teams, sms |
| `filesystem` | file, read_file, write_file, upload, download, path |
| `code_execution` | python_repl, exec, shell, bash, terminal, code_interpreter, `CodeExecutorAgent` |
| `database` | sql, query_db, db, mongo, postgres |
| `financial` | refund, payment, charge, transfer, payout, invoice |
| `destructive` | delete, drop, remove, truncate, wipe |
| `identity_admin` | user, role, permission, grant, admin |
| `delegation` | handoffs, sub-agents, `AgentTool`, crews with more than one agent |
| `mcp` | MCP client tools, `mcp_servers=` |

Also record risk flags: `financial` or `destructive` with no human approval
step; `code_execution`; `web_access` combined with `email_messaging` (data
can be sent out).

#### 7.4 Confidence

| Level | Rule |
|---|---|
| `high` | Constructor matched **and** (tools or model or description extracted) |
| `medium` | Constructor matched, no attributes extracted |
| `low` | Framework-specific usage only (e.g. `StateGraph(` with no nodes found) |

### 8. Continuous and scheduled discovery

| Mode | Trigger | Work |
|---|---|---|
| **Baseline** | First install, or USN journal reset or wrapped around | Stage 0 file-table listing, then stages 1–3 for all roots |
| **Continuous** | USN changes (batched), process starts, proxy traffic | Stages 2–3 for the affected roots only; stage 4 runs all the time |
| **Scheduled incremental** | Configurable, default daily | Re-check roots whose cache is older than N days |
| **Scheduled full** | Configurable, default weekly | Stage 0 again, to catch anything the journal missed |
| **On demand** | Backend command | Full or for given roots |

#### USN journal details

- `FSCTL_QUERY_USN_JOURNAL` gives the `UsnJournalID` and `NextUsn`. Save both
  per volume.
- `FSCTL_READ_USN_JOURNAL`, with a reason mask for create, delete, rename and
  data changes, reads from the saved position.
- If the `UsnJournalID` changed, or the saved position is older than
  `FirstUsn` (the journal wrapped), run a **new baseline** for that volume.

#### Volumes the journal can't cover

Non-NTFS volumes (FAT, exFAT, removable drives) and network shares use a
scheduled walk with the same skip rules, or are excluded by policy.

#### Configuration (policy pushed from the backend)

```json
{
  "discovery": {
    "enabled": true,
    "continuous": true,
    "debounceSeconds": 60,
    "incrementalSchedule": "0 2 * * *",
    "fullSchedule": "0 3 * * 0",
    "volumes": ["auto"],
    "excludePaths": [],
    "maxFileBytes": 1048576,
    "maxFilesPerRoot": 5000,
    "captureDescriptions": true,
    "maxDescriptionChars": 1000,
    "budgets": { "cpuPercent": 25, "memoryMb": 200 }
  }
}
```

### 9. Performance budgets

| Item | Target | How |
|---|---|---|
| Watcher idle CPU | < 1% | Event-driven; no polling of the filesystem |
| Watcher extra memory | < 30 MB | Keep only per-volume USN position and the project-root index |
| Scanner CPU | ≤ 25% of one core (configurable) | Job Object `JOBOBJECT_CPU_RATE_CONTROL_INFORMATION` (hard cap) |
| Scanner memory | ≤ 200 MB | Job Object memory limit; stream files; don't hold whole roots in memory |
| Scanner disk I/O | Background priority | `SetPriorityClass(PROCESS_MODE_BACKGROUND_BEGIN)` lowers both CPU and I/O priority |
| Baseline duration | Minutes, not hours, on a typical developer laptop | File-table listing plus the pre-check |
| Re-scan of an unchanged root | ~0 file reads | Cache keyed on path, size and modified time |

Also: pause scanning while on battery or under heavy user load (policy), and
add a per-file regex timeout or use `NonBacktracking`.

### 10. Output schema

One record per agent:

```json
{
  "schemaVersion": 1,
  "id": "sha256(host|projectRoot|framework|file|agentName)",
  "state": "implemented",
  "confidence": "high",
  "framework": { "key": "crewai", "display": "CrewAI", "language": "python", "version": "0.80.0" },
  "agent": {
    "name": "researcher",
    "description": "Investigate customer complaints. An AI research analyst.",
    "models": [ { "id": "gpt-4o", "provider": "openai", "source": "static" } ],
    "tools": [ "search_tool", "send_email_tool" ],
    "capabilities": [ "web_access", "email_messaging" ],
    "riskFlags": [ "exfiltration_combo" ]
  },
  "project": {
    "root": "C:\\Users\\alice\\code\\support-bot",
    "name": "support-bot",
    "manifest": "pyproject.toml",
    "gitRemote": "https://github.com/alice/support-bot.git",
    "owner": "DOMAIN\\alice",
    "isTest": false
  },
  "evidence": [
    { "file": "src/crew.py", "line": 7, "snippet": "researcher = Agent(" }
  ],
  "runtime": {
    "running": false,
    "lastSeenRunning": null,
    "processes": [],
    "observedModels": []
  },
  "firstSeen": "2026-11-05T10:00:00Z",
  "lastSeen": "2026-11-05T10:00:00Z"
}
```

- `models[].source`: `static` (from code) or `observed` (from proxy traffic).
  The observed model is the one that matters for risk.
- Inventory records for **Installed** and **Referenced** use the same shape,
  with an empty `agent` block.
- Send changes only (new, changed, removed), keyed by `id`.

**Privacy:** never send source code off the machine. Snippets are capped at
200 characters. Descriptions are capped and can be turned off by policy.
Secrets are never captured: drop any argument value that matches a secret
pattern.

### 11. Implementation plan

| Step | Deliverable | Done when |
|---|---|---|
| **1. Detection library** | `ShadowAgents.Detection` (C# class library): rule model, Python rules for all Section 7.2 frameworks, profiler, capability classifier | Passes the test corpus (Section 13) with results equal to or better than ai-surface on the same files |
| **2. Scanner exe on given roots** | `Scanner.exe --job job.json`: resolver, pre-check, cache, JSON output, Job Object and background priority | Meets the Section 9 budgets on a large test tree |
| **3. Machine-wide discovery** | File-table indexer, manifest parsing, project-root merge, loose-file roots | Finds every agent in a seeded test machine with no paths configured |
| **4. Continuous mode** | USN reader, change batcher and scheduler in the endpoint agent | A newly created agent file is reported within debounce time plus scan time |
| **5. Running state** | ETW/WMI process events, command-line-to-root matching, Titanium correlation, observed models | A running seeded agent is marked `running` with its observed model |
| **6. More languages** | TS/JS rules, n8n reader, C# via Roslyn | Corpus tests per language |
| **7. Hardening** | Hostile-input tests, journal reset handling, non-NTFS fallback, policy config, telemetry on scan cost | Soak test: 7 days on developer machines within budgets |

### 12. Phase 2: MCP servers

Out of scope for Phase 1, but plan the data model for it.

- **Client configs on Windows** (per user profile):
  - Claude Desktop: `%APPDATA%\Claude\claude_desktop_config.json`
  - Claude Code: `%USERPROFILE%\.claude.json`, project `.mcp.json`
  - Cursor: `%USERPROFILE%\.cursor\mcp.json`, project `.cursor\mcp.json`
  - VS Code: `%APPDATA%\Code\User\mcp.json` and `settings.json`, project
    `.vscode\mcp.json`
  - Windsurf: `%USERPROFILE%\.codeium\windsurf\mcp_config.json`
  - Cline: `%APPDATA%\Code\User\globalStorage\saoudrizwan.claude-dev\settings\cline_mcp_settings.json`
  - Continue: `%USERPROFILE%\.continue\config.yaml` / `config.json`

  Check these paths against current client versions; shadow-mcp keeps an
  up-to-date list.
- **MCP servers in source:** Python `mcp.server.fastmcp` / `FastMCP(` /
  `@mcp.tool`; TS `@modelcontextprotocol/sdk` / `new McpServer(`; C#
  `ModelContextProtocol` / `.AddMcpServer()` / `[McpServerTool]`.
- **Running:** stdio servers are child processes of AI clients (link them
  through the parent process ID); remote servers show up in proxy traffic.
- **Never launch** a discovered MCP server to list its tools.

### 13. Testing strategy

- **Positive corpus:** ai-surface `tests/fixtures/agents/*` and
  `examples/demo-app`; official examples from each framework (LangChain,
  LangGraph, crewAI-examples, AutoGen samples, Semantic Kernel samples,
  LlamaIndex, Pydantic AI, OpenAI Agents SDK, Mastra). Pin them by commit.
- **Negative corpus:** projects that import a framework but define no agent
  (only an LLM call, only embeddings), local modules named `agents`, a
  framework name inside strings or comments.
- **Comparison test against ai-surface:** run `ai-surface scan <dir> -o json`
  and our scanner on the same corpus, and compare agent names, tools and
  frameworks. Any difference is either our bug or a documented improvement.
- **Hostile input:** 100k unclosed `Agent(` tokens, huge one-line files,
  deeply nested brackets, invalid UTF-8, files locked by other processes.
  The scanner must stay within budget and never crash.
- **Performance:** a synthetic tree of about 1M files, 50 GB and 200 projects.
  Measure baseline time, steady-state CPU and I/O.
- **End to end:** a seeded VM with agents in odd places (Downloads, Desktop
  notebooks, `C:\temp`, a second drive) and no configured paths.

### 14. Reference projects

All are open source. Port logic, not code, unless the license is honored
(MIT: keep the copyright notice; Apache-2.0: keep LICENSE and NOTICE).

| Project | License | Take from it |
|---|---|---|
| [apisec-inc/AI-Surface](https://github.com/apisec-inc/AI-Surface) | MIT | Framework signatures, constructor and tool extraction, bracket matcher, hostile-input guards, capability and risk rules, test fixtures. Main source of rules. |
| [cisco-ai-defense/aibom](https://github.com/cisco-ai-defense/aibom) | Apache-2.0 | Three-tier design (exact parsing, cross-reference, optional LLM classification); tree-sitter use; watch mode |
| [Defend-AI-Tech-Inc/agent-discover-scanner](https://github.com/Defend-AI-Tech-Inc/agent-discover-scanner) | MIT | Layered architecture; linking code to running processes; classification (confirmed, shadow, zombie, ghost) |
| [Trusera/ai-bom](https://github.com/Trusera/ai-bom) | Apache-2.0 | n8n workflow and node parsing; CrewAI decorator handling |
| [zuwardo-zero/shadow-mcp](https://github.com/zuwardo-zero/shadow-mcp) | MIT | MCP client config paths, including Windows (Phase 2) |
| [cisco-ai-defense/mcp-scanner](https://github.com/cisco-ai-defense/mcp-scanner) | Apache-2.0 | MCP tool risk rules (Phase 2) |

**Gaps in ai-surface 1.1.1 to fix while porting:**

- No agent constructors for LangGraph, LlamaIndex, Semantic Kernel or Haystack
  in Python; these are detected only at framework level.
- The `autogen` import root misses `autogen_agentchat`, `autogen_ext` and
  `autogen_core`.
- No LangChain v1 `create_agent`.
- The agent's model is never linked to the agent; descriptions and
  instructions are never extracted.
- Every detector walks the tree separately. Use one walk with all rules.

### 15. Open questions

1. **Other operating systems:** is Phase 1 Windows-only, or are macOS and
   Linux endpoints needed? That decides whether to put the USN and ETW code
   behind an interface now.
2. **WSL and containers:** agents inside WSL distros (`\\wsl$\...`) or Docker
   are invisible to NTFS. Should they be in Phase 1, or later?
3. **Description capture:** are system prompts and instructions acceptable to
   send to the backend under customer privacy terms, or should we send hashes
   only?
4. **Network shares and removable drives:** scan, skip, or decide by policy?
5. **Optional LLM step:** is a local or backend LLM pass allowed to classify
   uncertain candidates (as aibom does), or must it stay fully deterministic?
6. **Ownership:** take `project.owner` from the file's owner or from the user
   profile path?
