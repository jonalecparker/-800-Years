---
description: Archive the session, refresh next-session doc, commit and push
---

Wrap up this coding session. Work through these steps in order.

## Step 1 — save the Unity project first (skip during design phase)

**If this folder has no Unity project yet** (no `Assets/` directory — this project is design-docs-only until game one ships), skip straight to Step 2.

Otherwise, before touching anything else, make sure no in-editor work is lost:
- Try to save via UnityMCP: run `execute_menu_item` for `File/Save` (saves the active scene) and `File/Save Project` (saves assets and project settings).
- **If the save succeeds, continue to Step 2.**
- **If UnityMCP isn't connected, Unity isn't running, or either save call errors, STOP.** Ask me to save the project manually (Ctrl+S, then File → Save Project) and wait for my confirmation before continuing with the rest of the wrap.

## Step 2 — verify what actually landed

Before writing anything down, check the working tree:
- `git status` to see what changed
- `git diff --stat` for the scope
- For any file you're about to claim you "added" or "rewrote," confirm it actually exists / changed.

Be honest. If something is half-finished, abandoned, or didn't work, that goes in the writeup as such — don't memorialize aspirational work as done.

## Step 3 — synthesize the session from this conversation

Pull from our conversation:
- The session's goal (what we set out to do)
- What we built or changed, with file names
- Trade-offs decided and why
- Bugs hit and fixed
- Things tried that didn't stick (real dead ends, not minor iterations)
- Open issues discovered at the end

Mirror the format of the existing `Docs/SessionLog/*.md` files (read one to match the structure). If you're missing a detail that matters, ask me one consolidated question — don't pepper me with several.

## Step 4 — write the docs

- **Archive.** Write a new file `Docs/SessionLog/<today>.md` (use today's date from your context — `currentDate`). If a file for today already exists, append to it under a horizontal rule rather than overwriting.
- **Refresh `Docs/NextSession.md`.** Rewrite it so next session has a focused "where we are + what's next" doc:
  - Update the "Where we left off" line.
  - Drop items we completed this session.
  - Carry forward items still pending.
  - Add new priorities / backlog items discovered this session.
  - Keep it to roughly one page.
- **Only if architecture or tooling materially changed** (new system added, old system replaced, new debug tool built), update `CLAUDE.md`. For each new or changed system, write at most 3–5 bullets covering only: (1) non-obvious gotchas, (2) "do not touch / do not replace" warnings, (3) cross-cutting constraints other systems must know about. Do NOT describe what the code does — that's readable from the files. The test: would a developer reading the code be surprised by this? If not, leave it out. Do NOT touch `CLAUDE.md` for bug fixes, parameter tuning, or anything already captured in the session log.

Show me a quick summary of what you're about to commit (which files, one-line description of each change).

## Step 5 — clear the screenshot scratch folder

`Assets/Screenshots/` holds Claude's verification captures — working artifacts, not project assets. It's gitignored (except `.gitkeep`), so nothing there is in version control.

- Delete `Assets/Screenshots/*.png` and `*.png.meta`. Leave `.gitkeep` alone.
- **Look before deleting.** If a capture doesn't look like one you took this session (an unfamiliar name, or something I might have saved there myself), say so and leave it rather than wiping it.

## Step 6 — commit and push

- `git add -A` (commits everything in the working tree, including in-progress code — this is the project policy). Quickly scan the list for anything that looks like a secret or a giant binary; flag it before committing if so.
- Commit with a message styled after recent commits — check `git log -3 --pretty=format:"%s%n%n%b"` for the format. Subject line is a short summary; body is a bulleted list of what changed and why. Include a `Co-Authored-By: <current Claude model> <noreply@anthropic.com>` trailer (e.g. `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`).
- `git push`

Report back: the commit hash, the subject line, and one sentence on what's queued up for next session.
