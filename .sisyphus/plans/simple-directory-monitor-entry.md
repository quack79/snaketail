# Minimal Directory Wildcard Monitor Entry

## TL;DR
> **Summary**: Add a minimal "Watch Directory (Wildcard)" entry to the File menu: pick a directory, enter a wildcard, and monitoring starts immediately; new or changed files in the directory are automatically appended to the UI.
> **Deliverables**: SimpleDirectoryMonitorDialog.cs, new menu entry, monitoring wiring, README notes
> **Effort**: Short
> **Parallel**: NO
> **Critical Path**: Create dialog → add menu → wire up monitoring and auto-open Tail → update docs → build

## Context
### Original Request
"This feature is too complicated. I just need to monitor files matching a fuzzy pattern in a given directory; if an existing file changes or a new file is created, it should be picked up immediately and shown in the tool's UI."

### Current State
- MonitorDirectoryWatcher already exists: non-recursive monitoring of a single directory, supports * and ? wildcards, detects new files
- TailForm already exists: opens a single file and tails it in real time
- The already-added Open Wildcard Monitor... entry opens a complex configuration dialog
- Desired: two minimal steps: pick a directory + enter a wildcard → monitoring starts immediately, new/changed files show up automatically

## Work Objectives
### Core Objective
Add a menu entry that lets the user pick a directory and enter a wildcard directly; on OK, wildcard monitoring of that directory starts immediately, and new/changed matching files in it are automatically appended to an MDI child window.

### Deliverables
1. Minimal dialog `SimpleDirectoryMonitorDialog.cs`: only a directory picker + wildcard (default *.log)
2. A second new entry in the File menu: "Watch Directory (Wildcard)"
3. Monitoring logic: after selection, create a MonitorDirectoryWatcher; matching files are automatically opened with TailForm and shown in the UI
4. Update README.md: explain the difference between the two entries
5. Build verification passes

### Definition of Done
- Release build passes (./script/build.ps1 --release)
- Menu visible: File → Watch Directory (Wildcard)
- Clicking it opens a dialog where you can pick a directory and enter a wildcard
- After OK, existing/new matching files in the directory are automatically opened and tailed in the UI with TailForm

### Must Have
- Minimal UI: no complex configuration, just directory + wildcard
- Automatically monitor and open new files
- Reuse MonitorDirectoryWatcher and TailForm; don't reinvent the wheel

### Must NOT Have
- Do not open the complex TailConfigForm
- Do not ask the user to configure encoding/interval/colors etc.
- Do not modify existing logic; add only minimal new code

## Verification Strategy
- Build passes + tests pass
- Manual QA through the menu: open dialog → pick a local log directory → OK → check that new/changed files appear
- Evidence: screenshot or screen recording (optional)

## Execution Strategy
### Steps
1. Create SimpleDirectoryMonitorDialog.cs: only a directory picker + wildcard text box
2. Add a second entry under the File menu in MainForm.Designer.cs (below Open File)
3. Implement the menu click logic in MainForm.cs: open dialog → create MonitorDirectoryWatcher → call OpenFileSelection on matching files or open a TailForm for each
4. Update README.md to explain the two entries: "minimal directory watch" vs "full wildcard configuration"
5. Run the build ./script/build.ps1 --release and make sure there are 0 errors

## TODOs
- [ ] 1. Create SimpleDirectoryMonitorDialog.cs (minimal directory + wildcard picker)

  **What to do**: Create SimpleDirectoryMonitorDialog.cs under src/, containing only:
  - Label + TextBox + button (browse for directory)
  - Label + TextBox (wildcard, default *.log)
  - OK/Cancel buttons
  - Validation: directory exists, wildcard is not empty

  **Must NOT do**: No other configuration options at all (encoding/colors/interval etc.)

  **References**: The structure of OpenEventLogDialog.cs can serve as a reference

  **Acceptance Criteria**: Compiles, dialog opens, a directory can be picked, wildcard is editable

  **QA Scenarios**:
  ```
  Scenario: Open the dialog
    Tool: Manual (or later via Playwright)
    Steps: Click the new entry under the File menu
    Expected: SimpleDirectoryMonitorDialog opens with the default wildcard *.log
    Evidence: Screenshot or screen recording clip
  ```

  **Commit**: YES | Message: "feat: add SimpleDirectoryMonitorDialog for minimal watch directory UI" | Files: [src/SimpleDirectoryMonitorDialog.cs]

- [ ] 2. Add a "Watch Directory (Wildcard)" entry to the File menu

  **What to do**: Add a menu item under the File menu in MainForm.Designer.cs, after openToolStripMenuItem and before openEventLogToolStripMenuItem. Text: "Watch &Directory..." or "Watch Directory (Wildcard)"; shortcut optionally Ctrl+Shift+D or none.

  **Must NOT do**: Do not change the order/size/text of existing menu items (except necessary Size alignment)

  **References**: How openToolStripMenuItem is declared in MainForm.Designer.cs

  **Acceptance Criteria**: Menu visible, positioned below Open File

  **QA Scenarios**:
  ```
  Scenario: Menu visible
    Tool: Manual
    Steps: Start the program, open the File menu
    Expected: "Watch Directory (Wildcard)" (or "Watch Directory...") is visible
    Evidence: Screenshot
  ```

  **Commit**: YES | Message: "feat: add menu entry for simple directory monitor" | Files: [src/MainForm.Designer.cs]

- [ ] 3. Implement the menu click logic: after picking a directory, start monitoring and automatically open matching files

  **What to do**: Add a click event handler in MainForm.cs:
  - Open SimpleDirectoryMonitorDialog
  - After the user clicks OK, create a MonitorDirectoryWatcher
  - For existing files that match initially, call OpenFileSelection or open a TailForm for each
  - For matching files added/changed later, likewise open a TailForm to show them
  - Reuse EnsureDefaultTailConfig() and CloneTailFileConfig() where possible to avoid duplicate code

  **Must NOT do**: Do not open TailConfigForm; do not make the user configure anything else

  **References**: The FileMatched event in MonitorDirectoryWatcher.cs; the open logic of OpenFileSelection in MainForm.cs

  **Acceptance Criteria**: After picking a directory, existing/new files in it that match the wildcard are automatically opened and tailed in the UI

  **QA Scenarios**:
  ```
  Scenario: Open existing matching files
    Tool: Manual
    Steps: Put a.log / b.log in a test directory, pick that directory, click OK
    Expected: Both files are automatically opened in the UI and tailing starts
    Evidence: Screenshot
  ```

  ```
  Scenario: New matching file is opened automatically
    Tool: Manual
    Steps: After starting monitoring, add c.log to the directory
    Expected: c.log is automatically opened in the UI and tailing starts
    Evidence: Screenshot
  ```

  **Commit**: YES | Message: "feat: implement simple directory monitor logic that auto-tails matched files" | Files: [src/MainForm.cs]

- [ ] 4. Update README.md to explain the difference between the two entries

  **What to do**: Under the "Tail log directory..." item in the README, add:
  - File → Watch Directory (Wildcard): minimal; just pick a directory + wildcard, new files open automatically
  - File → Open Wildcard Monitor...: full configuration; encoding/interval/colors etc. can be configured

  **References**: The existing wildcard notes in README.md

  **Acceptance Criteria**: The README clearly explains both entries

  **Commit**: YES | Message: "docs: update README to explain both wildcard monitor entries" | Files: [README.md]

- [ ] 5. Final verification: run the Release build + tests

  **What to do**: Run ./script/build.ps1 --release and make sure there are 0 errors/warnings (ignore pre-existing warnings). Run dotnet test and make sure there are 0 failures.

  **References**: CLAUDE.md

  **Acceptance Criteria**: Build passes, tests pass

  **QA Scenarios**:
  ```
  Scenario: Release build passes
    Tool: Bash
    Steps: ./script/build.ps1 --release
    Expected: 0 errors, 0 warnings (ignoring pre-existing ones)
    Evidence: Build output
  ```

  **Commit**: NO | Do not commit automatically; committed per convention when a later /start-work wraps up

## Final Verification Wave
- [ ] F1. Plan Compliance Audit — manually check conformance with the plan
- [ ] F2. Manual QA walkthrough: menu → dialog → pick directory → new files open automatically
- [ ] F3. Scope Fidelity Check — confirm no complex configuration was added, only directory + wildcard

## Commit Strategy
- One commit per task (except the F wave)
- Commit in task order 1→2→3→4
- Final wrap-up commit by /start-work (per CLAUDE.md)

## Success Criteria
- Minimal entry is visible and usable
- After picking a directory + wildcard, new/changed files are automatically shown in the UI
- Release build passes
