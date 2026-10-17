# Install disktiles for this user on Windows, with the GTK it needs. No admin;
# nothing else is touched. (Linux and macOS: install.sh.)
#
#   irm https://github.com/luisrudge/disktiles/releases/latest/download/install.ps1 | iex
#   & ([scriptblock]::Create((irm …/install.ps1))) uninstall      remove it again
#
# The app goes to %LOCALAPPDATA%\Programs\disktiles, with a Start menu entry
# and the `disktiles` command on the user's PATH. ARM64 runs the x64 build.
#
# The download is checked against the release's SHA256SUMS. Next to an
# unpacked release, or with DISKTILES_FROM=dir (a folder holding
# bin\disktiles.exe), it installs from there instead. DISKTILES_VERSION=v0.1.0
# picks a release; DISKTILES_URL=… downloads the assets from somewhere else
# (a mirror, or a local folder for a test). DISKTILES_INSTALL_DIR,
# DISKTILES_SHORTCUTS (the Start menu folder) and DISKTILES_NO_PATH=1 are for
# tests too.
param([string]$Action)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = 'luisrudge/disktiles'
$dir = if ($env:DISKTILES_INSTALL_DIR) { $env:DISKTILES_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\disktiles' }
$shortcuts = if ($env:DISKTILES_SHORTCUTS) { $env:DISKTILES_SHORTCUTS } else { [Environment]::GetFolderPath('Programs') }
$shortcut = Join-Path $shortcuts 'disktiles.lnk'
$exe = Join-Path $dir 'bin\disktiles.exe'
$parts = 'bin', 'share', 'lib', 'disktiles.cmd'

# The user's PATH, read and written as stored: as REG_EXPAND_SZ it keeps its
# %VARIABLES%, which [Environment]::SetEnvironmentVariable would expand.
function Set-UserPath([switch]$Add) {
    if ($env:DISKTILES_NO_PATH) { return }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    try {
        $kind = if ($key.GetValueNames() -contains 'Path') { $key.GetValueKind('Path') } else { 'ExpandString' }
        $old = $key.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
        $new = @($old -split ';' | Where-Object { $_ -and $_.TrimEnd('\') -ne $dir.TrimEnd('\') })
        if ($Add) { $new += $dir }
        if (($new -join ';') -ne $old) { $key.SetValue('Path', ($new -join ';'), $kind) }
    } finally { $key.Close() }
    # Setting a variable through .NET tells Explorer and new terminals that
    # the environment changed; this one is removed again straight away.
    [Environment]::SetEnvironmentVariable('DISKTILES_PATH_CHANGED', '1', 'User')
    [Environment]::SetEnvironmentVariable('DISKTILES_PATH_CHANGED', $null, 'User')
}

# disktiles.exe is a GUI program: PowerShell neither waits for it nor reads
# its output, unless asked to like this.
function Get-Version {
    if (-not (Test-Path -LiteralPath $exe)) { return '' }
    $out = [IO.Path]::GetTempFileName()
    try {
        Start-Process -FilePath $exe -ArgumentList '--version' -Wait -NoNewWindow -RedirectStandardOutput $out
        (Get-Content -LiteralPath $out) -join ''
    } catch { '' } finally { Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue }
}

function Assert-NotRunning {
    $running = Get-Process disktiles -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
    if ($running) { throw "disktiles is running from $dir; close it first" }
}

function Remove-Installed {
    foreach ($part in $parts) {
        $path = Join-Path $dir $part
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
}

if ($Action -eq 'uninstall') {
    Assert-NotRunning
    Remove-Installed
    if ((Test-Path -LiteralPath $dir) -and -not (Get-ChildItem -LiteralPath $dir -Force)) { Remove-Item -LiteralPath $dir }
    if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
    Set-UserPath
    Write-Host "removed $dir and $shortcut"
    return
}

# Where the files come from: DISKTILES_FROM, this script's own folder, or a
# download. Run through iex, the script has no folder: then only a download.
$from = $env:DISKTILES_FROM
if (-not $from -and $PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot 'bin\disktiles.exe'))) { $from = $PSScriptRoot }
$tmp = $null
try {
    if (-not $from) {
        $asset = 'disktiles-win-x64.zip'
        $base = if ($env:DISKTILES_VERSION) { "https://github.com/$repo/releases/download/$env:DISKTILES_VERSION" } else { "https://github.com/$repo/releases/latest/download" }
        if ($env:DISKTILES_URL) { $base = $env:DISKTILES_URL }
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ("disktiles-" + [Guid]::NewGuid())
        New-Item -ItemType Directory $tmp | Out-Null
        Write-Host "downloading $base/$asset"
        foreach ($name in $asset, 'SHA256SUMS') {
            if ($base -match '^https?://') {
                Invoke-WebRequest -UseBasicParsing -Uri "$base/$name" -OutFile (Join-Path $tmp $name)
            } else {
                Copy-Item -LiteralPath (Join-Path $base $name) -Destination $tmp
            }
        }
        $line = @(Get-Content (Join-Path $tmp 'SHA256SUMS')) -match " [ *]?$([regex]::Escape($asset))$" | Select-Object -First 1
        $actual = (Get-FileHash -Algorithm SHA256 (Join-Path $tmp $asset)).Hash
        if (-not ($line -match '^[0-9a-fA-F]{64}') -or $Matches[0] -ne $actual) { throw "$asset does not match the release's SHA256SUMS; nothing was installed" }
        $from = Join-Path $tmp 'unpacked'
        Expand-Archive -LiteralPath (Join-Path $tmp $asset) -DestinationPath $from
    }
    if (-not (Test-Path (Join-Path $from 'bin\disktiles.exe'))) { throw "no bin\disktiles.exe in $from" }

    $old = Get-Version
    Assert-NotRunning
    New-Item -ItemType Directory -Force $dir | Out-Null
    # The old bin\ goes first: a DLL a newer GTK no longer ships must not stay.
    Remove-Installed
    foreach ($part in 'bin', 'share', 'lib') {
        $path = Join-Path $from $part
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $dir -Recurse }
    }
    # The command on PATH is this, not bin\: other programs that search PATH
    # for a DLL must not find GTK's.
    Set-Content -LiteralPath (Join-Path $dir 'disktiles.cmd') -Value "@`"%~dp0bin\disktiles.exe`" %*" -Encoding Ascii

    New-Item -ItemType Directory -Force $shortcuts | Out-Null
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $env:USERPROFILE
    $link.Description = 'See what is using your disk, mark what should go, remove it'
    $link.Save()

    Set-UserPath -Add
    if (-not $env:DISKTILES_NO_PATH -and ($env:Path -split ';') -notcontains $dir) { $env:Path += ";$dir" }

    $new = Get-Version
    if (-not $new) { $new = 'disktiles (version unknown)' }
    if ($old -and $old -ne $new) { Write-Host "updated $old -> $($new -replace '^disktiles ', ''): $dir" }
    else { Write-Host "installed ${new}: $dir" }
    Write-Host 'open disktiles from the Start menu, or run (in a new terminal): disktiles [FOLDER]'
} finally {
    if ($tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}
