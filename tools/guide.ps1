<#
.SYNOPSIS
The START-HERE guide: asks where the server will run, finds the game and the AIR client, asks where to install, finds or installs
the tools a build needs, builds Amberline and installs and starts it (this PC) or says what to copy where (a rented server).

.DESCRIPTION
Batch 637 (one folder): step 3 asks where Amberline goes (C:\Amberline unless the user chooses another; an existing install is
offered for an update) and whether to delete the original game and AIR client folders once Amberline has its own copy. The release's
first start does the copying (HostInstall in the Platform, with the player installer's move code), from the arguments this guide
passes to its Start Amberline.cmd; the guide only asks and shows. For a rented server step 3 asks where the server package goes.
Batch 633. START-HERE.cmd at the repository root runs this file in the built-in Windows PowerShell 5.1, because PowerShell 7 may
not be installed yet, so it is written for 5.1. The build needs PowerShell 7: it runs through pwsh once that is installed.
A missing tool is installed with winget after a yes; on a no, or without winget, the guide shows the tool's download page and
waits. After each install the guide reads PATH again from the registry (for this window only), so no restart is needed.
The answers are kept in the state folder (answers.json), so the next run offers them again, and every run writes a log there.
Version rule for a build made here: 0.0.<minutes since 1 January 2026, UTC>. Each build counts as newer than the one before,
so the home release's self-install takes it; every published release (0.1.0 or later) counts as newer than any build made here.
Batch 635 (the first player's run): before it asks for the folders, the guide searches this PC for League of Legends.exe (its
SHA-256 against the pin in native/exehost/exehost.c) and the AIR client (LolClient.swf at the stock size, and LolClient.exe), the
usual places first, then every fixed drive, and lists what it found. A picked parent folder resolves to the copy inside it. The
build's details stay in the log. Everything the guide starts runs without the .NET banner or telemetry and in English, and the
PowerShell 7 child returns plain text only. The log is UTF-8 and never holds this PC's name or the Windows account's name.
Batch 645 (polish): plain words a screen reader reads well. No colour carries meaning alone (Done:, Problem:, Try again:,
Warning:), there are no text bars or progress art (the build's stages are "Part n of 7" lines, with a "Still building" line each
minute), lines wrap at 78 columns, paths stand on their own lines, and every question is answered by typing. The search has a
time limit (-SearchSeconds) and S skips it; the folders chosen last time are searched first. On a PC with an install the guide
names its version and the one this download builds, and offers an update (which also repairs it), never a second install. The
run ends with a summary.
Batch 653 (the owner's three adjustments, 3 November 2026): fewer words, one set of console answers and a line before every wait.
A yes or no is always "Type Y for yes or N for no.", a pause "Press Enter to continue.", the end "Press Enter to close."
(START-HERE.cmd). Other questions say what to type ("Type a number"); the intro says once that Enter sends an answer.
Batch 682 (owner, 4 November 2026: one guide for both): the release zip carries START-HERE.cmd and this file too (tools\guide.ps1
beside its built app\ folder, whose BUILD.json names the version and the game executable's SHA-256). There the guide skips the
tools and the build (steps 4 and 5, in one line) and its step 6 starts the zip's own Start Amberline.cmd; every other step,
question and word is the repository's. A rented server from the zip is told to unzip it on that server, or to build its package
from the source code. Batch 682b: on a PC with an install the update question also takes R, a rented server package instead (an install not
older than this build is asked "Build a package for a rented server instead?"). From the repository R goes on with the
rented path; from the zip it says that the package is built from the source code. Start Amberline.cmd started without options opens this guide, unless AMBERLINE_GUIDE is set (step 6 sets it).
On a PC with an Amberline (both ways) the guide asks neither step 1 nor step 2: it offers the update at once, and the install keeps
its own game and AIR client (the build reads them, or the folders the install's settings name). A release zip of the installed
version starts that Amberline instead.
Batch 765 (NOT RETAIL; owner, 13 November 2026): in the release zip, the game question also takes the client zip
(patch-1.0.0.126-client-bundle.zip), pasted or dragged into the window; the AIR question is then skipped and step 6 passes
--client-zip instead of --game and --air (the release's first start unpacks it). A build from the source code reads the two folders,
so there the zip is refused with a sentence that says so.

.PARAMETER DryRun
DIAGNOSTICS: prints the plan (what would be installed, built and started) and does none of it. No winget, no build, no start.

.PARAMETER Answers
DIAGNOSTICS: answers given in place of the questions, as JSON text or the path of a JSON file: where (1 or home, 2 or rented),
gameRoot (a folder, or in the release zip the client zip), airRoot, host, serverName, system (1 or windows, 2 or linux), useSaved and install (Y or N), installFolder,
updateInstall and deleteOriginals (Y or N), packageFolder (a rented server's package). With -DryRun also
missing: the tools to treat as missing (dotnet, pwsh, zig); without it a dry run looks for the tools. gameChoice and airChoice
answer the numbered list of the search's finds.

.PARAMETER StateFolder
Where the answers and the logs are kept. Default: .artifacts\start-here in the folder with START-HERE.cmd (Git ignores it).

.PARAMETER SearchRoots
DIAGNOSTICS: the folders the search walks in place of the usual places and every fixed drive (the checks give temporary trees);
the folders chosen last time still come first. A dry run without it does not search.

.PARAMETER SearchFolderLimit
At most this many folders are looked at in one search. Default 60000.

.PARAMETER SearchSeconds
A search stops after this many seconds. Default 60. S skips it at any time.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools\guide.ps1 -DryRun -Answers answers.json
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [string]$Answers,
    [string]$StateFolder,
    [string[]]$SearchRoots,
    [ValidateRange(1, 1000000)][int]$SearchFolderLimit = 60000,
    [ValidateRange(0, 3600)][int]$SearchSeconds = 60
)
$ErrorActionPreference = 'Stop'
# For everything this guide starts: no .NET welcome banner or telemetry, and .NET's messages in English whatever the system language.
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$repository = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repository
if (-not $StateFolder) { $StateFolder = Join-Path $repository '.artifacts\start-here' }
New-Item -ItemType Directory -Force -Path $StateFolder | Out-Null
$StateFolder = (Resolve-Path -LiteralPath $StateFolder).Path
$log = Join-Path $StateFolder ('guide-{0}-{1}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID)
$savedFile = Join-Path $StateFolder 'answers.json'
$given = $null
if ($Answers) {
    $text = if ($Answers.TrimStart().StartsWith('{')) { $Answers } else { Get-Content -LiteralPath $Answers -Raw }
    $given = $text | ConvertFrom-Json
}

$utf8 = New-Object Text.UTF8Encoding $false
# The log may be attached to a public issue, so it never holds the account's name, its profile folder's name or the PC's name
# (a tool's output can carry them). Whole words only, so a short name does not cut into other words.
$privateNames = @($env:USERNAME, $env:COMPUTERNAME, $(if ($env:USERPROFILE) { Split-Path -Leaf $env:USERPROFILE }) | Where-Object { $_ })
$privatePattern = if ($privateNames.Count) { [regex]('(?i)(?<![A-Za-z0-9])(?:' + (($privateNames | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')(?![A-Za-z0-9])') }
function Write-Log([string]$Text) {
    if ($privatePattern) { $Text = $privatePattern.Replace($Text, '<hidden>') }
    [IO.File]::AppendAllText($log, $Text + "`r`n", $utf8)
}
# Batch 645: lines wrap at spaces within $width columns (a console is 80 or more), so nothing is cut at the window's edge. A
# wrapped line goes on two columns further in, a numbered item under its own words.
$width = 78
function Format-Wrapped([string]$Text) {
    $spaces = $Text.Length - $Text.TrimStart(' ').Length
    $number = [regex]::Match($Text, '^ *\d+\. ')
    $hang = if ($number.Success) { $number.Length } elseif ($spaces) { $spaces + 2 } else { 0 }
    $line = ' ' * $spaces
    foreach ($word in ($Text.Trim() -split ' +')) {
        if (-not $line.Trim()) { $line += $word }
        elseif ($line.Length + 1 + $word.Length -le $width) { $line += ' ' + $word }
        else { $line; $line = (' ' * $hang) + $word }
    }
    $line
}
# Colour only repeats what the words say ("Done:", "Problem:", "Try again:", "Warning:"). -Whole keeps a path or command on one line.
function Say([string]$Text = '', [string]$Color = '', [switch]$Whole) {
    foreach ($line in $(if ($Whole -or $Text.Length -le $width) { $Text } else { Format-Wrapped $Text })) {
        if ($Color) { Write-Host $line -ForegroundColor $Color } else { Write-Host $line }
    }
    Write-Log $Text
}
function SayPath([string]$Path) { Say "    $Path" -Whole }
function Step([int]$Number, [string]$Title) { Say; Say "Step $Number of 6: $Title" 'Cyan' }
function Stop-Guide([string]$What, [string]$Todo) {
    Say
    Say 'Something went wrong.' 'Red'
    Say "What happened: $What" 'Red'
    Say "What to do: $Todo"
    Say 'The full log is here:'
    SayPath $log
    exit 1
}
# The last words of every run that ends well: the folder it is about, what was done, how to open it and what comes next.
function Finish([string[]]$Lines, [string]$Label, [string]$Folder) {
    Say
    Say 'Summary' 'Cyan'
    if ($Folder) { Say "  ${Label}:"; SayPath $Folder }
    foreach ($line in $Lines) { Say "  $line" }
    Say '  Log:'
    SayPath $log
    if ($DryRun) { Say; Say 'DRY RUN finished: nothing was installed, built or started.' 'Green' }
    exit 0
}
function Test-Given([string]$Key) { $null -ne $given -and $null -ne $given.$Key }

# The word list's console answers (batch 653): every yes or no, every pause and the window's end say exactly these.
$yesNo = 'Type Y for yes or N for no.'
# Batch 682b (owner-approved): the update question's third answer, a rented server's package instead.
$updateOrRented = 'Type Y to update, N to keep it, or R to build a rented server package.'
$goOn = 'Press Enter to continue.'
# A prompt, then the line typed. A sentence of the word list ends with its full stop; any other prompt gets a colon.
function Read-Line([string]$Prompt) {
    $shown = if ($Prompt.EndsWith('.')) { "$Prompt " } else { "${Prompt}: " }
    Write-Host $shown -NoNewline
    $value = Read-Host
    Write-Log "$shown$value"
    $value
}
# One answer: the injected one (shown as if typed), else typed in this window.
function Read-Answer([string]$Key, [string]$Prompt) {
    if (Test-Given $Key) {
        $value = [string]$given.$Key
        Say $(if ($Prompt.EndsWith('.')) { "$Prompt $value" } else { "${Prompt}: $value" }) -Whole
        return $value
    }
    if ($DryRun) { throw "The dry run has no answer for '$Key'." }
    Read-Line $Prompt
}
# Asks until $Test returns a value (it returns $null for an answer it refuses). An empty $Problem: $Test said it already.
function Read-Valid([string]$Key, [string]$Prompt, [scriptblock]$Test, [string]$Problem) {
    while ($true) {
        $value = & $Test (Read-Answer $Key $Prompt)
        if ($null -ne $value) { return $value }
        if (Test-Given $Key) { throw "The answer given for '$Key' is not valid. $Problem" }
        if ($Problem) { Say "Try again: $Problem" 'Yellow' }
    }
}
# The question on its own line, then the same prompt for every yes or no.
function Read-YesNo([string]$Key, [string]$Question) {
    Say $Question
    $answer = Read-Valid $Key $yesNo {
        param($text)
        switch -Regex ($text.Trim()) { '^(?i)y(es)?$' { 'Y' } '^(?i)no?$' { 'N' } }
    } $yesNo
    $answer -eq 'Y'
}

function Select-Folder([string]$Title) {
    if ($DryRun) { return $null }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = $Title
        $dialog.ShowNewFolderButton = $false
        # A top-most owner keeps the folder window in front of this one.
        $owner = New-Object System.Windows.Forms.Form
        $owner.TopMost = $true
        try { if ($dialog.ShowDialog($owner) -eq 'OK') { return $dialog.SelectedPath } } finally { $owner.Dispose(); $dialog.Dispose() }
    } catch { Write-Log "Folder window: $($_.Exception.Message)" }
    $null
}
# Batch 765: the client zip's full path when $Text names an existing .zip file (pasted, or the file dragged into this window), else $null.
$clientZipName = 'patch-1.0.0.126-client-bundle.zip'
function Get-ClientZip([string]$Text) {
    $path = $Text.Trim().Trim('"')
    try { if ($path -match '(?i)\.zip$' -and (Test-Path -LiteralPath $path -PathType Leaf)) { return (Resolve-Path -LiteralPath $path).Path } } catch { }
    $null
}
# A folder pasted, or chosen in the window ($Kind: game or air); a parent folder resolves to the copy found inside it. Batch 765: for the
# game, in the release zip, also the client zip (returned as it is); a build from the source code reads the folders, so it says why not.
function Read-Folder([string]$Kind, [string]$Title) {
    $folder = Read-Valid "${Kind}Root" 'Press Enter to choose it in a window, or paste its path' {
        param($text)
        $path = $text.Trim().Trim('"')
        if (-not $path) { $path = Select-Folder $Title }
        try { if ($path -and (Test-Path -LiteralPath $path -PathType Container)) { return (Resolve-Path -LiteralPath $path).Path } } catch { }
        if ($Kind -eq 'game' -and $path -and (Get-ClientZip $path)) {
            if ($prebuilt) { return (Get-ClientZip $path) }
            Say 'Try again: building Amberline from the source code reads the game and AIR client folders, so it cannot take the client zip. Unzip it, then give its folders.' 'Yellow'
            return $null
        }
        Say 'Try again: that folder was not found.' 'Yellow'
        $null
    } ''
    if (Get-ClientZip $folder) { return $folder }
    Resolve-Picked $Kind $folder
}

# The search (batch 635): breadth first from each root, at most $searchDepth folders deep and $SearchFolderLimit folders in all.
# It leaves out junctions and links, hidden and system folders, Windows' own folders and build output folders.
$searchDepth = 8
$skipAttributes = [int]([IO.FileAttributes]'Hidden, System, ReparsePoint')
$skipFolders = '^(Windows|Microsoft|\$)|^(Common Files|Reference Assemblies|Internet Explorer|System Volume Information|ProgramData|node_modules|\.artifacts|\.git)$'
$searchFiles = 'League of Legends.exe', 'LolClient.swf', 'LolClient.exe'
$stockSwfSize = 6944733   # the stock size build-server.ps1 checks
# Batch 645: the version in an app folder's BUILD.json, x.y.z as HostVersion.Read takes it, or $null.
function Get-AppVersion([string]$App) {
    try { $text = [string](Get-Content -LiteralPath (Join-Path $App 'BUILD.json') -Raw | ConvertFrom-Json).version } catch { return $null }
    $parsed = $null
    if ($text -match '^\d+\.\d+\.\d+$' -and [version]::TryParse($text, [ref]$parsed)) { $parsed }
}
# Batch 682: the release zip's version, when this guide runs from an unzipped release (its built app\ beside START-HERE.cmd); else
# $null, in the repository.
$prebuilt = Get-AppVersion (Join-Path $repository 'app')
# build-server.ps1's pin: the stock executable's SHA-256, from the ExeSha256 table in native/exehost/exehost.c; the release zip has
# no source code, so its BUILD.json carries the same pin (build-server.ps1 writes it there).
$exePin = if ($prebuilt) { ([string](Get-Content -LiteralPath (Join-Path $repository 'app\BUILD.json') -Raw | ConvertFrom-Json).gameExecutableSha256).ToUpperInvariant() }
else {
    $pinTable = [regex]::Match((Get-Content -LiteralPath (Join-Path $repository 'native\exehost\exehost.c') -Raw), 'ExeSha256\[32\]\s*=\s*\{([^}]*)\}')
    (-join @([regex]::Matches($pinTable.Groups[1].Value, '0x([0-9a-fA-F]{2})') | ForEach-Object { $_.Groups[1].Value })).ToUpperInvariant()
}

function New-Search {
    [pscustomobject]@{ Folders = 0; Seen = (New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase))
        Games = (New-Object System.Collections.ArrayList); Airs = (New-Object System.Collections.ArrayList)
        Clock = [Diagnostics.Stopwatch]::StartNew(); Noted = 0; Stopped = $null }
}
# Batch 645: true once the search ends early, at its time limit ('time') or because S was pressed in this window ('skipped').
# Other keys pressed meanwhile are dropped, so a stray Enter does not answer the next question. Every 10 seconds a line says how
# far it got.
function Test-SearchEnd($Search) {
    if (-not $Search.Stopped) {
        $seconds = [int][Math]::Floor($Search.Clock.Elapsed.TotalSeconds)
        if ($seconds -ge $SearchSeconds) { $Search.Stopped = 'time' }
        try { while (-not [Console]::IsInputRedirected -and [Console]::KeyAvailable) { if ([Console]::ReadKey($true).Key -eq 'S') { $Search.Stopped = 'skipped' } } } catch { }
        if (-not $Search.Stopped -and $seconds -ge $Search.Noted + 10) {
            $Search.Noted = $seconds - $seconds % 10
            Say "  Still searching: $($Search.Folders) folders in $seconds seconds. Press S to skip."
        }
    }
    [bool]$Search.Stopped
}
function Test-Game($Exe) {
    # .NET's SHA-256, not Get-FileHash: Windows PowerShell started from PowerShell 7 inherits a module path it cannot load that from.
    $stream = $Exe.OpenRead()
    try { $hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)).Replace('-', '') } finally { $stream.Dispose() }
    $correct = $exePin -and $hash -eq $exePin
    [pscustomobject]@{ Folder = $Exe.DirectoryName; Usable = $correct; Note = $(if ($correct) { 'patch 1.0.0.126, correct' } else { 'another patch' }) }
}
function Test-Air([string]$Folder, $Files) {
    $swf = $Files['LolClient.swf']
    $problem = if (-not $swf -or $swf.Length -ne $stockSwfSize) { 'another version' } elseif (-not $Files['LolClient.exe']) { 'the AIR client executable is missing' }
    [pscustomobject]@{ Folder = $Folder; Usable = -not $problem; Note = $(if ($problem) { $problem } else { 'patch 1.0.0.126, correct' }) }
}
function Search-Folders($Search, [string[]]$Roots, [switch]$Quiet) {
    foreach ($root in $Roots) {
        if ($Search.Stopped -or $Search.Folders -ge $SearchFolderLimit) { break }
        if (-not $root -or -not [IO.Directory]::Exists($root)) { continue }
        if (-not $Quiet) { Say "  Searching $root" -Whole }
        $queue = New-Object System.Collections.Queue
        $queue.Enqueue(@((New-Object IO.DirectoryInfo $root), 0))
        while ($queue.Count -and $Search.Folders -lt $SearchFolderLimit) {
            if ($Search.Folders % 16 -eq 0 -and (Test-SearchEnd $Search)) { break }
            $folder, $level = $queue.Dequeue()
            if (-not $Search.Seen.Add($folder.FullName.TrimEnd('\'))) { continue }
            $Search.Folders++
            $files = @{}
            try {
                foreach ($entry in $folder.EnumerateFileSystemInfos()) {
                    if ($entry -is [IO.DirectoryInfo]) {
                        if ($level -lt $searchDepth -and ([int]$entry.Attributes -band $skipAttributes) -eq 0 -and $entry.Name -notmatch $skipFolders) { $queue.Enqueue(@($entry, ($level + 1))) }
                    } elseif ($searchFiles -contains $entry.Name) { $files[$entry.Name] = $entry }
                }
                if ($files['League of Legends.exe']) { [void]$Search.Games.Add((Test-Game $files['League of Legends.exe'])) }
                if ($files['LolClient.swf'] -or $files['LolClient.exe']) { [void]$Search.Airs.Add((Test-Air $folder.FullName $files)) }
            } catch { Write-Log "Search: $($folder.FullName): $($_.Exception.Message)" }
        }
    }
}
function Find-GameAndAir([string[]]$Remembered) {
    if ($DryRun -and -not $SearchRoots) { Say 'DRY RUN: would search this PC for the game and the AIR client.'; return $null }
    $search = New-Search
    Say "Searching this PC for them. This takes at most $SearchSeconds seconds. Press S to skip."
    # Batch 645: the folders chosen last time come first, so a search that finds them there needs no drive search.
    if ($SearchRoots) { Search-Folders $search (@($Remembered) + $SearchRoots) }
    else {
        # The usual places first: the folder around Amberline (the first player kept the game and the AIR client there), the
        # user's own folders and the usual game folders. Then every fixed drive, unless both were found already.
        $drives = @([IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady } | ForEach-Object { $_.RootDirectory.FullName })
        # Batch 637: an Amberline install's own copies come first.
        $usual = @((Get-Installed)) + @($Remembered) + @((Split-Path -Parent $repository), [Environment]::GetFolderPath('Desktop'), (Join-Path $env:USERPROFILE 'Downloads'), [Environment]::GetFolderPath('MyDocuments'))
        foreach ($programs in @(${env:ProgramFiles(x86)}, $env:ProgramFiles) | Where-Object { $_ }) { $usual += (Join-Path $programs 'League of Legends'), (Join-Path $programs 'Riot Games') }
        foreach ($drive in $drives) { $usual += (Join-Path $drive 'Riot Games'), (Join-Path $drive 'League of Legends'), (Join-Path $drive 'Games') }
        Search-Folders $search $usual
        if (-not (@($search.Games | Where-Object { $_.Usable }).Count -and @($search.Airs | Where-Object { $_.Usable }).Count)) { Search-Folders $search $drives }
    }
    if ($search.Stopped -eq 'skipped') { Say 'You skipped the search.' }
    elseif ($search.Stopped) { Say "The search stopped at its time limit of $SearchSeconds seconds." }
    Say ('Looked at {0} folder{1}.' -f $search.Folders, $(if ($search.Folders -ne 1) { 's' }))
    $search
}
# The finds for one folder: the usable ones numbered (as found: the usual places and the shallowest first), the others with why
# they can't be used, then "Choose another folder" and "Paste a path". Without a search, or without a usable find: as before.
function Select-Found([string]$Kind, [string]$Name, $Search, [string]$Title) {
    if (-not $Search) { return Read-Folder $Kind $Title }
    $finds = @(if ($Kind -eq 'game') { $Search.Games } else { $Search.Airs })
    $usable = @($finds | Where-Object { $_.Usable })
    foreach ($other in @($finds | Where-Object { -not $_.Usable })) { Say "  Can't be used, $($other.Note): $($other.Folder)" -Whole }
    if (-not $usable.Count) {
        Say 'None found. Choose it yourself.'
        return Read-Folder $Kind $Title
    }
    Say "The search found these. Choose one:"
    for ($i = 0; $i -lt $usable.Count; $i++) { Say ('  {0}. {1} ({2})' -f ($i + 1), $usable[$i].Folder, $usable[$i].Note) -Whole }
    $last = $usable.Count + 2
    Say ('  {0}. Choose another folder in a window' -f ($last - 1))
    Say ('  {0}. Paste a path' -f $last)
    $pick = Read-Valid "${Kind}Choice" 'Type a number' {
        param($text)
        $number = 0
        if ([int]::TryParse($text.Trim(), [ref]$number) -and $number -ge 1 -and $number -le $last) { $number }
        # Batch 765: the client zip pasted or dragged in here too (the release zip's game question).
        elseif ($Kind -eq 'game' -and $prebuilt -and (Get-ClientZip $text)) { Get-ClientZip $text }
    } "type a number from 1 to $last."
    if ($pick -is [string]) { return $pick }
    if ($pick -le $usable.Count) { return $usable[$pick - 1].Folder }
    if ($pick -eq $last - 1) { $picked = Select-Folder $Title; if ($picked) { return Resolve-Picked $Kind $picked } }
    Read-Folder $Kind $Title
}
# A picked folder that does not hold the file itself: the first usable copy a search finds inside it (a parent folder, say).
# Nothing usable inside: the folder as picked, and build-server.ps1 says what is wrong with it.
function Resolve-Picked([string]$Kind, [string]$Folder) {
    $file = if ($Kind -eq 'game') { 'League of Legends.exe' } else { 'LolClient.swf' }
    if (Test-Path -LiteralPath (Join-Path $Folder $file) -PathType Leaf) { return $Folder }
    Say "Looking inside that folder (at most $SearchSeconds seconds)."
    $inside = New-Search
    Search-Folders $inside @($Folder) -Quiet
    $found = @(@(if ($Kind -eq 'game') { $inside.Games } else { $inside.Airs }) | Where-Object { $_.Usable })
    if ($found.Count) { Say 'Found it inside that folder:'; SayPath $found[0].Folder; return $found[0].Folder }
    $Folder
}

# PATH as Windows now has it (machine, then user), plus the folders this guide found tools in. This window only.
$foundFolders = New-Object System.Collections.Generic.List[string]
function Update-Path {
    $parts = @([Environment]::GetEnvironmentVariable('Path', 'Machine'), [Environment]::GetEnvironmentVariable('Path', 'User')) + $foundFolders
    $env:Path = ($parts | Where-Object { $_ }) -join ';'
}
function Add-ToPath([string]$Folder) {
    if (($env:Path -split ';') -notcontains $Folder) { $foundFolders.Add($Folder); $env:Path = "$Folder;$env:Path" }
}
function Find-Program([string]$Name, [string]$Fallback) {
    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Path }
    if ($Fallback -and (Test-Path -LiteralPath $Fallback -PathType Leaf)) { return $Fallback }
    $null
}
# Runs a program for a yes or no (exit code 0); its output goes to the log only.
function Test-Run([string]$Program) {
    $ErrorActionPreference = 'Continue'
    try { $output = @(& $Program @args 2>&1 | ForEach-Object { "$_" }) } catch { Write-Log "${Program}: $($_.Exception.Message)"; return $false }
    Write-Log "$Program $args (exit code $LASTEXITCODE): $($output -join ' | ')"
    $LASTEXITCODE -eq 0
}
function Find-Pwsh {
    $path = Find-Program 'pwsh' (Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe')
    if (-not $path) { return $null }
    $ErrorActionPreference = 'Continue'
    $text = (& $path -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion.ToString()' 2>$null | Select-Object -First 1)
    $version = $null
    if ([version]::TryParse(([string]$text -replace '-.*$', ''), [ref]$version) -and $version -ge [version]'7.5') { $path }
}

# The release zip builds nothing and has no global.json (batch 682), so its tools are never looked for.
$sdk = if ($prebuilt) { '' } else { (Get-Content -LiteralPath (Join-Path $repository 'global.json') -Raw | ConvertFrom-Json).sdk.version }
$sdkMajor = $sdk.Split('.')[0]
# Each tool: what it is, why the build needs it, how it is found, its winget package and its download page.
$tools = @(
    @{ Id = 'dotnet'; Name = ".NET $sdkMajor SDK"; Why = 'it builds the programs'; Path = $true
        # global.json asks for this SDK or a later patch of it; winget's newest .NET SDK can be of a later band, so ask for this one.
        Package = "Microsoft.DotNet.SDK.$sdkMajor"; Extra = @('--version', $sdk); Url = "https://dotnet.microsoft.com/download/dotnet/$sdkMajor.0"
        Find = { $p = Find-Program 'dotnet' (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'); if ($p -and (Test-Run $p '--version')) { $p } } },
    @{ Id = 'pwsh'; Name = 'PowerShell 7'; Why = 'version 7.5 or later, for the build scripts'; Path = $true
        Package = 'Microsoft.PowerShell'; Extra = @(); Url = 'https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows'
        Find = { Find-Pwsh } },
    # Batch 634: Zig builds the natives for Windows and Linux alike, so neither Git, LLVM nor the C++ Build Tools is needed.
    @{ Id = 'zig'; Name = 'Zig'; Why = 'it builds two small native parts'; Path = $true
        Package = 'zig.zig'; Extra = @(); Url = 'https://ziglang.org/download/'
        Find = { Find-Program 'zig' (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\zig.exe') } }
)
$haveWinget = $DryRun -or [bool](Get-Command winget -ErrorAction SilentlyContinue)

function Find-Tool($Tool) {
    if ($DryRun -and (Test-Given 'missing')) { if (@($given.missing) -contains $Tool.Id) { return $null } else { return '(dry run: found)' } }
    $path = & $Tool.Find
    if ($path -and $Tool.Path) { Add-ToPath (Split-Path -Parent $path) }
    $path
}
function Confirm-Tool($Tool) {
    $path = Find-Tool $Tool
    if ($path) { Say "  $($Tool.Name): found."; return $path }
    Say
    Say "Missing: $($Tool.Name) ($($Tool.Why))." 'Yellow'
    while ($true) {
        if ($haveWinget -and (Read-YesNo 'install' "Install $($Tool.Name) now? This accepts its license.")) {
            $arguments = @('install', '--id', $Tool.Package, '--exact') + $Tool.Extra + @('--source', 'winget', '--accept-package-agreements', '--accept-source-agreements')
            $shown = ($arguments | ForEach-Object { if ($_ -match ' ') { "`"$_`"" } else { $_ } }) -join ' '
            if ($DryRun) { Say "DRY RUN: would install it with: winget $shown" -Whole; return '(dry run: installed)' }
            Say "Installing $($Tool.Name). This takes a few minutes. If Windows asks, choose Yes."
            Write-Log "winget $shown"
            $ErrorActionPreference = 'Continue'
            & winget @arguments
            Write-Log "winget exit code: $LASTEXITCODE"
            $ErrorActionPreference = 'Stop'
        } else {
            Say "Install $($Tool.Name) from its download page, then come back here:"
            SayPath $Tool.Url
            if ($DryRun) { Say 'DRY RUN: would wait here until it is installed.'; return '(dry run: installed by hand)' }
            Read-Line $goOn | Out-Null
        }
        Update-Path
        $path = Find-Tool $Tool
        if ($path) { Say "Done: $($Tool.Name) is ready." 'Green'; return $path }
        Say "Problem: $($Tool.Name) is still not found. If its installer asked for a restart, restart Windows, then double-click START-HERE.cmd again." 'Yellow'
    }
}

function Format-Quoted([string]$Text) { "'" + $Text.Replace("'", "''") + "'" }
function Format-Minutes([TimeSpan]$Time) {
    $minutes = [int][Math]::Floor($Time.TotalMinutes)
    if ($minutes -lt 1) { 'less than a minute' } elseif ($minutes -eq 1) { '1 minute' } else { "$minutes minutes" }
}
# build-server.ps1 in PowerShell 7. Each of its stage lines becomes "Part n of 7: ...", a line each minute says it is still
# working, and the rest goes to the log only; its refusal comes back as one plain sentence.
function Invoke-Build($Parameters, [string]$Pwsh) {
    $call = (Format-Quoted (Join-Path $repository 'tools\build-server.ps1')) + ' ' +
        (($Parameters.GetEnumerator() | ForEach-Object { "-$($_.Key) $(Format-Quoted ([string]$_.Value))" }) -join ' ')
    if ($DryRun) { Say "DRY RUN: would build with: pwsh -NoProfile -ExecutionPolicy Bypass -Command & $call" -Whole; return }
    Say 'This takes a few minutes, longer the first time.'
    Write-Log "pwsh: & $call"
    # Plain text only (batch 635): with -EncodedCommand and its error output redirected, pwsh writes CLIXML unless -OutputFormat
    # is given, and CLIXML carries the PC's and the account's name. Every stream comes back as text (*>&1), in UTF-8.
    $command = "`$ErrorActionPreference = 'Stop'; `$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [Text.UTF8Encoding]::new(`$false); " +
        "try { & $call *>&1 | ForEach-Object { [string]`$_ }; exit 0 } catch { [Console]::Out.WriteLine('GUIDE-ERROR: ' + `$_.Exception.Message); exit 1 }"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $result = @{ Error = $null; Minutes = 0 }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $windowEncoding = [Console]::OutputEncoding
    $ErrorActionPreference = 'Continue'
    try {
        try { [Console]::OutputEncoding = $utf8 } catch { Write-Log "UTF-8 output: $($_.Exception.Message)" }
        & $Pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand $encoded 2>&1 | ForEach-Object {
            $line = "$_"
            Write-Log $line
            if ($line -match '^AMBERLINE-STAGE (\d+)/(\d+) (.+)$') { Write-Host "  Part $($Matches[1]) of $($Matches[2]): $($Matches[3])" }
            elseif ($line.StartsWith('GUIDE-ERROR: ')) { $result.Error = $line.Substring(13) }
            elseif ([int][Math]::Floor($clock.Elapsed.TotalMinutes) -gt $result.Minutes) {
                $result.Minutes = [int][Math]::Floor($clock.Elapsed.TotalMinutes)
                Write-Host "  Still building: $(Format-Minutes $clock.Elapsed) so far."
            }
        }
        $code = $LASTEXITCODE
    } finally {
        try { [Console]::OutputEncoding = $windowEncoding } catch { }
        $ErrorActionPreference = 'Stop'
    }
    if ($code -ne 0) {
        $what = if ($result.Error) { "The build stopped with this message: $($result.Error)" } else { "The build stopped (exit code $code)." }
        Stop-Guide $what ('If it is about your game or AIR client folder, double-click START-HERE.cmd again and answer N to ' +
            '"Use these answers again?". Otherwise, read the log.')
    }
    Say "Done: the build is finished, after $(Format-Minutes $clock.Elapsed)." 'Green'
}

function Show-Choice($Choice) {
    if ($Choice.where -eq 'home') { Say '  Where: this PC' }
    else { Say "  Where: a rented $($Choice.system) server, address $($Choice.host), server name $($Choice.serverName)" }
    if ($Choice.clientZip) { Say "  Client zip: $($Choice.clientZip)" -Whole }
    else {
        Say "  Game folder: $($Choice.gameRoot)" -Whole
        Say "  AIR client folder: $($Choice.airRoot)" -Whole
    }
    if ($Choice.installFolder) { Say "  Install folder: $($Choice.installFolder)" -Whole }
    if ($Choice.deleteOriginals -eq 'Y') { Say '  The originals: deleted after copying' }
    if ($Choice.packageFolder) { Say "  Server package folder: $($Choice.packageFolder)" -Whole }
}
function Save-Choice {
    $choice | ConvertTo-Json | Set-Content -LiteralPath $savedFile -Encoding UTF8
    Write-Log "Answers kept in $savedFile"
}

# Batch 637, the one-folder install. The marker and the pointer are HostInstall's (FolderMove.InstallMarker, FolderMove.PointerPath).
$installMarker = 'amberline-install.json'
$defaultInstall = 'C:\Amberline'
# The install folder %LOCALAPPDATA%\Amberline\install.json names, while it still holds the marker; else $null.
function Get-Installed {
    try {
        $folder = (Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'Amberline\install.json') -Raw | ConvertFrom-Json).folder
        if ($folder -and (Test-Path -LiteralPath (Join-Path $folder $installMarker) -PathType Leaf)) { return [string]$folder }
    } catch { }
    $null
}
function Test-Inside([string]$Path, [string]$Folder) {
    $Folder -and ($Path.TrimEnd('\') + '\').StartsWith($Folder.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
# Batch 645, as HostInstall does it (ClientPlan.Make): a client the install folder has its own copy of is used from there, and the
# picked folder stays as it is. Returns the names of the clients it took from the install.
function Use-OwnCopies($Choice) {
    if (-not $Choice.installFolder) { return }
    foreach ($pair in @(@('gameRoot', 'game\projects\lol_game_client\releases\0.0.0.51', 'game'), @('airRoot', 'air', 'AIR client'))) {
        $own = Join-Path $Choice.installFolder $pair[1]
        if (-not (Test-Inside $Choice.($pair[0]) $Choice.installFolder) -and (Test-Path -LiteralPath $own -PathType Container)) {
            $Choice.($pair[0]) = $own
            $pair[2]
        }
    }
}
# Why a folder cannot be the install folder (or, with -Package, a rented server's package folder), or $null. HostInstall.FolderProblem
# in the Platform checks the install folder again before it copies anything.
function Test-TargetFolder([string]$Folder, [switch]$Package) {
    $other = ' Choose another folder.'
    if ($Folder -notmatch '^[A-Za-z]:\\') { return "Type the full path, for example $defaultInstall." }
    try { $full = [IO.Path]::GetFullPath($Folder).TrimEnd('\') } catch { return 'Windows cannot use that folder path.' + $other }
    if ($full.Length -le 2) { return 'A whole drive cannot be the folder.' + $other }
    if (@($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432) | Where-Object { Test-Inside $full $_ }) {
        return 'Program Files needs administrator rights.' + $other
    }
    if (@($env:OneDrive, $env:OneDriveConsumer, $env:OneDriveCommercial) | Where-Object { Test-Inside $full $_ }) { $synced = $true }
    if ($synced -or @($full.Split('\') | Where-Object { $_ -eq 'OneDrive' -or $_ -like 'OneDrive - *' }).Count) {
        return 'OneDrive syncs that folder.' + $other
    }
    if (Test-Inside $full $repository) { return 'That folder is inside the Amberline download (the folder with START-HERE.cmd).' + $other }
    foreach ($pair in @(@('game', $choice.gameRoot), @('AIR client', $choice.airRoot))) {
        if (Test-Inside $full $pair[1]) { return "That folder is inside your $($pair[0]) folder." + $other }
    }
    if ((Test-Path -LiteralPath $full -PathType Container) -and @(Get-ChildItem -LiteralPath $full -Force).Count) {
        # A rebuild into a package folder moves the old one aside (build-server.ps1), so only a package folder may be built into again.
        $own = if ($Package) { 'README-DEPLOY.txt' } else { $installMarker }
        if (-not (Test-Path -LiteralPath (Join-Path $full $own) -PathType Leaf)) {
            return 'That folder holds other files. Choose a new or empty folder.'
        }
    }
    $null
}
# The question on its own line, then the prompt that says what Enter does.
function Read-TargetFolder([string]$Key, [string]$Question, [string]$Default, [switch]$Package) {
    Say $Question
    Read-Valid $Key "Type a folder, or press Enter for $Default" {
        param($text)
        $path = $text.Trim().Trim('"')
        if (-not $path) { $path = $Default }
        $problem = Test-TargetFolder $path -Package:$Package
        if ($problem) { Say "Problem: $problem" 'Yellow' } else { [IO.Path]::GetFullPath($path).TrimEnd('\') }
    } ''
}
# The bytes under a folder (or a file), for the "how much is copied" line.
function Get-Bytes([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return (Get-Item -LiteralPath $Path).Length }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return 0 }
    $sum = 0L
    foreach ($file in [IO.Directory]::EnumerateFiles($Path, '*', 'AllDirectories')) { $sum += (New-Object IO.FileInfo $file).Length }
    $sum
}
# The AIR client's own files, as HostInstall takes them (FolderMove.AirEntries); anything else in its folder stays.
$airEntries = 'Adobe AIR', 'META-INF', 'assets', 'css', 'logs', 'LolClient.exe', 'LolClient.swf', 'mimetype', 'mod_lgn.dat', 'mod_man.dat',
    'mod_ser.dat', 'version.txt', 'lol.properties', 'locale.properties', 'lol.properties.stock', 'locale.properties.stock'
function Format-Gigabytes([long]$Bytes) { ($Bytes / 1e9).ToString('0.0', [Globalization.CultureInfo]::InvariantCulture) + ' GB' }
$openIt = 'To open Amberline: double-click its desktop icon.'
# Batch 682: starts a Start Amberline.cmd in a new window, after unpacking $Zip into $Folder when one is given; a dry run only
# says so. Started without options that .cmd opens this guide, unless AMBERLINE_GUIDE is set, so this sets it.
function Start-Amberline([string]$Folder, [string[]]$Options, [string]$Zip) {
    $starter = Join-Path $Folder 'Start Amberline.cmd'
    $line = (@($Options) | ForEach-Object { if ($_ -like '--*') { $_ } else { '"' + $_.TrimEnd('\') + '"' } }) -join ' '
    $unpack = if ($Zip) { "unpack $Zip into $Folder and " }
    if ($DryRun) { Say "DRY RUN: would ${unpack}start $starter $line in a new window." -Whole; return }
    if ($Zip) {
        Say 'Unpacking Amberline and starting it in a new window. This takes a few seconds.'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($Zip, $Folder)
    } else { Say 'Starting Amberline in a new window. This takes a few seconds.' }
    Write-Log "Starting $starter $line"
    $env:AMBERLINE_GUIDE = '1'
    if ($line) { Start-Process -FilePath $starter -WorkingDirectory $Folder -ArgumentList $line }
    else { Start-Process -FilePath $starter -WorkingDirectory $Folder }
}

try {
    Write-Log "Amberline setup guide, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), Windows PowerShell $($PSVersionTable.PSVersion)$(if ($DryRun) { ', dry run' })"
    Say 'Amberline setup' 'Cyan'
    Say '6 steps. Type each answer, then press Enter. To stop, close this window.'
    if ($DryRun) { Say 'DRY RUN: this run only shows what it would do. Nothing is installed, built or started.' 'Yellow' }
    # This download's build number (the version rule above), the same for the whole run; the release zip's own version.
    $version = if ($prebuilt) { [string]$prebuilt } else { '0.0.{0}' -f [int][Math]::Floor(([DateTime]::UtcNow - [DateTime]::new(2026, 1, 1)).TotalMinutes) }

    # Batch 645: the Amberline on this PC (the pointer's install, or one from before batch 637 in the user folder) and its version.
    $installed = Get-Installed
    $app = if ($installed) { Join-Path $installed 'app' } else { Join-Path $env:LOCALAPPDATA 'Amberline\app' }
    $legacy = -not $installed -and (Test-Path -LiteralPath (Join-Path $app 'BUILD.json') -PathType Leaf)
    $update = [bool]($installed -or $legacy)
    $have = if ($update) { Get-AppVersion $app }
    $later = @('From now on: start Amberline from its desktop icon. Keep its window open while players play.',
        $(if ($prebuilt) { 'To update: choose Update now on its page, or unzip the newest release and double-click its START-HERE.cmd.' }
        else { 'To update: get the newest code and double-click START-HERE.cmd again.' }))

    # The answers of the last run, offered again; declined, their folders are still searched first. An update needs none.
    $choice = $null
    $last = $null
    if (-not $update -and (Test-Path -LiteralPath $savedFile)) {
        try { $last = Get-Content -LiteralPath $savedFile -Raw | ConvertFrom-Json } catch { $last = $null }
        if ($last -and $last.where -and (($last.gameRoot -and $last.airRoot) -or ($prebuilt -and $last.clientZip))) {
            foreach ($name in 'installFolder', 'deleteOriginals', 'packageFolder', 'clientZip') {
                if ($last.PSObject.Properties.Name -notcontains $name) { $last | Add-Member -NotePropertyName $name -NotePropertyValue $null }
            }
            # Batch 637: after the last install the install's own copies take the place of the folders picked then.
            Use-OwnCopies $last | Out-Null
            Say
            Say 'Last time you chose:'
            Show-Choice $last
            if (Read-YesNo 'useSaved' 'Use these answers again?') { $choice = $last }
        } else { $last = $null }
    }
    $reused = [bool]$choice
    $remembered = @(if ($last) { $last.gameRoot; $last.airRoot })

    # Step 2's questions, into $Target's gameRoot and airRoot.
    function Read-Clients($Target, [string]$Where) {
        Step 2 'Your game and AIR client folders'
        if ($Where -eq 'rented') { Say 'Choose them on this PC. You copy them to your server later.' }
        $search = Find-GameAndAir $remembered
        Say
        Say 'Game folder (patch 1.0.0.126, with DATA and LEVELS):'
        # Batch 765: in the release zip, the client zip answers for both folders.
        if ($prebuilt) { Say "Or the client zip ($clientZipName) for both: paste its path, or drag the zip into this window." }
        $Target.gameRoot = Select-Found 'game' 'game folder' $search 'Choose the game folder (it holds DATA and LEVELS)'
        if ($prebuilt -and (Get-ClientZip $Target.gameRoot)) {
            $Target.clientZip = $Target.gameRoot
            $Target.gameRoot = $null
            Say 'Your client zip:'
            SayPath $Target.clientZip
            Say 'Amberline unpacks the game and the AIR client from it, so the AIR client folder is not asked.'
            return
        }
        Say 'Your game folder:'
        SayPath $Target.gameRoot
        Say
        Say 'AIR client folder (with its assets folder):'
        $Target.airRoot = Select-Found 'air' 'AIR client folder' $search 'Choose the AIR client folder (it holds its .swf file and assets)'
        Say 'Your AIR client folder:'
        SayPath $Target.airRoot
        # Batch 635: the first player unzipped Amberline inside the folder that held the AIR client and the game; that works now.
        foreach ($pair in @(@('game', $Target.gameRoot), @('AIR client', $Target.airRoot))) {
            if (Test-Inside $repository $pair[1]) {
                Say "Amberline's own folder is inside your $($pair[0]) folder. That is fine."
            }
        }
    }

    # The folders the build reads (the choice's, except in an update below).
    $buildFrom = $null
    $where = $null
    if ($update) {
        # Batch 682 (the orchestrator's follow-up, 4 November 2026): an Amberline on this PC is updated in place, in both ways, so
        # where the server runs and the folders are not asked. The update brings in no folder: the install keeps its own copies
        # of the game and the AIR client, or the folders its settings name. Batch 682b: or, from the repository, a rented
        # server's package is built instead (step 1 answered "rented").
        # Batch 645: an update, which also repairs. HostInstall starts an equal version without copying it, and a build made here
        # always has a new number, so "rebuild the same version" is this update. A second install elsewhere is not offered: the
        # pointer, the desktop icon, the ports and the users' Play files belong to one install.
        $folder = Split-Path -Parent $app
        Say
        Say "Your Amberline $(if ($have) { $have } else { 'without a version number' }) is in:"
        SayPath $folder
        # HostInstall keeps an installed version that is equal or newer, so a build made here would change nothing: say so at
        # once. The release zip of the installed version starts it, as a start of that zip without the guide did.
        if ($have -and $have -ge [version]$version) {
            if ($prebuilt -and $have -eq [version]$version) {
                Say "Amberline $have is already installed. Starting it."
                Start-Amberline $repository @(if ($installed) { '--install-to', $installed })
                Finish (@('Now: a new window named Amberline starts it. Then its page opens in your web browser.') + $later) 'Install folder' $folder
            }
            $kept = @("Nothing was changed. Your Amberline $have, in $folder, is not older than this download's build $version.",
                'To update it, use the newest Amberline release zip.', $openIt)
            if ($prebuilt) { Finish $kept }
            Say "It is not older than this download's build $version, so this build would not replace it."
            if (-not (Read-YesNo 'rentedInstead' 'Build a package for a rented server instead?')) { Finish $kept }
            $where = 'rented'
        } else {
            Say "This download $(if ($prebuilt) { 'is' } else { 'builds' }) $version. Updating also repairs it; your settings, logins and users stay."
            if ($installed) { Say 'To install elsewhere, uninstall this one first (Uninstall Amberline.cmd in its folder).' }
            Say 'Update this Amberline now?'
            # Read-YesNo's answers, and R.
            $answer = Read-Valid 'updateInstall' $updateOrRented {
                param($text)
                switch -Regex ($text.Trim()) { '^(?i)y(es)?$' { 'Y' } '^(?i)no?$' { 'N' } '^(?i)r$' { 'R' } }
            } $updateOrRented
            if ($answer -eq 'N') { Finish @('Nothing was changed.', $openIt) }
            if ($answer -eq 'R') {
                if ($prebuilt) { Finish @("Nothing was changed. A rented server's package is built from the Amberline source code: download it from GitHub on this PC and double-click its START-HERE.cmd.") }
                $where = 'rented'
            }
        }
        # The rented path: steps 1 to 6 as without an install, with step 1 answered.
        if ($where) { $update = $false }
    }
    if ($update) {
        Say
        Say 'Steps 1 and 2 of 6: not needed. This PC has Amberline already.' 'Cyan'
        $choice = [pscustomobject]@{ where = 'home'; gameRoot = $null; airRoot = $null; host = $null; serverName = $null; system = $null
            installFolder = $installed; deleteOriginals = 'N'; packageFolder = $null; clientZip = $null }
        foreach ($name in @(Use-OwnCopies $choice)) { Say "Using the install folder's own copy of the $name." }
        if (-not $prebuilt -and -not ($choice.gameRoot -and $choice.airRoot)) {
            # The build reads both folders: the install's own copies, else the folders its settings name, else step 2 asks.
            $buildFrom = [pscustomobject]@{ gameRoot = $choice.gameRoot; airRoot = $choice.airRoot }
            try { $settings = Get-Content -LiteralPath (Join-Path $folder 'data\platform.json') -Raw | ConvertFrom-Json } catch { $settings = $null }
            foreach ($pair in @(@('gameRoot', 'dataRoot'), @('airRoot', 'airRoot'))) {
                $named = if ($settings) { [string]$settings.($pair[1]) }
                if (-not $buildFrom.($pair[0]) -and $named -and [IO.Path]::IsPathRooted($named) -and (Test-Path -LiteralPath $named -PathType Container)) {
                    $buildFrom.($pair[0]) = $named
                }
            }
            if (-not ($buildFrom.gameRoot -and $buildFrom.airRoot)) { Read-Clients $buildFrom 'home' }
        }
    } elseif (-not $choice) {
        Step 1 'Where will your server run?'
        if ($where) { Say 'On a rented server (from a hosting company), as you chose.' }
        else {
            Say '  1. On this PC'
            Say '  2. On a rented server (from a hosting company)'
            $where = Read-Valid 'where' 'Type 1 or 2' {
                param($text)
                switch -Regex ($text.Trim()) { '^(1|(?i)home)$' { 'home' } '^(2|(?i)rented)$' { 'rented' } }
            } 'type 1 or 2.'
        }
        # Batch 682: the release zip installs Amberline on the PC it runs on, and a server package is built from the source code.
        if ($where -eq 'rented' -and $prebuilt) {
            Finish @('Nothing was changed. This download installs Amberline on the PC you start it on.',
                'For a Windows server: copy this zip to the server, unzip it there, double-click START-HERE.cmd there and type 1.',
                'For a Linux server: download the Amberline source code from GitHub on this PC and double-click its START-HERE.cmd. It builds the server package.')
        }
        $choice = [pscustomobject]@{ where = $where; gameRoot = $null; airRoot = $null; host = $null; serverName = $null; system = $null
            installFolder = $null; deleteOriginals = $null; packageFolder = $null; clientZip = $null }
        if ($where -eq 'rented') {
            Say
            Say 'Your server''s address (a name or a number address, without https:// or a port).'
            # build-server.ps1's rule for -PublicHost.
            $choice.host = Read-Valid 'host' 'Type the address' {
                param($text)
                $h = $text.Trim()
                if ($h -match '^[A-Za-z0-9.-]{1,253}\z' -and [uri]::CheckHostName($h) -in 'Dns', 'IPv4') { $h }
            } 'type only the name or the number address, without https://, a port or a path.'
            Say
            # build-server.ps1's rule for -ServerName.
            $choice.serverName = Read-Valid 'serverName' 'Type a server name, or press Enter for Amberline' {
                param($text)
                $n = $text.Trim()
                if (-not $n) { $n = 'Amberline' }
                if ($n -cmatch '^[A-Za-z0-9](?:[A-Za-z0-9 ._-]{0,38}[A-Za-z0-9])?\z' -and $n -notmatch '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?\z') { $n }
            } 'use 1 to 40 letters, digits, spaces, dots, hyphens or underscores; start and end with a letter or digit.'
            Say
            Say 'Which system does your server run?'
            Say '  1. Windows'
            Say '  2. Linux'
            $choice.system = Read-Valid 'system' 'Type 1 or 2' {
                param($text)
                switch -Regex ($text.Trim()) { '^(1|(?i)windows)$' { 'Windows' } '^(2|(?i)linux)$' { 'Linux' } }
            } 'type 1 for Windows or 2 for Linux.'
        }

        Read-Clients $choice $where
        Save-Choice
    } else {
        Say
        Say 'Steps 1 and 2 of 6: your answers from last time.' 'Cyan'
    }
    if (-not $buildFrom) { $buildFrom = $choice }

    # Step 3 (batch 637): where Amberline goes, and whether the originals go once Amberline has its own copy.
    if ($choice.where -eq 'home') {
        Step 3 'Where Amberline goes'
        if ($update) {
            Say 'Amberline is updated where it is:'
            SayPath (Split-Path -Parent $app)
        } elseif ($reused -and $choice.installFolder) {
            Say 'The install folder from last time:'
            SayPath $choice.installFolder
        } else {
            $choice.installFolder = Read-TargetFolder 'installFolder' 'Where should Amberline be installed? It keeps everything in that folder.' $defaultInstall
        }
        if (-not $update -and -not $reused) {
            Say 'Install folder:'
            SayPath $choice.installFolder
            foreach ($name in @(Use-OwnCopies $choice)) { Say "Using the install folder's own copy of the $name." }
            $choice.deleteOriginals = 'N'
            $outside = @(@($choice.gameRoot, $choice.airRoot) | Where-Object { $_ -and -not (Test-Inside $_ $choice.installFolder) })
            if ($choice.clientZip -and -not ($choice.gameRoot -and $choice.airRoot)) { Say 'Amberline unpacks the game and the AIR client from the client zip into it. The zip stays as it is.' }
            if ($outside.Count) {
                $drive = [IO.Path]::GetPathRoot($choice.installFolder)
                $bytes = 0L
                if (-not (Test-Inside $choice.gameRoot $choice.installFolder)) { $bytes += Get-Bytes $choice.gameRoot }
                if (-not (Test-Inside $choice.airRoot $choice.installFolder)) { foreach ($entry in $airEntries) { $bytes += Get-Bytes (Join-Path $choice.airRoot $entry) } }
                $free = 0L
                try { $free = (New-Object IO.DriveInfo $drive).AvailableFreeSpace } catch { Write-Log "Free space: $($_.Exception.Message)" }
                Say
                Say 'Amberline copies these into the install folder:'
                foreach ($folder in $outside) { SayPath $folder }
                Say "They hold $(Format-Gigabytes $bytes). Drive $($drive.TrimEnd('\')) has $(Format-Gigabytes $free) free."
                if ($bytes -gt $free) { Say 'Warning: that may not be enough space. If the copy stops, choose a folder on another drive.' 'Yellow' }
                $kept = @(if (Test-Path -LiteralPath $choice.airRoot) { Get-ChildItem -LiteralPath $choice.airRoot -Force | Where-Object { $airEntries -notcontains $_.Name } | ForEach-Object { $_.Name } })
                if ($kept.Count) { Say "These stay in the AIR client folder: $($kept -join ', ')." }
                Say 'Amberline can then delete the originals (only the game''s and AIR client''s own files).'
                $choice.deleteOriginals = if (Read-YesNo 'deleteOriginals' 'Delete the originals?') { 'Y' } else { 'N' }
            }
        }
        if (-not $legacy) { Save-Choice }
    } else {
        Step 3 'Where the server package goes'
        if ($reused -and $choice.packageFolder) {
            Say 'The server package folder from last time:'
            SayPath $choice.packageFolder
        } else {
            Say 'You copy this folder to your server. Nothing is copied or deleted on this PC.'
            $choice.packageFolder = Read-TargetFolder 'packageFolder' 'Where should the server package go?' "$defaultInstall\server-package" -Package
            Save-Choice
        }
    }

    $runtime = if ($choice.where -eq 'rented' -and $choice.system -eq 'Linux') { 'linux-x64' } else { 'win-x64' }
    # Batch 682: the release zip is built already, so it needs neither the tools nor the build, and says so in one line.
    if ($prebuilt) { Say; Say "Steps 4 and 5 of 6: not needed. This download is Amberline $version, built already." 'Cyan' }
    else {
        Step 4 'Build tools'
        Say 'Looking for them. This takes a few seconds.'
        $found = @{}
        foreach ($tool in $tools) { $found[$tool.Id] = Confirm-Tool $tool }
        Say 'Done: all the tools are here.' 'Green'

        Step 5 'Building Amberline'
    }
    if ($choice.where -eq 'home') {
        # The Start Amberline.cmd that installs: the release zip's own, beside START-HERE.cmd, or that of the zip built here, once unpacked.
        $unpacked = $repository
        if (-not $prebuilt) {
            $output = Join-Path $StateFolder ('home-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
            Say "Building Amberline $version into:"
            SayPath $output
            Invoke-Build ([ordered]@{ Layout = 'Home'; Version = $version; GameRoot = $buildFrom.gameRoot; AirRoot = $buildFrom.airRoot; Output = $output }) $found['pwsh']
            $zip = Join-Path $output "Amberline-$version-win-x64.zip"
            $unpacked = Join-Path $output 'Amberline'
        }

        Step 6 'Installing and starting Amberline'
        # The release's first start installs (HostInstall): the folder, the game and AIR client to bring in, and the delete answer.
        $zipped = $choice.clientZip -and -not ($choice.gameRoot -and $choice.airRoot)
        $arguments = @(if ($choice.installFolder) {
            '--install-to', $choice.installFolder
            # Batch 765: the client zip instead of the two folders, never with them (unless the install has its own copies of both).
            if ($zipped) { '--client-zip', $choice.clientZip }
            else {
                if ($choice.gameRoot) { '--game', $choice.gameRoot }
                if ($choice.airRoot) { '--air', $choice.airRoot }
                if ($choice.deleteOriginals -eq 'Y') { '--delete-originals' }
            }
        })
        Start-Amberline $unpacked $arguments $zip
        $copies = @(@($choice.gameRoot, $choice.airRoot) | Where-Object { $_ -and $choice.installFolder -and -not (Test-Inside $_ $choice.installFolder) }).Count
        $folder = if ($choice.installFolder) { $choice.installFolder } else { Split-Path -Parent $app }
        $deletes = if ($choice.deleteOriginals -eq 'Y') { ' Once the copies are checked, it deletes the originals.' } else { '' }
        if ($update) {
            Finish (@(
                "Now: a new window named Amberline asks you to press Enter, then updates to $version$(if ($copies) { ' and copies the game and the AIR client' }). This ends any game in progress.$deletes",
                'Then: its page opens in your web browser.') + $later) 'Install folder' $folder
        }
        Finish (@(
            "Now: a new window named Amberline copies the program$(if ($copies) { ', the game and the AIR client' }) into that folder$(if ($zipped) { ' and unpacks the game and the AIR client from the client zip into it' }).$deletes Then its page opens in your web browser.",
            'On that page: set up your server, then choose Users and Add a user.') + $later) 'Install folder' $folder
    } else {
        $output = $choice.packageFolder
        Say "Building the server package for $($choice.host) ($($choice.system)) into:"
        SayPath $output
        Invoke-Build ([ordered]@{ Layout = 'Server'; Runtime = $runtime; PublicHost = $choice.host; ServerName = $choice.serverName
            GameRoot = $choice.gameRoot; AirRoot = $choice.airRoot; Output = $output }) $found['pwsh']

        Step 6 'Putting it on your server'
        $windows = $runtime -eq 'win-x64'
        Say 'Do these in order:'
        Say '  1. Copy this folder to your server:'
        SayPath " $output"
        Say '  2. Copy your game and AIR client folders to the server too.'
        # The server's own path style: a Linux server writes / between folders.
        $file, $program = if ($windows) { 'platform\platform-data\platform.json', 'server\Amberline.Server.exe' } else { 'platform/platform-data/platform.json', 'server/Amberline.Server' }
        Say "  3. On the server, edit $file. Replace each <...> with the server's game folder, server program ($program) and AIR client folder.$(if ($windows) { ' Write each \ as \\.' })$(if ([uri]::CheckHostName($choice.host) -eq 'Dns') { ' Put the server''s number address (four numbers with dots) in gameHost.' })"
        Say '  4. In your hosting company''s firewall, open TCP port 8391 and UDP ports 5219 to 5222.'
        if ($windows) { Say '  5. On the server, start platform\Amberline.Platform.exe and keep it running.' }
        else {
            Say '  5. On the server, in that folder, run these (keep the last one running):'
            SayPath ' chmod +x server/Amberline.Server server/exehost platform/Amberline.Platform'
            SayPath ' cd platform'
            SayPath ' ./Amberline.Platform'
        }
        Say '  6. Make each user a login on the management page (see README-DEPLOY.txt).'
        Say '  7. Give each user their login and this Play file:'
        SayPath (' ' + (Join-Path $output 'Play.cmd'))
        if ($DryRun) { Say "DRY RUN: would open $output in File Explorer." -Whole } else { Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$output`"" }
        Finish @('Built. File Explorer shows the folder now. Next: the checklist above.',
            'To rebuild: double-click START-HERE.cmd. The same folder keeps your users'' Play files working.') 'Server package folder' $output
    }
} catch {
    Stop-Guide $_.Exception.Message 'Double-click START-HERE.cmd to try again. If it happens again, open an issue on GitHub with the log.'
}
