# WallTerm installer / uninstaller. Run through Install.cmd, "Install (run at startup).cmd" or Uninstall.cmd.
#   -Action Install          copy to Program Files, Start Menu shortcut, PATH, Add/Remove Programs entry, launch
#   -Action InstallStartup   the same, plus a Task Scheduler logon task that starts WallTerm elevated at sign-in
#   -Action Uninstall        remove all of the above (settings in %USERPROFILE%\.config\wallterm are kept)
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'InstallStartup', 'Uninstall')]
    [string]$Action
)

$ErrorActionPreference = 'Stop'

$InstallDir = Join-Path $env:ProgramFiles 'WallTerm'
$InstalledExe = Join-Path $InstallDir 'WallTerm.exe'
$ShortcutPath = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\WallTerm.lnk'
$UninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WallTerm'
$TaskName = 'WallTerm'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-Arch {
    $a = $env:PROCESSOR_ARCHITEW6432
    if (-not $a) { $a = $env:PROCESSOR_ARCHITECTURE }
    switch ($a) {
        'ARM64' { 'arm64' }
        'AMD64' { 'x64' }
        default { throw "Unsupported processor architecture: $a (WallTerm needs x64 or ARM64)." }
    }
}

function Test-DesktopRuntime {
    # The exe is framework-dependent: it needs the .NET Desktop Runtime 8 or newer for this architecture.
    $dir = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'
    if (-not (Test-Path $dir)) { return $false }
    foreach ($d in Get-ChildItem $dir -Directory) {
        $v = $null
        if ([version]::TryParse(($d.Name -split '-')[0], [ref]$v) -and $v.Major -ge 8) { return $true }
    }
    return $false
}

function Stop-WallTerm {
    $procs = @(Get-Process WallTerm -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }
    Write-Host 'Closing the running WallTerm...'
    $quit = $null
    if ([Threading.EventWaitHandle]::TryOpenExisting('Local\WallTerm.Quit', [ref]$quit)) {
        $quit.Set() | Out-Null
        $quit.Dispose()
    }
    foreach ($p in $procs) {
        if (-not $p.WaitForExit(5000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
}

function Send-EnvironmentChange {
    if (-not ('WallTermSetup.Native' -as [type])) {
        Add-Type -Namespace WallTermSetup -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
    }
    $r = [UIntPtr]::Zero
    # HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG
    [WallTermSetup.Native]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$r) | Out-Null
}

function Set-MachinePath([switch]$Remove) {
    # Read and write the raw registry value so %SystemRoot%-style entries are not expanded and flattened.
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment', $true)
    try {
        $raw = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $parts = @($raw -split ';' | Where-Object { $_ -and ($_.TrimEnd('\') -ne $InstallDir) })
        if (-not $Remove) { $parts += $InstallDir }
        $new = $parts -join ';'
        if ($new -ne $raw) {
            $key.SetValue('Path', $new, [Microsoft.Win32.RegistryValueKind]::ExpandString)
            Send-EnvironmentChange
        }
    }
    finally { $key.Dispose() }
}

function Remove-StartupTask {
    & "$env:SystemRoot\System32\schtasks.exe" /Query /TN $TaskName 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        & "$env:SystemRoot\System32\schtasks.exe" /Delete /TN $TaskName /F | Out-Null
        Write-Host 'Removed the sign-in task.'
    }
}

function Install-WallTerm([bool]$Startup) {
    $arch = Get-Arch
    $source = Join-Path $PSScriptRoot "WallTerm-$arch.exe"
    if (-not (Test-Path $source)) { throw "Missing $source. Run this from the extracted release folder." }

    if (-not (Test-DesktopRuntime)) {
        Write-Warning ".NET Desktop Runtime 8 (or newer) for $arch was not found. WallTerm will not start without it."
        Write-Host   "  Download: https://dotnet.microsoft.com/download/dotnet/8.0  (Windows Desktop Runtime, $arch)"
        if ((Read-Host 'Install anyway? [y/N]') -notmatch '^(y|yes)$') { return }
    }

    Stop-WallTerm

    Write-Host "Installing to $InstallDir"
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Copy-Item $source $InstalledExe -Force
    Copy-Item $PSCommandPath (Join-Path $InstallDir 'setup.ps1') -Force
    Get-ChildItem $InstallDir | Unblock-File

    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($ShortcutPath)
    $lnk.TargetPath = $InstalledExe
    $lnk.WorkingDirectory = $InstallDir
    $lnk.Description = 'Live PowerShell terminal as the desktop background'
    $lnk.Save()
    Write-Host 'Added Start Menu shortcut.'

    Set-MachinePath
    Write-Host "Added $InstallDir to PATH (new shells can run 'wallterm')."

    $version = (Get-Item $InstalledExe).VersionInfo.FileVersion
    if ($version) { $version = (($version -split '\.')[0..2]) -join '.' }
    New-Item -Path $UninstallKey -Force | Out-Null
    $uninstallCmd = "`"$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe`" -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\setup.ps1`" -Action Uninstall"
    $values = @{
        DisplayName     = 'WallTerm'
        DisplayVersion  = $version
        DisplayIcon     = $InstalledExe
        InstallLocation = $InstallDir
        UninstallString = $uninstallCmd
        Publisher       = 'WallTerm'
    }
    foreach ($k in $values.Keys) { Set-ItemProperty -Path $UninstallKey -Name $k -Value $values[$k] }
    Set-ItemProperty -Path $UninstallKey -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $UninstallKey -Name NoRepair -Value 1 -Type DWord
    Set-ItemProperty -Path $UninstallKey -Name EstimatedSize -Value ([int]((Get-Item $InstalledExe).Length / 1KB)) -Type DWord
    Write-Host 'Registered in Settings > Apps > Installed apps.'

    if ($Startup) {
        $p = Start-Process $InstalledExe -ArgumentList '--install-startup' -Wait -PassThru -WindowStyle Hidden
        if ($p.ExitCode -ne 0) { throw "WallTerm --install-startup failed (exit code $($p.ExitCode))." }
        Write-Host 'WallTerm will start automatically (elevated, no UAC prompt) when you sign in.'
        & "$env:SystemRoot\System32\schtasks.exe" /Run /TN $TaskName | Out-Null
    }
    else {
        Remove-StartupTask
        Start-Process $InstalledExe -WorkingDirectory $InstallDir
    }

    Write-Host "`nWallTerm $version installed and started." -ForegroundColor Green
}

function Uninstall-WallTerm {
    Stop-WallTerm
    Remove-StartupTask

    Set-MachinePath -Remove
    if (Test-Path $ShortcutPath) { Remove-Item $ShortcutPath -Force }
    if (Test-Path $UninstallKey) { Remove-Item $UninstallKey -Recurse -Force }

    if (Test-Path $InstallDir) {
        Set-Location $env:SystemRoot
        Remove-Item $InstallDir -Recurse -Force
        Write-Host "Removed $InstallDir"
    }

    Write-Host "`nWallTerm uninstalled." -ForegroundColor Green
    Write-Host "Your settings were kept in $env:USERPROFILE\.config\wallterm (delete that folder to remove them)."
}

# Elevate once up front; everything after this runs without further UAC prompts.
if (-not (Test-Admin)) {
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Action $Action"
    try {
        Start-Process "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Verb RunAs -ArgumentList $argList
    }
    catch {
        Write-Host 'Administrator permission is required; nothing was changed.' -ForegroundColor Yellow
        Read-Host 'Press Enter to close' | Out-Null
    }
    exit
}

try {
    switch ($Action) {
        'Install'        { Install-WallTerm $false }
        'InstallStartup' { Install-WallTerm $true }
        'Uninstall'      { Uninstall-WallTerm }
    }
}
catch {
    Write-Host "`nError: $($_.Exception.Message)" -ForegroundColor Red
}
Read-Host "`nPress Enter to close" | Out-Null
