# Orbit project workflow

## Project

Orbit is a native Windows WPF desktop assistant. Keep its interface clear,
readable and minimal. Preserve the stable toolbar position, fixed task viewport,
automatic three-entry clipboard history and external clipboard dragging.
Use the existing C# 5 / .NET Framework 4.8 toolchain without adding packages
unless the feature requires them.

## GitHub destination and standing authorization

The user explicitly requested automatic source-tree uploads after each completed
update and chose a dedicated repository for this project. The latest verified account is
`TsuIppaku`, GitHub profile ID `153787250`. The target is
`https://github.com/TsuIppaku/Singularity`, branch `main`. The current Orbit
application occupies this repository's root; preserve any future unrelated
project directories when synchronizing.

This destination supersedes earlier account choices. Never use a different
connected account or the machine-wide Git identity for Orbit uploads. Do not
change the machine's global Git identity or remove its other account connections.

For every completed update:

1. Finish the requested change and the checks relevant to it. `build.ps1
   -SmokeTest` runs the app and component checks with isolated test data.
2. Run `scripts/prepare-github-tree.ps1` and review the paths in the generated
   `artifacts/github-tree.json`. Publish source, tests, build tooling and project
   documentation. Keep personal task, note and clipboard data, credentials,
   generated binaries and test artifacts outside the upload.
3. Prefer the user's GitHub plugin. Re-discover its account selector and verify
   `get_profile` identifies `TsuIppaku` before a write. A connection's display
   name or the local Git `user.name` alone does not establish authentication.
4. Read the current `main` commit and its tree. If remote source changes have
   appeared since the previous synchronization, integrate them before publishing.
   Build the new tree on that remote tree. Retain unrelated remote files; delete
   only formerly managed project paths intentionally removed in this update.
5. Create one meaningful commit with the current remote head as parent, then
   update `main` with that head as `expected_sha`, using a normal fast-forward.
   Never force-push or rewrite someone else's history. If the head has changed,
   inspect and integrate the change before retrying.
6. Verify the resulting remote commit/tree and report its URL. With native Git
   access, align local history with the published commit while preserving local
   edits. Never use a destructive reset to make synchronization appear clean.

Uploading a completed project update is already authorized; do not ask for the
same permission each time. A missing repository, unavailable connection or
conflict is a concrete condition to resolve, not a successful upload. Report
what is pending and retain all local work if synchronization cannot finish.

The user supplied the repository URL above on 2026-11-16. Verify its remote
head and permissions before synchronizing; an origin URL alone is not proof
that a push or upload succeeded.
