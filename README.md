# dirused

A Windows disk usage and purge tool: from one node, measure local drives and UNC paths across many machines, largest
files first, and free space safely. Private; plan and milestones live in k3d-manager
`docs/plans/dirused-windows-disk-usage-tool.md` until they move here.

## Development VM

Windows 11 ARM64 on VirtualBox (M4), provisioned by Ansible, with a second disk as `D:`.

    make vm-up        # first run: creates .venv, installs collections, boots and provisions
    make vm-check     # OS, .NET SDK, Git, volumes
    make vm-provision # rerun the playbook
    make vm-halt

Pinned: box `stromweld/windows-11` 202511.21.0 (virtualbox, arm64), .NET SDK 10.0.404 (LTS), Git 2.56.1,
PowerShell 7.6.10, ansible-core 2.22.0, pywinrm 0.5.0, ansible.windows 3.9.2, chocolatey.chocolatey 1.6.0.

The VM is for running and looking at the tool. Builds and tests also run on the `windows-latest` CI runner, which is
the offline gate. Multi-node UNC and purge testing needs real nodes (ACG); see the plan's CLI gate.

## Usage

    dirused .
    dirused C:\path /top 10
    dirused D:\
    dirused D:\ /deep
    dirused D:\ /deep 3
    dirused D:\ /exact
    dirused D:
    dirused d:/apps
    dirused sub/dir
    dirused \\server\share\path
    dirused //server/share/path
    dirused \\node
    dirused /all
    dirused C:\path /json
    dirused C:\path /csv
    dirused /file targets.txt /summary
    dirused /file targets.txt /timeout 30s /parallel 4
    dirused --help

A bare node scans its lettered administrative disk shares (such as `C$` and `D$`). Admin shares require administrator rights on the target; scans use the current Windows token. Relative paths resolve against the current directory.

### Output format

By default, dirused prints the volume summary line followed by top-level entries (depth 1) sorted largest first, with human-readable sizes:

```
C:\  used 42.1G, free 57.9G, total 100G, 42.1% used
C:\Users          28G
C:\Windows        12G
C:\Program Files   2.1G
C:\dump.bin      512M
```

- **Top-level by default:** A bare scan lists entries at depth 1 (immediate children of the target). Subtree totals are fully computed.
- **/deep [n]:** List entries down to `n` directory levels (1–32). Running `/deep` without an argument defaults to 2 levels.
- **/exact:** By default, scan speeds over the network by reading file length and locally rounding to the volume cluster size without per-file file opens or compression checks. Both the default scan and `/exact` count a hard-linked file once. With `/exact`, dirused adds true on-disk sizes (`GetCompressedFileSize`).
- **On disk vs. logical:** "Logical" size is the apparent file length; "on disk" is the space allocated in whole volume clusters (e.g. 4 KB on NTFS, so a 27.2 KB file occupies 28 KB), smaller only for compressed or sparse files. In `/exact` mode, any row whose on-disk size differs from its logical size by more than one cluster appends `(<size> on disk)`.

### Largest cleanup candidates

`/largest` lists matching files anywhere under each target. It defaults to 20 files; `/largestcount N` accepts 1–1000.
The built in, case insensitive file name patterns are:

| Group | Patterns |
|---|---|
| Logs | `*.log`, `*.log.*`, `*.out` |
| Heap and crash dumps | `*.hprof`, `*.phd`, `javacore*.txt`, `*.dmp`, `*.mdmp`, `*.trc`, `*.etl` |
| Backups | `*.bak`, `*.bkp`, `*.old`, `*.orig`, `*.backup` |
| Temp | `*.tmp`, `*.temp`, `~$*`, `*.~*`, `*.swp`, `*.$$$` |
| Partial transfers | `*.part`, `*.partial`, `*.crdownload`, `*.filepart` |

Pass a plain list to replace the defaults (`/largest "*.hprof;*.dmp"`), a `+` list to add patterns
(`/largest "+*.trc2;+*.out2"`), or `*` to include every file. A single exact name needs a trailing semicolon,
for example `/largest "catalina.out;"`. In Windows, write a relative folder whose name starts with `+` as `.\+name` so `/largest` does not consume it as a pattern.
Without `/purge`, dirused only lists candidates and does not open them.
In the HTML report, files of 10 GB or more are red, 1 GB or more amber, and each row has a bar scaled to the largest file in its group.

### Purging listed files

`/purge [days]` acts only on the files shown by `/largest [<patterns>]`, skips files modified within the requested age (default: 1 day), and selects files at least `/minsize` large (default: 1 GB). `/purge` alone is a dry run and reports what would be deleted. Add `/confirm` to permanently delete eligible files. Purge dry runs work without `/largest` patterns; only `/confirm` requires explicit, non-match-all file patterns. Text and HTML output do not list files smaller than `/minsize`; lower `/minsize` to see them; JSON and CSV list every file. See [docs/purge.md](docs/purge.md) for the full decision tree, in-use file detection, service classification, exit codes, and file formats.

The help describes the switches this way:

```
/purge [days]       Delete the files /largest lists that were last
                    modified more than [days] days ago (default: 1)
                    and are at least /minsize big (default: 1GB).
                    Patterns are needed only with /confirm; without
                    them the dry run lists what /largest finds.
                    Without /confirm this is a dry run: nothing is
                    deleted, the report shows what would be.
/minsize <size>     With /purge: smallest file to delete, e.g. 100MB,
                    999MB, 2GB (default: 1GB). Text and HTML output
                    do not list files smaller than /minsize; lower
                    /minsize to see them; JSON and CSV list every file.
/savelist <file>    Save eligible rows after the dry run
/fromlist <file>    Purge only rows in a saved list (requires /purge)
/schedule <time>    With /fromlist /purge: preview a one-time run at HH:MM
                    [PT|PST|PDT|UTC]; add /confirm to schedule it
/confirm            With /purge: really delete. Deletes are permanent
                    (admin shares like \\host\d$ skip the Recycle Bin).
                    Each deletion is logged to C:\temp\dirused-purge.log
/yes                With /purge /confirm: proceed without prompt
                    (stops Tomcat only between 19:00 and 06:00)

Purge examples:
  dirused D:\ /largest *.log /purge
      Dry run: list what would be deleted, delete nothing
  dirused D:\ /largest *.log /purge /confirm
      Delete the listed .log files of 1GB or more not modified in the last day
  dirused D:\ /largest *.log /purge /minsize 100MB
      Dry run: also include listed .log files from 100MB
  dirused /file servers.txt /largest *.log;*.hprof /purge 7 /confirm
      On every server, delete listed .log and .hprof files older than 7 days
```

Purging with `/confirm` requires explicit file patterns; match-all patterns and `+` additions are refused, and when `/largest` is given more than once the last one counts. Without `/confirm`, `/largest /purge` with no patterns is a dry run over the default list. Sizes require KB, MB, GB, or TB units and use powers of 1024. A file is skipped when it is smaller than `/minsize`, outside the target, has an ambiguous trailing dot or space, has an unknown or recent modification time, no longer exists, is a directory or reparse point, is hard-linked, or changed since the scan. Locked or denied files are reported as failed, except Tomcat redirect logs which can be deleted by stopping Tomcat under `/confirm` (or `/yes` off-hours). Dry runs do not open files.

Each confirmed run appends one tab-separated line per deleted or failed file to `C:\temp\dirused-purge.log`: UTC timestamp, user, machine, path, bytes, and status. Always run without `/confirm` first and read the list. A log still being written (modified today) is skipped; rotate it or stop the service first.

To preserve the exact reviewed set, add `/savelist <file>` to a dry run, for example:

```
dirused \\server\d$\logs /largest *.log /purge /savelist delete.txt
dirused /fromlist delete.txt /purge
dirused /fromlist delete.txt /purge /confirm
```

The UTF-8 delete list stores the target, NTFS volume serial, 64-bit file ID, size and full modified timestamp for each eligible file. You may remove lines before using it; only files whose identity and metadata still match are considered. The list’s saved age and minimum size are used. Bad or duplicate rows are skipped and reported; the command exits 3 when any are present. A file without a reported NTFS ID is not saved. `/fromlist` accepts `/confirm`, `/html`, `/output`, `/json` and `/csv` with `/purge`; it cannot be combined with scan targets or scan options.

### Switches

```
Usage: dirused [target...] [switches]

Targets:
  . | <rel>           Current or relative path
  C:\x | c:/x | C:    Local path or drive root
  \\server\share      UNC share (or //server/share)
  \\node | //node     All admin shares on a host
  /all                All local drives, or every drive on each target host

Switches (use /, - or -- in any case):
  /top <n>            Show top N entries (default: 50)
  /json               Output JSON (default: text)
  /csv                Output CSV (default: text)
  /html               Also write an HTML report
                      (default file: C:\temp\output.html)
  /output <path>      Write the report to <path>: the HTML file with /html,
                      otherwise the text, JSON or CSV output (default: console)
  /all                All local drives, or every drive on each target host
  /file <path>        Read targets from file, one per line (default: none)
  /summary            Volume usage; a /warn or /crit volume also lists
                      its top 3 folders (default: off)
  /warn [n]           Warning fullness threshold (default: 85)
  /crit [n]           Critical fullness threshold (default: 90)
  /deep [n]           List n directory levels, 1-32 (/deep alone: 2)
  /exact              On-disk sizes and hard-link dedup (slower)
  /timeout <n>[s|m|h] Per-target timeout, > 0 (default: none)
  /parallel <n>       Max concurrent scans, 1-64 (default: 4)
  /help               Show this help

Exit codes:
  0  All targets completed
  1  Usage error or unreadable target file
  3  Any target incomplete, failed, or timed out
  4  Any volume at or above /crit (takes precedence over 3; service start failure returns 3)
```

Sample `targets.txt`:

    # Fleet targets (one per line, blank lines and # comments ignored)
    C:\
    \\server\share

When a scan is stopped by a timeout or cancellation, its errors name the folder in progress as `<path>: not finished: scan stopped`.
If `System Volume Information` at a volume root cannot be read, output labels it `<path>: system only (not counted)`; its usage is included in the volume's used figure.

Switches also accept `--` in place of `/`. JSON and CSV are mutually exclusive. Example: `dirused /file servers.list /summary /warn 80 /crit 95`. A path beginning with `/` or a file name beginning with `-` must be written as `\lists\servers.txt` or `.\-servers.txt`. Exit code 4 for a critical volume takes precedence over 3 for incomplete targets, except when a service start fails which returns exit 3.

The top-3 folder walk obeys `/timeout` (60s per target when `/timeout` is not given) and never affects the exit code.

## License

Apache License 2.0. See [LICENSE](LICENSE).
# Updates

dirused checks GitHub at most every 15 minutes (always on `/help`) on a scan or a bare `dirused`, and prints a short notice (the new version, how to update, how to stop checking and a release-notes link) to stderr on each run while a newer version exists; stop it with `/noupdatecheck` or `DIRUSED_NO_UPDATE_CHECK=1`. Checks are silent on failure and never change scan output or status.

Run `dirused /version` to see the installed version. `dirused /update` downloads the newest release for this architecture and checks its SHA-256 before replacing `dirused.exe`; updates are never installed automatically. The checksum and executable come from the same GitHub release, so this detects download corruption but does not independently authenticate the publisher. HTTPS certificate validation remains enabled.

The updater replaces only `dirused.exe`; adjacent `live-check.ps1` and `README.txt` are unchanged. If Windows blocks the new executable (for example, through AppLocker), restore the previous one by renaming `dirused.exe.old` to `dirused.exe`.
