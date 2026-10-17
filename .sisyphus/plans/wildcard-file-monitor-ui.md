# Wildcard File Monitoring and Rules Dialog

## TL;DR
> **Summary**: Add "directory + wildcard" monitor rules on top of the existing WinForms architecture: the user configures a single-level directory and a `*`/`?` pattern in a dedicated rules dialog, and the system automatically discovers and takes over matching files, reusing the existing `TailForm` to open/activate tabs.
> **Deliverables**:
> - Session-level `MonitorRuleConfig` data model and persistence
> - Rule validation/preview logic
> - Rule runtime manager based on `FileSystemWatcher + Directory.GetFiles`
> - `MainForm` menu entry and a dedicated "Monitor Rules" dialog
> - README / CLAUDE usage notes and regression verification
> **Effort**: Large
> **Parallel**: YES - 2 waves
> **Critical Path**: 1 → 3 → 4 → 5 → 6/7 → 8 → 9

## Context
### Original Request
- New feature: monitor files matching a wildcard in a given directory, e.g. `d:\xxx\*err*.log`
- Both the design and the UI are needed

### Interview Summary
- The main app is confirmed to be **.NET 8 WinForms**, entry point `Program -> MainForm`
- Scope limited to: **current directory only**, **no recursion into subdirectories**
- Match syntax limited to: **only `*` and `?`**, no regex, no exclusion rules
- When a rule matches a new file: **automatically add it to monitoring**, without asking the user to confirm again
- UI direction decided: `MainForm` provides the entry and uses a **dedicated rule management dialog**
- Test strategy decided: **TDD**

### Metis Review (gaps addressed)
- Fill in and pin down the following default decisions so no judgment calls are needed during execution:
  - The wildcard rule matches only the "file name part"; the directory is stored separately; the UI shows a combined preview path
  - On Windows, path and file name matching is **case-insensitive**
  - When a rule is enabled, first do one **discovery of existing files**, then hook up the watcher events
  - After a file is deleted/renamed, **do not automatically close** the open `TailForm`; keep the existing Tail behavior for missing files
  - If a matching file is already open in the current window, **activate the existing tab instead of creating a duplicate tab**
- Guardrails:
  - **Do not break** the existing "latest matching file" semantics of `TailFileConfig.FileCheckPattern`
  - **Do not introduce** a DI container, recursive scanning, regex matching, or a confirmation prompt per match

## Work Objectives
### Core Objective
Without breaking existing single-file Tail and the old wildcard path semantics, add a "monitor rules" capability to the WinForms main program: the user can continuously monitor multiple files matching a pattern in a given directory, and they are automatically added to the session as existing Tail views.

### Deliverables
- `MonitorRuleConfig` session model
- `MonitorRuleValidation` validation/preview logic
- `MonitorRuleManager` runtime discovery and watcher coordination logic
- `MonitorRuleEditForm` (add/edit a single rule)
- `MonitorRulesForm` (rule management and match status overview)
- `MainForm` menu entry and startup/shutdown lifecycle integration
- Documentation updates: `README.md`, `CLAUDE.md`

### Definition of Done (verifiable conditions with commands)
- Before running `pwsh -File ".\script\build.ps1" --release`, make sure any running `SnakeTail.exe` has exited, so `.run\SnakeTail.exe` isn't locked causing `MSB3027/MSB3021`
- `dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~MonitorRule"`
- `dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~LogFileStream"`
- `pwsh -File ".\script\build.ps1" --release`
- Manual/agent QA passes the following key scenarios:
  - After creating a rule, matching files already in the directory are discovered immediately
  - When a new file lands in the directory, the corresponding tab is opened or activated automatically
  - The same file is not opened in multiple tabs due to duplicate events
  - Rules are restored after saving the session/restarting

### Must Have
- New rule fields: `Name`, `DirectoryPath`, `FilePattern`, `Enabled`
- When a rule is enabled, scan the current directory once immediately, then hook up the real-time watcher
- Reuse the existing open/activate logic of `MainForm.OpenFileOrActivateTab` / `BringToFrontAndOpenOrActivateTab`
- The UI provides: add, edit, delete, enable/disable, and view the current match status of files
- The edit dialog provides a combined preview: `<DirectoryPath>\<FilePattern>`
- Rules are saved to `TailConfig` and persisted along with the XML session and the default session

### Must NOT Have (guardrails, AI slop patterns, scope boundaries)
- No recursion into subdirectories
- No regex, exclusion patterns, or multi-directory rules
- Do not introduce a new dependency injection framework or refactor the global architecture
- Do not change the existing behavior of the old `TailConfigForm` / `TailFileConfig.FileCheckPattern`
- Do not show a confirmation prompt for each newly matched file
- Do not automatically close the user's existing Tail tabs when a file is deleted

## Verification Strategy
> ZERO HUMAN INTERVENTION - all verification is agent-executed.
- Test decision: **TDD** + xUnit (existing `tests/SnakeTail.Tests/SnakeTail.Tests.csproj`)
- QA policy: every task comes with happy / failure scenarios; UI scenarios are closed out through testable logic + final agent QA
- Evidence: `.sisyphus/evidence/task-{N}-{slug}.{ext}`

## Execution Strategy
### Parallel Execution Waves
> Target: 5-8 tasks per wave. <3 per wave (except final) = under-splitting.
> Extract shared dependencies as Wave-1 tasks for max parallelism.

Wave 1: Data contracts and runtime foundation
- T1 MonitorRuleConfig data model
- T2 Rule validation and combined preview
- T3 Directory scanning / watcher wrapper
- T4 Rule manager and duplicate-match deduplication
- T5 MainForm lifecycle and session integration

Wave 2: UI and wrap-up
- T6 MonitorRuleEditForm single-rule edit dialog
- T7 MonitorRulesForm main rule management dialog
- T8 MainForm menu integration + dialog state persistence
- T9 README / CLAUDE / final regression script and notes

### Dependency Matrix (full, all tasks)
| Task | Depends On | Needed By |
|---|---|---|
| T1 | - | T2, T3, T4, T5, T6, T7, T9 |
| T2 | T1 | T3, T4, T6, T7 |
| T3 | T1, T2 | T4, T5 |
| T4 | T1, T2, T3 | T5, T7 |
| T5 | T1, T3, T4 | T8, T9 |
| T6 | T1, T2 | T7 |
| T7 | T4, T6 | T8 |
| T8 | T5, T7 | T9 |
| T9 | T5, T8 | Final Verification |

### Agent Dispatch Summary (wave → task count → categories)
- Wave 1 → 5 tasks → `quick` ×2, `unspecified-high` ×3
- Wave 2 → 4 tasks → `visual-engineering` ×2, `unspecified-high` ×1, `writing` ×1
- Final Verification → 4 tasks → `oracle` / `unspecified-high` / `deep`

## TODOs
> Implementation + Test = ONE task. Never separate.
> EVERY task MUST have: Agent Profile + Parallelization + QA Scenarios.

- [x] 1. Extend the session model to persist monitor rules

  **What to do**:
  - Add `MonitorRuleConfig` in `src/TailConfig.cs`, with fields fixed as:
    - `string Name`
    - `string DirectoryPath`
    - `string FilePattern`
    - `bool Enabled`
  - Add `List<MonitorRuleConfig> MonitorRules` on `TailConfig`, initialized to an empty list in the constructor, so old sessions never deserialize to `null`
  - `Name` may be empty; when displayed empty, UI logic falls back uniformly to `Path.GetFileName(DirectoryPath) + " | " + FilePattern`
  - **Do not** turn each rule into a Tail appearance template; the first version reuses the current default Tail configuration throughout
  - Add xUnit regression tests covering: defaults, safe deserialization, and loading old XML without a `MonitorRules` field

  **Must NOT do**:
  - Do not change the meaning of existing `TailFileConfig` fields
  - Do not store rules in a separate JSON / SQLite table; the first version only stores them with the `TailConfig` session

  **Recommended Agent Profile**:
  - Category: `quick` - Reason: Single-file data contract extension + matching tests; simple judgment
  - Skills: [`superpowers:test-driven-development`] - Write the failing deserialization test first, then change the DTO
  - Omitted: [`my-ui`] - No UI work in this task

  **Parallelization**: Can Parallel: YES | Wave 1 | Blocks: [2, 3, 4, 5, 6, 7, 9] | Blocked By: []

  **References**:
  - Pattern: `src/TailConfig.cs:25-35` - Session-level configuration container `TailConfig`
  - Pattern: `src/TailConfig.cs:171-219` - Serialized field style of the existing `TailFileConfig`
  - Pattern: `tests/SnakeTail.Tests/TailFileConfigDefaultsTests.cs:8-31` - How defaults/old-XML regression tests are written
  - API/Type: `src/MainForm.cs:916-930` - The actual session save path via `XmlSerializer(typeof(TailConfig))`
  - API/Type: `src/Storage/SnakeTailStorage.cs:204-230` - The default session is serialized/deserialized through the same `TailConfig`

  **Acceptance Criteria**:
  - [ ] `TailConfig` has a non-null `MonitorRules` collection; deserializing old session XML neither throws nor yields `null`
  - [ ] New test file `tests/SnakeTail.Tests/MonitorRuleConfigTests.cs`
  - [ ] `dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~MonitorRuleConfigTests"` passes

  **QA Scenarios**:
  ```
  Scenario: The new session model serializes/deserializes safely
    Tool: Bash
    Steps: Run dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~MonitorRuleConfigTests"
    Expected: All tests PASS, covering defaults and XML round-trip
    Evidence: .sisyphus/evidence/task-1-monitor-rule-config.txt

  Scenario: Old XML missing the MonitorRules field is still compatible
    Tool: Bash
    Steps: Run the missing-field compatibility case in the same test set
    Expected: Deserialization succeeds, MonitorRules is non-null and Count == 0
    Evidence: .sisyphus/evidence/task-1-monitor-rule-config-error.txt
  ```

  **Commit**: YES | Message: `feat(config): add monitor rule session model` | Files: [`src/TailConfig.cs`, `tests/SnakeTail.Tests/MonitorRuleConfigTests.cs`]

- [x] 2. Pin down rule validation, normalization and combined preview semantics

  **What to do**:
  - Create `src/MonitorRuleValidation.cs`, providing pure-logic static/internal methods:
    - `NormalizeDirectoryPath(string)`: trim whitespace, convert to an absolute path, strip the trailing separator
    - `ValidateDirectory(string)`: the directory must not be empty; return a clear error text if the directory does not exist
    - `ValidateFilePattern(string)`: must not be empty; must not contain directory separators; only plain characters, `*` and `?` are allowed
    - `BuildPreviewPath(string directoryPath, string filePattern)`: outputs `<dir>\<pattern>`
    - `BuildDisplayName(MonitorRuleConfig)`: falls back to a default display name when the name is empty
  - Make the first-version semantics explicit: `FilePattern` corresponds only to the file name; a full path like `d:\a\b\*.log` may not be entered in the pattern box; the full path is only shown in the preview text
  - Add `tests/SnakeTail.Tests/MonitorRuleValidationTests.cs` covering: valid patterns, empty directory, nonexistent directory, pattern with separators, case/whitespace normalization

  **Must NOT do**:
  - No support for `**`, exclusion patterns, or regex
  - Do not silently correct a nonexistent directory to the nearest existing parent directory

  **Recommended Agent Profile**:
  - Category: `quick` - Reason: Standalone pure-logic helper, well suited to TDD
  - Skills: [`superpowers:test-driven-development`] - Lock down the error messages and preview semantics first
  - Omitted: [`my-ui`] - This task creates no forms

  **Parallelization**: Can Parallel: YES | Wave 1 | Blocks: [3, 4, 6, 7] | Blocked By: [1]

  **References**:
  - Pattern: `src/TailConfigForm.cs:61-188` - The existing config dialog's "convert values on load/before save" style
  - Pattern: `src/MainForm.cs:973-985` - Error messages must be precisely traceable; follow the explicit text concatenation style
  - Test: `tests/SnakeTail.Tests/LogFileStreamWatcherMatchTests.cs:8-45` - Case-insensitive file matching convention on Windows
  - Test: `tests/SnakeTail.Tests/TailFileConfigDefaultsTests.cs:8-31` - xUnit pure-logic test style

  **Acceptance Criteria**:
  - [ ] `MonitorRuleValidation` returns stable, assertable error text for invalid directories and invalid patterns
  - [ ] The preview path is always output as `DirectoryPath + "\\" + FilePattern`
  - [ ] `dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~MonitorRuleValidationTests"` passes

  **QA Scenarios**:
  ```
  Scenario: A valid directory + pattern produces a combined preview
    Tool: Bash
    Steps: Run dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~MonitorRuleValidationTests"
    Expected: All preview, display-name and normalize tests PASS
    Evidence: .sisyphus/evidence/task-2-monitor-rule-validation.txt

  Scenario: An invalid pattern is rejected with a precise reason
    Tool: Bash
    Steps: In the same test set, run the scenarios for a pattern with path separators, an empty string, and an invalid directory
    Expected: Tests assert stable error text; silent failures are not accepted
    Evidence: .sisyphus/evidence/task-2-monitor-rule-validation-error.txt
  ```

  **Commit**: YES | Message: `feat(monitor): add rule validation helpers` | Files: [`src/MonitorRuleValidation.cs`, `tests/SnakeTail.Tests/MonitorRuleValidationTests.cs`]

- [x] 3. Add a directory scanning and watcher wrapper layer

  **What to do**:
  - Create `src/MonitorDirectoryWatcher.cs`, encapsulating the "one rule -> one directory watcher" responsibility
  - Fixed implementation strategy:
    - When a rule is enabled, first run `Directory.GetFiles(directory, pattern, TopDirectoryOnly)` for baseline discovery
    - Then create the `FileSystemWatcher`
    - `Path = DirectoryPath`
    - `Filter = FilePattern`
    - `IncludeSubdirectories = false`
    - Listen for `Created`, `Renamed`, `Changed`
  - Emit unified events: `MatchFound(string absolutePath)`, `RuleError(string message)`
  - The `Changed` event is only a fallback for "file created first, written later" and must not cause duplicate floods; the same path must be debounced within a short time window (suggested: dedupe by path within 300ms)
  - When the directory does not exist, don't throw a UI-level exception; raise `RuleError` instead and mark the rule as not runnable
  - Add `tests/SnakeTail.Tests/MonitorDirectoryWatcherTests.cs`: cover initial discovery, match after creation, and missing-directory error

  **Must NOT do**:
  - Do not reuse the "find only the latest file" logic of `LogFileStream.FindFileUsingPattern`
  - Do not let the watcher directly new up a `TailForm`

  **Recommended Agent Profile**:
  - Category: `unspecified-high` - Reason: File system events have ordering and deduplication details
  - Skills: [`superpowers:test-driven-development`] - Lock down the initial discovery and debounce tests first
  - Omitted: [`my-ui`] - Pure runtime logic

  **Parallelization**: Can Parallel: YES | Wave 1 | Blocks: [4, 5, 7] | Blocked By: [1, 2]

  **References**:
  - Pattern: `src/LogFileStream.cs:46-58` - Existing `FileSystemWatcher` construction and initialization entry point
  - Pattern: `src/LogFileStream.cs:70-149` - Approach to reload/check handling after a file change
  - Pattern: `src/LogFileStream.cs:220-237` - The old wildcard mode only finds the latest file; this task must bypass that old semantics
  - Pattern: `src/LogFileStream.cs:557-580` - The existing event-driven + debounce approach can serve as a reference
  - Test: `tests/SnakeTail.Tests/LogFileStreamAppendDetectionTests.cs:10-67` - How file tests are written using temp directories/temp files
  - Test: `tests/SnakeTail.Tests/LogFileStreamWatcherMatchTests.cs:34-44` - Reference for wildcard watcher semantics

  **Acceptance Criteria**:
  - [ ] When a rule is enabled, matching files already in the directory are returned first
  - [ ] After a new file is created or renamed into the directory, `MatchFound` is reported only once after debouncing
  - [ ] A missing directory does not crash; the error is exposed through `RuleError`
  - [ ] `dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~MonitorDirectoryWatcherTests"` passes

  **QA Scenarios**:
  ```
  Scenario: Existing matching files are discovered immediately when a rule is enabled
    Tool: Bash
    Steps: Run dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~MonitorDirectoryWatcherTests"
    Expected: Baseline discovery and new-file match tests PASS
    Evidence: .sisyphus/evidence/task-3-monitor-directory-watcher.txt

  Scenario: A missing directory returns an error instead of throwing an unhandled exception
    Tool: Bash
    Steps: Run the nonexistent-directory scenario in the same test set
    Expected: Assert that RuleError is received; the test process does not crash
    Evidence: .sisyphus/evidence/task-3-monitor-directory-watcher-error.txt
  ```

  **Commit**: YES | Message: `feat(monitor): add directory watcher` | Files: [`src/MonitorDirectoryWatcher.cs`, `tests/SnakeTail.Tests/MonitorDirectoryWatcherTests.cs`]

- [ ] 4. Implement the rule manager and duplicate-match dedup registry

  **What to do**:
  - Create `src/MonitorRuleManager.cs` to manage all `MonitorDirectoryWatcher` instances centrally
  - Fixed responsibilities:
    - `ApplyRules(IReadOnlyList<MonitorRuleConfig>)`
    - `StartAll()` / `StopAll()` / `Dispose()`
    - Maintain `RuleState` (Running / Disabled / Error) and a `MatchedFiles` snapshot
    - Raise `RuleMatchDiscovered(ruleId/name, absolutePath)` and `RulesChanged` events to the outside
  - Internally maintain a **case-insensitive** set of absolute paths to suppress duplicate events for the same file
  - If a file is already managed by another rule, raise the "needs open/activate" event only once; the UI just shows that it is matched by multiple rules and does not open duplicate tabs
  - When a rule is disabled, stop its watcher but **do not close** the open `TailForm`
  - Add `tests/SnakeTail.Tests/MonitorRuleManagerTests.cs` covering: duplicate-match dedup, files shared across rules, no further events after a rule is disabled

  **Must NOT do**:
  - Do not stuff `MainForm` or `TailForm` directly into the manager
  - Do not show a `MessageBox` inside the manager

  **Recommended Agent Profile**:
  - Category: `unspecified-high` - Reason: Needs to coordinate multiple rules, multiple watchers and multi-event deduplication
  - Skills: [`superpowers:test-driven-development`] - Lock down the multi-rule duplicate-match tests first
  - Omitted: [`my-ui`] - Still non-UI core logic

  **Parallelization**: Can Parallel: YES | Wave 1 | Blocks: [5, 7] | Blocked By: [1, 2, 3]

  **References**:
  - Pattern: `src/MainForm.cs:546-617` - Existing "open or activate existing tab" convention; the manager only raises events and does not manipulate the UI directly
  - Pattern: `src/LogFileStream.cs:38-45` - Example of the existing event model (FileReloadedEvent / FileChangedEvent)
  - Test: `tests/SnakeTail.Tests/LogFileStreamWatcherMatchTests.cs:8-45` - Case-insensitive matching regression convention
  - Test: `tests/SnakeTail.Tests/LogFileStreamAppendDetectionTests.cs:35-66` - Temp directory/wait helper style

  **Acceptance Criteria**:
  - [ ] When the same path is hit by multiple events, only one pending-open event is reported
  - [ ] When the same path is matched by multiple rules at once, the UI is not asked to open multiple tabs
  - [ ] Disabling a rule only stops further discovery and does not close open tail windows
  - [ ] `dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~MonitorRuleManagerTests"` passes

  **QA Scenarios**:
  ```
  Scenario: When multiple watchers hit the same file repeatedly, only one open request is triggered
    Tool: Bash
    Steps: Run dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~MonitorRuleManagerTests"
    Expected: All dedup and disabled-rule tests PASS
    Evidence: .sisyphus/evidence/task-4-monitor-rule-manager.txt

  Scenario: No new matches are sent after a rule is disabled
    Tool: Bash
    Steps: Run the create-file-after-disable scenario in the same test set
    Expected: No new match-discovered events are received; the status of known files is kept
    Evidence: .sisyphus/evidence/task-4-monitor-rule-manager-error.txt
  ```

  **Commit**: YES | Message: `feat(monitor): add monitor rule manager` | Files: [`src/MonitorRuleManager.cs`, `tests/SnakeTail.Tests/MonitorRuleManagerTests.cs`]

- [ ] 5. Wire the rule manager into the MainForm lifecycle and session restore

  **What to do**:
  - Modify `src/MainForm.cs`:
    - Add a field holding the `MonitorRuleManager`
    - After `LoadSessionWithConfig` finishes loading `TailFiles`, apply and start `tailConfig.MonitorRules`
    - In `BuildCurrentTailConfig` / `SaveSession` / `SaveSessionToDb`, write the current rules back to `TailConfig.MonitorRules`
    - In `MainForm_FormClosing`, first `StopAll/Dispose`, then run the existing session save
  - When a rule matches a new file, always call `BringToFrontAndOpenOrActivateTab(new[]{path})`; **do not** copy the open logic yourself
  - If a file matched by a rule already exists in a current `TailForm` tab, the dedup/activate semantics of `OpenFileOrActivateTab` must be kept
  - When the default session or an XML session is restored, rules should be restored automatically with the session
  - Add `tests/SnakeTail.Tests/MonitorRuleSessionPersistenceTests.cs`, covering at least that rules survive a `TailConfig` round-trip; if MainForm is hard to test directly, extract a testable helper and unit test that instead of writing fragile form tests

  **Must NOT do**:
  - Do not hook rule logic directly into `Program.cs`
  - Do not start rules before `LoadSessionWithConfig`, to avoid opening windows out of order before the tail windows are restored

  **Recommended Agent Profile**:
  - Category: `unspecified-high` - Reason: Involves startup order, session restore and reuse of the existing window-opening logic
  - Skills: [`superpowers:test-driven-development`] - Add persistence regression tests first, then wire up the MainForm lifecycle
  - Omitted: [`my-ui`] - Mainly control-flow wiring

  **Parallelization**: Can Parallel: YES | Wave 1 | Blocks: [8, 9] | Blocked By: [1, 3, 4]

  **References**:
  - Pattern: `src/Program.cs:54-64` - The entry layer only news up `MainForm`; feature logic should not spread into Program
  - Pattern: `src/MainForm.cs:546-617` - `OpenFileOrActivateTab` / `BringToFrontAndOpenOrActivateTab` ready-made reuse points
  - Pattern: `src/MainForm.cs:839-944` - XML session save entry point
  - Pattern: `src/MainForm.cs:1043-1090` - Default session save construction logic
  - Pattern: `src/MainForm.cs:1092-1175` - Session restore order
  - Pattern: `src/MainForm.cs:1491-1558` - Save/release lifecycle on close
  - API/Type: `src/Storage/SnakeTailStorage.cs:183-230` - Default session persistence path

  **Acceptance Criteria**:
  - [ ] `MonitorRules` is persisted when saving both the XML session and the default session
  - [ ] After a session is restored, enabled rules start automatically and take over matching files
  - [ ] When the same path is matched, `OpenFileOrActivateTab` is still reused and no duplicate tabs are created
  - [ ] `dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~MonitorRuleSessionPersistenceTests"` passes

  **QA Scenarios**:
  ```
  Scenario: Rules are fully preserved after a session round-trip
    Tool: Bash
    Steps: Run dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~MonitorRuleSessionPersistenceTests"
    Expected: XML/default-session persistence regression tests PASS
    Evidence: .sisyphus/evidence/task-5-monitor-session.txt

  Scenario: Matching an already-open file activates the old tab instead of opening a new one
    Tool: Bash
    Steps: Run the regression tests that include the open-dispatch helper; if the helper was extracted, filter by the helper tests
    Expected: Assert that only one open request happens or only the activate behavior is returned
    Evidence: .sisyphus/evidence/task-5-monitor-session-error.txt
  ```

  **Commit**: YES | Message: `feat(main): restore monitor rules with sessions` | Files: [`src/MainForm.cs`, `tests/SnakeTail.Tests/MonitorRuleSessionPersistenceTests.cs`]

- [ ] 6. Implement the single-rule edit dialog (MonitorRuleEditForm)

  **What to do**:
  - Create `src/MonitorRuleEditForm.cs`, `src/MonitorRuleEditForm.Designer.cs`, `src/MonitorRuleEditForm.resx`
  - The dialog layout is fixed as:
    - `Name` text box (may be empty)
    - `Directory` text box + `Browse...` button
    - `Pattern` text box (example placeholder: `*err*.log`)
    - `Preview` read-only text box showing `<Directory>\<Pattern>` live
    - `Enabled` check box
    - `Preview Matches` button + read-only list (shows at most the first 20 currently matching files, with "+N more" when exceeded)
    - `OK` / `Cancel`
  - `Preview Matches` only calls `Directory.GetFiles(directory, pattern, TopDirectoryOnly)` for a synchronous preview; it does not create a watcher
  - `MonitorRuleValidation` must run before `OK`
  - `ShowDialog(owner)` must be passed `MainForm` or the parent dialog as owner
  - Follow the project's current WinForms hand-written/Designer style; do not introduce MVVM

  **Must NOT do**:
  - Do not stuff rule editing directly into `TailConfigForm`
  - Do not support configuring per-rule encoding/colors/window mode in this dialog

  **Recommended Agent Profile**:
  - Category: `visual-engineering` - Reason: Many WinForms interaction and layout details
  - Skills: [`my-ui`] - Must follow the rules for dialogs, buttons, input boxes and feedback
  - Omitted: [`superpowers:test-driven-development`] - The testable logic is already in T2; the UI shell relies mainly on compilation and final QA

  **Parallelization**: Can Parallel: YES | Wave 2 | Blocks: [7] | Blocked By: [1, 2]

  **References**:
  - Pattern: `src/TailConfigForm.cs:25-35` - Existing config dialog constructor pattern
  - Pattern: `src/TailConfigForm.cs:61-188` - WinForms style for loading/saving control values
  - Pattern: `src/MainForm.Designer.cs:128-166` - Menu/dialog naming style
  - API/Type: `src/MainForm.cs:1233-1237` - Example of binding the dialog owner
  - External: `README.md` - The product already has a "tail log directory" capability; the new UI must clearly be "multi-file rule management" and not be confused with the old entry

  **Acceptance Criteria**:
  - [ ] The edit dialog can add/edit a `MonitorRuleConfig`
  - [ ] The Preview text updates instantly as the directory or pattern is typed
  - [ ] `Preview Matches` shows the first 20 matching files in the current directory or a precise error reason
  - [ ] With an invalid directory or pattern, `OK` does not close the dialog and the error text is precise
  - [ ] `pwsh -File ".\script\build.ps1" --release` compiles successfully

  **QA Scenarios**:
  ```
  Scenario: The edit dialog shows the correct preview for valid input
    Tool: Bash
    Steps: First run dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~MonitorRuleValidationTests", then run pwsh -File ".\script\build.ps1" --release
    Expected: Validation tests PASS and the release build succeeds, showing the UI wiring did not break compilation
    Evidence: .sisyphus/evidence/task-6-monitor-rule-edit.txt

  Scenario: Invalid directories/patterns are not saved
    Tool: Bash
    Steps: Rerun the failure cases in the validation tests and run the release build
    Expected: Failure cases PASS (i.e. correctly rejected), build succeeds
    Evidence: .sisyphus/evidence/task-6-monitor-rule-edit-error.txt
  ```

  **Commit**: YES | Message: `feat(ui): add monitor rule edit dialog` | Files: [`src/MonitorRuleEditForm.cs`, `src/MonitorRuleEditForm.Designer.cs`, `src/MonitorRuleEditForm.resx`]

- [ ] 7. Implement the main rule management dialog (MonitorRulesForm)

  **What to do**:
  - Create `src/MonitorRulesForm.cs`, `src/MonitorRulesForm.Designer.cs`, `src/MonitorRulesForm.resx`
  - The dialog layout is fixed as:
    - Upper `ListView`: columns `Enabled`, `Name`, `Directory`, `Pattern`, `Status`, `Matched`
    - Lower `ListView`: shows the known matching files of the selected rule, columns `FileName`, `FullPath`, `OpenState`, `LastSeen`
    - Buttons on the right: `Add...`, `Edit...`, `Delete`, `Enable/Disable`, `Refresh Now`, `Close`
  - Fixed behavior:
    - When the selected rule changes, the lower area refreshes that rule's matching file status
    - Double-clicking a file in the lower area calls `MainForm.BringToFrontAndOpenOrActivateTab(new[]{path})`
    - `Refresh Now` only reruns the baseline discover for the current rule; it does not rebuild the whole session
    - Deleting a rule only deletes the rule; it does not close open tabs
  - Status text is fixed as: `Running` / `Disabled` / `Error: <reason>`
  - If a rule matched an already-open file, the lower area's `OpenState` shows `Open`; otherwise `Pending/Open on match` is not needed, just show `Managed`

  **Must NOT do**:
  - Do not make the matched-files list a tree structure
  - Do not close file tabs as a side effect of deleting a rule

  **Recommended Agent Profile**:
  - Category: `visual-engineering` - Reason: Needs somewhat more complex WinForms grids and interaction feedback
  - Skills: [`my-ui`] - Lists, buttons, and dialog owner/ESC behavior must be consistent
  - Omitted: [`superpowers:test-driven-development`] - Relies mainly on the testable logic of T2/T4/T5 and final compilation/QA

  **Parallelization**: Can Parallel: YES | Wave 2 | Blocks: [8] | Blocked By: [4, 6]

  **References**:
  - Pattern: `src/MainForm.cs:138-156` - Status bar update semantics can be borrowed for refreshing the dialog's status text
  - Pattern: `src/MainForm.cs:546-617` - Double-clicking a matched file must reuse the existing open/activate logic
  - Pattern: `src/MainForm.cs:655-673` - Existing style of driving menu display from right-click/current selection
  - Pattern: `src/TailConfigForm.cs:250-257` - How list items are driven by the config object in Tag
  - API/Type: `src/Storage/SnakeTailStorage.cs:239-254` - If dialog position needs persisting, the AppSettings read/write capability can be extended

  **Acceptance Criteria**:
  - [ ] The management dialog fully shows the rule list and the current matching file list
  - [ ] Enabling/disabling, deleting, and Refresh Now are all reflected instantly in the status column
  - [ ] Double-clicking a matched file only opens or activates one tab
  - [ ] `pwsh -File ".\script\build.ps1" --release` compiles successfully

  **QA Scenarios**:
  ```
  Scenario: Rule list and matching file list status refresh reliably
    Tool: Bash
    Steps: Run pwsh -File ".\script\build.ps1" --release; then run dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~MonitorRuleManagerTests"
    Expected: Build succeeds and manager status tests PASS, showing the status source the UI depends on is stable
    Evidence: .sisyphus/evidence/task-7-monitor-rules-form.txt

  Scenario: Deleting/disabling a rule does not accidentally close open tabs
    Tool: Bash
    Steps: Run the manager/session regression tests (MonitorRuleManagerTests + MonitorRuleSessionPersistenceTests)
    Expected: Tests PASS; no "deleting a rule closes the file" error semantics appear
    Evidence: .sisyphus/evidence/task-7-monitor-rules-form-error.txt
  ```

  **Commit**: YES | Message: `feat(ui): add monitor rules management dialog` | Files: [`src/MonitorRulesForm.cs`, `src/MonitorRulesForm.Designer.cs`, `src/MonitorRulesForm.resx`]

- [ ] 8. Wire up the MainForm menu, dialog state persistence and interaction wrap-up

  **What to do**:
  - Modify `src/MainForm.Designer.cs` and `src/MainForm.cs`
  - Add an item to the `File` menu: `Monitor &Rules...`, positioned after `Open File...` and before `Open EventLog...`
  - Clicking it opens `MonitorRulesForm` with owner `this`
  - The dialog position/size persistence approach is fixed as: extend the `AppSettings` access of `SnakeTailStorage` and save `MonitorRulesForm.Bounds` and `MonitorRuleEditForm.Bounds` as keys
  - If there are no rules, the lower area of the management dialog shows empty-state text instead of a blank/broken view
  - When a rule hits a runtime error, report it through the management dialog's status column and the necessary logs, without a flood of blocking message boxes
  - Keep the tray menu consistent with the main menu: since the existing tray menu "borrows" File menu items, the new menu item needs no separate implementation; putting it in the File menu is enough for it to be reused

  **Must NOT do**:
  - Do not add a second tray-specific implementation
  - Do not turn all runtime errors into pop-ups

  **Recommended Agent Profile**:
  - Category: `unspecified-high` - Reason: Involves menu structure, tray reuse, window state persistence and consolidating error feedback
  - Skills: [`my-ui`] - Must follow the menu, dialog owner, and consistency rules
  - Omitted: [`superpowers:test-driven-development`] - The focus is wiring and state persistence

  **Parallelization**: Can Parallel: YES | Wave 2 | Blocks: [9] | Blocked By: [5, 7]

  **References**:
  - Pattern: `src/MainForm.Designer.cs:128-166` - Where the `File` menu is defined
  - Pattern: `src/MainForm.cs:1233-1237` - `ShowDialog(this)` owner convention
  - Pattern: `src/MainForm.cs:1258-1277` - Implementation of the tray menu reusing `File` menu items; new menu items must be compatible with this mechanism
  - Pattern: `src/Storage/SnakeTailStorage.cs:239-254` - Can be extended into a general settings read/write entry point
  - Pattern: `src/MainForm.cs:1491-1558` - Style of releasing resources and swallowing errors on window close

  **Acceptance Criteria**:
  - [ ] Both the `File` menu and the tray menu can access `Monitor Rules...`
  - [ ] `MonitorRulesForm` and `MonitorRuleEditForm` remember their last position and size
  - [ ] Rule runtime errors do not produce repeated blocking pop-ups
  - [ ] `pwsh -File ".\script\build.ps1" --release` compiles successfully

  **QA Scenarios**:
  ```
  Scenario: Both the main menu and the tray menu contain the same entry
    Tool: Bash
    Steps: Run pwsh -File ".\script\build.ps1" --release
    Expected: Compiles successfully; no compile/resource errors in the MainForm menu and tray reuse code
    Evidence: .sisyphus/evidence/task-8-mainform-menu.txt

  Scenario: The dialog opens normally even when the state persistence keys are missing
    Tool: Bash
    Steps: Run the new storage-related regression tests (if the executor added them), then run the release build
    Expected: Regression tests PASS; the first time the dialog opens it uses the default position and does not throw
    Evidence: .sisyphus/evidence/task-8-mainform-menu-error.txt
  ```

  **Commit**: YES | Message: `feat(main): wire monitor rules dialogs` | Files: [`src/MainForm.cs`, `src/MainForm.Designer.cs`, `src/Storage/SnakeTailStorage.cs`]

- [ ] 9. Update the docs and complete the final regression notes

  **What to do**:
  - Update `README.md`:
    - Add a feature description for "monitoring multiple files by directory + wildcard"
    - State the first-version limits: current directory only, only `*`/`?`, no recursion, no exclusion rules
    - Document the UI entry: `File -> Monitor Rules...`
    - Document the difference from the old `FileCheckPattern`: the old capability is "a single Tail follows the latest file by pattern", the new one is "session-level rules manage multiple matching files"
  - Update `CLAUDE.md`: add the feature's limits and the requirement to keep README in sync (only change the project-level CLAUDE, not the user-level global instructions)
  - Add verification commands to the README: `dotnet test ...` and `pwsh -File .\script\build.ps1 --release`

  **Must NOT do**:
  - Do not omit the explanation of the differences between the old and new capabilities
  - Do not describe "may support recursion/regex in the future" as an implemented capability

  **Recommended Agent Profile**:
  - Category: `writing` - Reason: Pure documentation cleanup and scope notes
  - Skills: [] - Project documentation is enough
  - Omitted: [`my-ui`] - No UI work

  **Parallelization**: Can Parallel: YES | Wave 2 | Blocks: [F1, F2, F3, F4] | Blocked By: [5, 8]

  **References**:
  - Pattern: `README.md` - Where the existing feature list and build notes live
  - Pattern: `script/build.ps1:76-102` - How the release build command is written
  - Pattern: `0run.ps1:90-110` - The project's test run entry point
  - API/Type: `src/LogFileStream.cs:220-237` - Evidence of the old wildcard mode's "find latest file" implementation

  **Acceptance Criteria**:
  - [ ] The README clearly distinguishes the old wildcard Tail from the new monitor rules feature
  - [ ] The README documents the limits, entry point and verification commands
  - [ ] CLAUDE.md is updated with the new feature's constraints

  **QA Scenarios**:
  ```
  Scenario: Docs match the actual commands
    Tool: Bash
    Steps: Following the new commands in the README, run dotnet test "tests/SnakeTail.Tests/SnakeTail.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~MonitorRule", then run pwsh -File ".\script\build.ps1" --release
    Expected: The commands recorded in the README run successfully as-is
    Evidence: .sisyphus/evidence/task-9-docs.txt

  Scenario: The README does not wrongly claim capabilities beyond the first-version scope
    Tool: Bash
    Steps: Review the README/CLAUDE changes and run the build command
    Expected: Docs only cover single-level directory + * / ? semantics; build has no errors
    Evidence: .sisyphus/evidence/task-9-docs-error.txt
  ```

  **Commit**: YES | Message: `docs: describe monitor rules feature` | Files: [`README.md`, `CLAUDE.md`]

## Final Verification Wave (MANDATORY — after ALL implementation tasks)
> 4 review agents run in PARALLEL. ALL must APPROVE. Present consolidated results to user and get explicit "okay" before completing.
> **Do NOT auto-proceed after verification. Wait for user's explicit approval before marking work complete.**
> **Never mark F1-F4 as checked before getting user's okay.** Rejection or user feedback -> fix -> re-run -> present again -> wait for okay.
- [ ] F1. Plan Compliance Audit — oracle
- [ ] F2. Code Quality Review — unspecified-high
- [ ] F3. Real Manual QA — unspecified-high (+ playwright if UI)
- [ ] F4. Scope Fidelity Check — deep

## Commit Strategy
- Commit per task; avoid mixing DTO, runtime, UI and docs into one big commit
- Recommended commit order:
  1. `feat(config): add monitor rule session model`
  2. `feat(monitor): add rule validation helpers`
  3. `feat(monitor): add directory watcher`
  4. `feat(monitor): add monitor rule manager`
  5. `feat(main): restore monitor rules with sessions`
  6. `feat(ui): add monitor rule edit dialog`
  7. `feat(ui): add monitor rules management dialog`
  8. `feat(main): wire monitor rules dialogs`
  9. `docs: describe monitor rules feature`

## Success Criteria
- The user can open the rule management dialog from `MainForm` via `File -> Monitor Rules...`
- The user can add a rule: directory + `*err*.log`; once enabled it immediately takes over all matching files in the current directory
- When a new file lands in the directory, the corresponding Tail tab is opened or activated automatically
- The same file never gets duplicate tabs because of repeated events or multiple rules
- After saving the XML session / default session, rules can be restored on next startup or when the session is reloaded
- Existing single-file Tail, the old `FileCheckPattern` behavior, build scripts and the test project are not broken
