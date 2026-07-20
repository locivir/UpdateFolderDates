# UpdateFolderDates

A small Windows command-line tool that rewrites each **directory's** timestamps to reflect the files inside it. When you copy or restore a folder tree, every directory's created/modified date is reset to the copy time. `UpdateFolderDates` walks the tree and restores meaningful folder dates derived from the files they contain.

It only ever changes **directory timestamps** and its own **configuration files** — never file contents, file timestamps, permissions, ownership, or ACLs.

- Runtime: Windows, .NET Framework 4.8
- Console application, `asInvoker` (never requests administrator elevation)

## What it does

For every directory in the scanned tree:

- **Creation time** is set to the **earliest** timestamp among the included descendant files, considering both each file's (valid) creation time **and** its last-write time.
- **Modified time** is set to the **latest** last-write time among the included descendant files.
- A file creation time earlier than **1980-01-01** is treated as invalid (a legacy/FAT sentinel) and that file's last-write time is used instead.

Because last-write times also count toward the creation calculation, a directory's creation time can legitimately end up **earlier** than a file's own creation time. Existing directory timestamps are never used as inputs, and **empty or fully-excluded directories are left unchanged**.

Extrema propagate upward, so every parent reflects all eligible files in its subtree.

## Features

- Recursive scan that computes a full plan **before** writing anything.
- File and directory **filters** with `*` and `?` wildcards.
- **Dry-run** mode to preview the exact plan without changing anything.
- **Confirmation prompt** before any real change (skippable for automation).
- Never follows **reparse points** (junctions, symbolic links, mount points), so it can't leave the target tree or loop on a cycle.
- **Filesystem-root guard** (drive and UNC share roots require an explicit opt-in).
- Robust error handling: one inaccessible file or folder never crashes the run and never lets a directory be updated from a partially-scanned subtree.
- **Automation-safe**: quiet mode, deterministic exit codes, and it never blocks waiting for a keypress.

## Requirements

- **To run:** Windows with the .NET Framework 4.8 runtime (included in Windows 10 1903+ and Windows 11).
- **To build:** the .NET SDK (`dotnet build`) or Visual Studio 2022 / Build Tools with the .NET Framework 4.8 targeting pack.

## Building from source

```
dotnet build UpdateFolderDates.csproj -c Release
```

The executable is produced at `bin\Release\UpdateFolderDates.exe`.

### Strong-name signing

The signing key (`Key.snk`) is **private and intentionally not included in this repository** (see `.gitignore`). The build adapts automatically:

| Situation | Result |
| --- | --- |
| `Key.snk` present at the project root | Builds **signed** automatically. |
| No key present | Builds **unsigned** (developer fallback) with a clear warning. |
| `-p:RequireStrongNameSigning=true` and no key | Build **fails** with a clear error. |

You can point the build at a key stored outside source control (for CI/production):

```
dotnet build UpdateFolderDates.csproj -c Release -p:StrongNameKeyPath=C:\secure\Key.snk -p:RequireStrongNameSigning=true
```

`StrongNameKeyPath` defaults to `Key.snk` in the project directory.

## Usage

```
UpdateFolderDates [options] <path> [<path> ...]
```

| Option | Description |
| --- | --- |
| `/ini=<file>` | Use a specific configuration file. |
| `/created[=true\|false]` | Update creation timestamps. Default `false`; no value means `true`. |
| `/modified[=true\|false]` | Update modified timestamps. Default `true`; no value means `true`. |
| `/verbose[=true\|false]` | Extra diagnostics. Does **not** change what gets updated. |
| `/quiet[=true\|false]` | Suppress informational output (errors still go to stderr). |
| `/dryrun[=true\|false]` | Scan and show the plan but change nothing. |
| `/yes[=true\|false]` | Skip the confirmation prompt (required for non-interactive runs). |
| `/allowroot[=true\|false]` | Permit operating on a drive or UNC share root. Default `false`. |
| `/filter=<pattern>` | Exclude matching entries. Repeatable. |
| `/defaults[=true\|false]` | Merge command-line filters with configuration-file filters instead of replacing them. |
| `/list` | Show the effective filters and exit (no traversal). |
| `/save[=<file>]` | Save the effective configuration and exit (no traversal). |

Boolean options accept only `true` or `false`; an invalid value (e.g. `/created=yes`) is rejected. Unknown options are rejected.

### Examples

```
:: Preview what would change (safe, no writes, no confirmation)
UpdateFolderDates D:\Photos /created /dryrun

:: Update modified times (default), with an interactive confirmation
UpdateFolderDates D:\Photos

:: Non-interactive: update created + modified without prompting
UpdateFolderDates D:\Photos /created /yes

:: Exclude some files, then run unattended
UpdateFolderDates D:\Projects "/filter=*.tmp" "/filter=*.bak" /yes

:: Save a reusable configuration, then just run against a folder
UpdateFolderDates "/filter=*.tmp" "/filter=*.bak" /created /save
UpdateFolderDates D:\Projects /yes

:: Show the effective filters
UpdateFolderDates /list
```

> Tip: wrap wildcard filters in quotes so your shell passes them through literally.

## Filters

Filter grammar:

- `*` matches zero or more characters; `?` matches exactly one character.
- Matching is **anchored to the whole name** and is **case-insensitive**.
- A pattern containing a path separator is matched against the whole path instead of just the name.
- A trailing `\` or `/` marks a **directory-only** filter, which excludes that directory **and its entire subtree**.
- A file filter never matches a directory, and a directory filter never matches a file.
- `thumbs.db` and `desktop.ini` are **always excluded** automatically and never need to be listed.

### Command-line vs. configuration filters (`/defaults`)

| Command line | `/defaults` | Effective filters |
| --- | --- | --- |
| no `/filter` | — | the configuration file's filters |
| one or more `/filter` | `false` (default) | command-line filters **replace** the configuration file's |
| one or more `/filter` | `true` | command-line filters **merge** with the configuration file's |

Filters are de-duplicated case-insensitively while preserving order.

## Configuration file

If `/ini` is not given, the default configuration file is searched for in this order:

1. Next to the executable (`UpdateFolderDates.ini`).
2. `%LOCALAPPDATA%\Locivir\UpdateFolderDates.ini`.

Command-line arguments always override configuration-file settings, regardless of order. Create a starting file with `/save` (see examples). Configuration lines look like:

```
# Lines starting with # are ignored.
/created=false
/modified=true
# One filter per line (single quotes optional):
*.tmp
'*(keep).jpg'
build\
```

## Confirmation and automation

A real, timestamp-changing run asks for confirmation (`y`/`yes`; the default is no). For scheduled tasks, pipelines, or any non-interactive use, pass `/yes`. If input is redirected or `/quiet` is active and `/yes` is not supplied, the tool refuses to make changes and exits with code `1` rather than hanging. Dry-run, `/list`, and `/save` never prompt.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success (including a successful dry-run, `/list`, or `/save`). |
| `1` | Command-line, path, configuration, or safety-validation error. No timestamps were changed. |
| `2` | Completed only partially — one or more entries failed or were incomplete. |
| `3` | Confirmation was declined. No timestamps were changed. |
| `4` | Unexpected fatal error. |

## Safety notes

- **Reparse points** (junctions, symlinks, mount points) are never followed, for both directories and files; a root path that is itself a reparse point is rejected. This keeps the scan inside the requested tree and prevents cycles from causing infinite recursion.
- **Drive and UNC roots** are rejected unless `/allowroot=true` is given explicitly. (A volume root's own timestamp cannot be set by Windows, so with `/allowroot` the tool updates the root's contents and leaves the root itself unchanged.)
- If a directory cannot be fully scanned (e.g. access denied), that directory and every ancestor whose result depends on it are left unchanged; independent siblings and roots are still processed, and the run reports exit code `2`.
- The tool changes **only directory timestamps** and its own configuration files.

## License

This software is released into the public domain under **[The Unlicense](https://unlicense.org/)**. See the [`LICENSE`](LICENSE) file for the full text.
