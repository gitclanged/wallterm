WallTerm - live PowerShell terminal as your desktop background
==============================================================

Double-click one of:

  Install.cmd                     Install WallTerm and start it.
  Install (run at startup).cmd    Install WallTerm, start it, and start it automatically
                                  (elevated, no UAC prompt) every time you sign in.
  Uninstall.cmd                   Close WallTerm and remove it, its shortcut, PATH entry and
                                  sign-in task. Your settings are kept.

Each asks for Administrator permission once (WallTerm always runs as Administrator).

What Install does:
  - copies WallTerm.exe (x64 or ARM64, picked automatically) to C:\Program Files\WallTerm
  - adds a Start Menu shortcut and puts the folder on PATH, so `wallterm` works in any new shell
  - registers WallTerm in Settings > Apps > Installed apps (you can uninstall from there too)

Running Install.cmd again upgrades in place. Running plain Install.cmd after the startup variant
turns run-at-startup off again.

Requires the .NET Desktop Runtime 8 or newer:
  https://dotnet.microsoft.com/download/dotnet/8.0

Settings: run `wallterm settings` (edits %USERPROFILE%\.config\wallterm\config.ini), or `wallterm help`. Full manual: MANUAL.md