# Changelog

All notable changes to dirused. Versions before 1.0 are pre-releases; switches may still change.
Each release's section here becomes its GitHub release notes. A tag with no section of its own gets the
`[Unreleased]` section instead, so a release can be tagged without renaming it first.

## [Unreleased]

### Added
- `/purge` held-file detection: probes candidate files with share mode 0, discovers Procrun Tomcat services via read-only SCM queries, and reports `would delete after stopping <service>` in dry runs or `failed: in use by <service> (stop not available yet)` under `/confirm`.
- `/purge /confirm` stops Tomcat for held redirect logs, deletes them, restarts Tomcat, and verifies it returns to Running.
- Switch `/yes` with `/purge /confirm`: automates service stops within the off-hours window (19:00–06:00).
- Automatic recovery warning on next run if a previously stopped Tomcat service remains down.
- Exit code 3 precedence on service start failure over exit 4 critical threshold warnings.
- `docs/purge.md`: comprehensive guide to the `/purge` decision path, statuses, held classification, and delete-list format with a Mermaid flowchart.
- `/savelist <file>` writes the reviewed `/largest /purge` candidates with NTFS volume serial, file ID, size and exact modified time; `/fromlist <file> /purge` rechecks that identity before deletion. Invalid and duplicate lines are reported and produce exit code 3.
- `/purge [days]` deletes the files `/largest <patterns>` lists that are older than days (default 1) and at least `/minsize` (default 1GB); a dry run unless `/confirm`; each deletion is logged to `C:\temp\dirused-purge.log`.
- `/largest` lists the largest log, dump, backup and temp files under each target (patterns overridable); `/largestcount` sets how many.
- Daily new-release notices on stderr and an explicit `/update` command that verifies the package SHA-256 before replacing `dirused.exe`.
- `/version` and `/noupdatecheck`.

### Changed
- The delete list (`/savelist`) format is updated to v3 (`# dirused delete list v3`): the purge header writes minsize in human-readable units (e.g. `minsize=1GB`, `minsize=1536MB`, `minsize=1000B`), and rows include a display-only `size` column (e.g. `138G`) after `bytes`. `/fromlist` continues to verify exact byte counts against `bytes` and accepts legacy v1 and v2 lists.
- The delete list (`/savelist`) format is updated to v2 (`# dirused delete list v2`): modification times and `# created=` use local time with explicit offset and 7-digit subsecond fraction (`yyyy-MM-dd HH:mm:ss.fffffff zzz`), readable at a glance without manual time zone conversion. `/fromlist` reads both v2 and legacy v1 lists and preserves exact UTC ticks.
- `/confirm` now checks NTFS volume serial and file ID for scan rows (`skipped: changed since scan (id)`) before deletion.
- D1 age exemption: held Tomcat redirect logs are exempt from the purge age rule.
- `/fromlist` dry runs now apply the same regular-file, hard-link, containment and read-only checks as `/confirm`; invalid list paths and purge-log open failures exit cleanly, and list HTML shows only purge results.
- `/purge` labels files smaller than `/minsize` as `smaller than` before any age check.
- `/purge` text and HTML output omit files smaller than `/minsize` and note how many were hidden; JSON and CSV still list every file.
- `/purge` dry runs no longer require `/largest` patterns; explicit patterns, non-match-all, and full pattern lists are required only with `/confirm`.
- Update lookup every 15 minutes (was daily) and always on `/help`. HTML largest-files rows are colored by size and show a size bar.
- `/file` followed by another switch now says the file name is missing. The update notice also says how to stop checking and links the release notes.
- The update notice now repeats on each run until you update (was once per version).
- Update notice also appears on a bare `dirused` and `/help`.
- `/html` report: OK volumes show a green bar and OK label; small usage stays visible.

## [v0.0.10] - 2026-11-17

### Changed
- The `/html` report colours by `/warn` and `/crit`: a volume at or above `/crit` gets a red bar and a red `CRIT`
  label, one at or above `/warn` a yellow bar and a yellow `WARN` label, and each host's row in the nodes table takes
  the colour of its fullest volume. Below `/warn` the bar stays blue and the row stays plain.

## [v0.0.9] - 2026-11-17

### Added
- `/html` also writes an HTML report; console output is unchanged. The report has a nodes table (one row per host,
  worst first, with crit and warn counts), a volumes table with a Host column and a bar per volume, and a table of
  targets that failed. It needs no internet access and works in any browser.
- `/output <path>` picks where the report goes. With `/html` the default is `C:\temp\output.html`; the folder is
  created and the file is overwritten each run. Without `/html`, `/output` sends the text, JSON or CSV output to the
  file instead of the console. stderr names the file: `HTML report: <path>` or `Report: <path>`.
- live-check `L15`: `/html` and `/output`. The package README lists L1-L15.

### Notes
- A path that cannot be written stops dirused with exit 1 before any scan: `Cannot write report '<path>': <reason>`.
- `/output` needs a path. `/output` on its own, or followed by another switch (`/output /html`), fails with
  `/output requires a path`.
- Server names that differ only in case (`\\srv1` and `\\SRV1`) share one row in the nodes table.

## [v0.0.8] - 2026-11-17

### Changed
- A bare `/warn` or `/crit` (no number after it) now means its default, 85 or 90, and leaves the next argument alone,
  the same way `/deep` works. `dirused /summary /crit /warn /file servers.list` used to fail with
  `/crit requires an integer between 1 and 100`. A number after the switch still sets it: `/warn 70 /crit 80`.
- `/summary` walks each warn/crit volume once for its top folders, even when several targets point at the same volume.
  It used to walk every such target and throw away the duplicates, up to 60s each.

### Added
- live-check `L14`: bare `/warn` and `/crit` use the defaults. The package README lists L1-L14.

## [v0.0.7] - 2026-11-17

First build of the v0.0.6 changes (the v0.0.6 tag's release build failed, so it has no download).

### Fixed
- A top-folders test assumed the parallel workers finish in a fixed order, which failed the v0.0.6 release build at
  random. No change to the program.
- The quick-start README inside the zip described `/summary` as "no folder walk". It now says a warn/crit volume also
  lists its top 3 folders.

## [v0.0.6] - 2026-11-17 (no download; see v0.0.7)

### Added
- `/summary` lists the top 3 folders of every warn or crit volume, largest first, under that volume's line. The extra
  walk is one level deep, obeys `/parallel`, and uses `/timeout` (60s per target when not given). If it cannot finish,
  the volume shows `top folders incomplete: <path>: <reason>`; the exit code and footer counts are unaffected.
  JSON summary output carries the folders too; CSV does not.

## [v0.0.5] - 2026-11-17

### Added
- Fullness levels: `/warn` (default 85) and `/crit` (default 90). A volume at or above a level is tagged `[WARN]`
  (yellow) or `[CRIT]` (red).
- `/summary` ends with a footer: `N volume(s): X at or above 90%, Y at or above 85%`.
- Exit code 4 when any volume is at or above `/crit`. It takes precedence over exit 3 (a target incomplete, failed or
  timed out).
- JSON and CSV output carry the level.
- live-check `L11` (crit tag, footer, exit 4) and `L12` (`/warn` at or above `/crit` is refused).

## [v0.0.4] - 2026-11-17

### Changed
- A scan stopped by `/timeout` or Ctrl+C names the folder it was in. Empty and last folders get a stop line too, and
  `/csv` writes the folder name to stderr.
- `System Volume Information` at a volume root that you may not read shows as "system only (not counted)" instead of
  an access error.

## [v0.0.3] - 2026-11-17

### Added
- Fast default: a top-level scan with readable sizes after the path. `/deep [n]` goes n levels down, and `/exact`
  counts every file exactly.
- Hard links are counted once.

### Fixed
- Text rows sort by the size they display.
- `\\?\` targets print as plain paths.

## [v0.0.2] - 2026-11-16

### Fixed
- `/summary` tells hosts apart even when their volumes share a serial number.
- `/all` with targets lists each target host's volumes, never the local machine's.
- The `/all` help line fits in 80 columns.

## [v0.0.1] - 2026-11-15

First pre-release.

### Added
- Folder and volume sizes for local paths, UNC paths and remote `\\host` targets.
- Fleet mode: `/file <list>`, `/summary`, `/timeout`, `/parallel`, `/help`.
- A target that cannot be reached shows UNREACHABLE (Windows errors 51, 64 and 1707). A hung scan is abandoned at
  `/timeout` plus a grace period and shows TIMED OUT. Ctrl+C shows CANCELLED.
- Release zips for win-x64 and win-arm64 with `dirused.exe`, `live-check.ps1` and a quick-start README.
