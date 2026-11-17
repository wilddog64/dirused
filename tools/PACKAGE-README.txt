dirused - Windows disk usage for local drives, UNC paths and \\node admin shares

Contents
  dirused.exe      self-contained single file; no .NET install needed
  live-check.ps1   live checks (L1-L15) plus optional fleet checks (F1-F2)
  README.txt       this file

dirused uses your current Windows login only. It never asks for or stores
credentials. Admin shares (C$, D$) need an account that is an administrator
on the target machine.

Quick start (PowerShell, from the unzipped folder)
  .\dirused.exe /help
  .\dirused.exe C:\                      local drive, top 50 folders and files
  .\dirused.exe \\server\c$\apps         one folder on another machine
  .\dirused.exe \\server                 every <letter>$ admin share on server
  .\dirused.exe \\server /summary        volume usage; a warn/crit volume also lists
                                         its top 3 folders

Fleet run
  Put one target per line in a text file. Lines starting with # and blank
  lines are ignored:
      # app servers
      \\app01
      \\app02\d$\logs
  Then:
      .\dirused.exe /file fleet.txt /summary /parallel 8 /timeout 60s
      .\dirused.exe /file fleet.txt /parallel 8 /timeout 5m /csv > fleet.csv

Exit codes
  0  every target completed
  1  usage error or unreadable target file
  3  at least one target incomplete, failed or timed out
     (the others still report)
  4  at least one volume at or above /crit; takes precedence over 3

Live checks
  powershell -ExecutionPolicy Bypass -File .\live-check.ps1
  powershell -ExecutionPolicy Bypass -File .\live-check.ps1 -Targets fleet.txt

  L1-L15 run against this machine (\\localhost and C:\). -Targets adds F1
  (fleet /summary: every failure must be labelled UNREACHABLE, ACCESS DENIED,
  TIMED OUT or CANCELLED)
  and F2 (a full fleet scan with /parallel 8 finishes). Save the output and
  send it back; it is the live evidence.

If Windows blocks the exe ("Windows protected your PC" or "blocked by
policy"): the build is unsigned. Right-click the zip > Properties > Unblock
before extracting, or ask IT; do not disable security controls.
