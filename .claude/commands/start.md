---
description: Refresh on the project and recap where we left off
---

Start of a new coding session. Do these things, then stop and wait for me:

1. Make sure the local repo is up to date with GitHub:
   - Run `git fetch` to refresh remote tracking info.
   - Run `git status -sb` to see how local `main` compares to `origin/main` and whether the working tree is dirty.
   - **Up to date** → note it and move on.
   - **Behind and fast-forwardable** (local has no commits the remote lacks) → run `git pull --ff-only` and note what came in.
   - **Diverged** (local has commits the remote doesn't) → do NOT merge. Flag it and let me decide.
   - **Dirty working tree** → mention the uncommitted changes before pulling so nothing gets clobbered; if a fast-forward pull would touch those files, flag it instead of pulling.
2. Read `Docs/NextSession.md` to refresh on where we left off and what's next.
3. Run `git log --oneline -5` to see the most recent commits.
4. List `Docs/SessionLog/` so you know which session was most recent. If something in that most-recent log adds important context that `NextSession.md` glosses over, skim it — otherwise don't.

Then report back in this format, and nothing else:

**Repo sync:** one line — up to date, pulled N commits, or a warning (diverged / dirty tree).

**Last session** (the date of the most recent SessionLog entry): one sentence on what we accomplished.

**Today's top priorities** (from NextSession.md): the top 1–3 items, one line each.

**Recent commits:** the git log output, one per line.

Then stop. Do not start working. Wait for me to tell you what we're tackling — it might be one of the priorities, or it might be something new.

If this project has a CLAUDE.md, it's already loaded into your context automatically — don't re-read it. (During the design phase there may not be one yet.)
