# Git Hooks

FitOSC uses repository-versioned Git hooks to catch common cleanliness and build issues before changes are shared.

## Install

From the repository root, run:

```bash
git config core.hooksPath .githooks
```

This setting is local to the clone and does not modify global Git configuration.

## Checks

The `pre-commit` hook:

- rejects whitespace errors in staged files;
- verifies that `dotnet format` would not change staged C# or Razor files.

Generated OpenVR bindings and existing legacy files are not reformatted automatically.

The `pre-push` hook:

- builds the solution in Release configuration;
- uses `--no-restore` so the check is deterministic after dependencies are restored.

Restore dependencies manually when needed:

```bash
dotnet restore FitOSC.sln
```

To check or remove the local hook configuration:

```bash
git config --get core.hooksPath
git config --unset core.hooksPath
```
