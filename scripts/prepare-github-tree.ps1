<#
.SYNOPSIS
Creates a reviewable UTF-8 source snapshot for TsuIppaku/Singularity, without syncing it.
.DESCRIPTION
The JSON is a full eligible snapshot, not a set of remote deletion instructions.
Before a later plugin sync, compare the remote tree with this snapshot and the
previous managed-file list. Delete only previously managed files that disappeared
from the eligible snapshot. Preserve unfamiliar remote files, even inside the
allowed directories. This script never contacts GitHub or changes the Git index.
.PARAMETER ProjectRoot
Source project root; defaults to this script's parent directory.
.PARAMETER OutputPath
JSON destination, relative to ProjectRoot or absolute. Keep it outside the source
directories. Defaults to artifacts/github-tree.json.
#>
[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [string]$OutputPath = 'artifacts/github-tree.json'
)

$ErrorActionPreference = 'Stop'
if ([String]::IsNullOrWhiteSpace($ProjectRoot)) { $ProjectRoot = Split-Path -Parent $PSScriptRoot }
$root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($root -match '^[A-Za-z]:$') { $root += [IO.Path]::DirectorySeparatorChar }
$rootItem = Get-Item -LiteralPath $root -Force
if (!$rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'ProjectRoot must be a regular directory, not a link.'
}
$rootPrefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$output = if ([IO.Path]::IsPathRooted($OutputPath)) { [IO.Path]::GetFullPath($OutputPath) } else { [IO.Path]::GetFullPath((Join-Path $root $OutputPath)) }
$topFiles = @('README.md', 'AGENTS.md', '.gitignore', '.gitattributes', 'build.ps1')
$sourceDirectories = @('src', 'tests', 'scripts', 'docs')
$extensions = @('.cs', '.xaml', '.ps1', '.md', '.json', '.config', '.manifest', '.mjs', '.svg', '.yml', '.yaml')
$utf8 = New-Object Text.UTF8Encoding($false, $true)

function Test-ExcludedName([string]$Name, [bool]$IsDirectory) {
    if ($Name -like '.env*') { return $true }
    if ($Name -match '(?i)(^|[._-])(secrets?|credentials?|tokens?|private[-_]?key|api[-_]?key|service[-_]?account|client[-_]?secret)([._-]|$)') { return $true }
    if ($Name -match '(?i)^(id_(rsa|dsa|ecdsa|ed25519)|authorized_keys|known_hosts)(\.|$)') { return $true }
    if ($Name -match '(?i)\.(pem|key|pfx|p12|p7b|p7c|der|crt|cer|keystore)(\.|$)') { return $true }
    if ($IsDirectory) {
        return $Name -match '(?i)(^|[._-])(cache|caches|exports?|dist|artifacts|bin|obj|vs|git|node_modules|packages|testresults|coverage|venv|logs?|tmp|temp|runtime)([._-]|$)' -or $Name -ieq 'clipboard-images'
    }
    return $Name -match '(?i)^(state([._-].*)?\.json|notes\.txt|clipboard.*\.json)$'
}

function Test-ManagedPath([string]$RelativePath) {
    $parts = $RelativePath.Split('/')
    if ($parts.Count -eq 1) { return $topFiles -contains $RelativePath }
    return $sourceDirectories -contains $parts[0]
}

# An output filename must never overwrite a managed source or become its own input.
if ($output.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    $outputRelative = $output.Substring($rootPrefix.Length).Replace('\', '/')
    if (Test-ManagedPath $outputRelative) { throw 'OutputPath must be outside managed source directories and top-level source files.' }
}

# Only local, read-only Git commands are used. --no-index applies ignore rules
# even to tracked files; sensitive/runtime exclusions above apply independently.
$git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$gitMarker = $false
$ancestor = $rootItem
while ($null -ne $ancestor) {
    if (Test-Path -LiteralPath (Join-Path $ancestor.FullName '.git')) { $gitMarker = $true; break }
    $ancestor = $ancestor.Parent
}
function Invoke-LocalGit([string]$Arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $git.Source
    $gitRoot = $root
    if ($gitRoot.EndsWith('\')) { $gitRoot += '.' }
    $start.Arguments = '-C "' + $gitRoot + '" ' + $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['GIT_OPTIONAL_LOCKS'] = '0'
    foreach ($name in @('GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE')) { $start.EnvironmentVariables.Remove($name) }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $null = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $standardOutput.Trim() }
    }
    finally { $process.Dispose() }
}
$useGitIgnore = $false
if ($gitMarker) {
    if ($null -eq $git) { throw 'Git is required to respect this repository''s ignore rules.' }
    $repository = Invoke-LocalGit 'rev-parse --is-inside-work-tree'
    if ($repository.ExitCode -ne 0 -or $repository.Output -ne 'true') { throw 'Cannot inspect the local Git repository; no snapshot was written.' }
    $useGitIgnore = $true
}

$paths = New-Object 'System.Collections.Generic.List[string]'
$pendingDirectories = New-Object 'System.Collections.Generic.Stack[string]'
$pendingDirectories.Push($root)
while ($pendingDirectories.Count -gt 0) {
    $directory = $pendingDirectories.Pop()
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        $relative = $item.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        if (Test-ExcludedName $item.Name $item.PSIsContainer) { continue }
        if ($item.PSIsContainer) {
            if ($directory -eq $root -and $sourceDirectories -notcontains $item.Name) { continue }
            $pendingDirectories.Push($item.FullName)
            continue
        }
        if (!(Test-ManagedPath $relative)) { continue }
        if ($relative.Contains('/')) {
            $extension = [IO.Path]::GetExtension($item.Name)
            if ($extensions -notcontains $extension -and !($relative.StartsWith('docs/', [StringComparison]::OrdinalIgnoreCase) -and $extension -ieq '.txt')) { continue }
        }
        if ($useGitIgnore) {
            $ignored = Invoke-LocalGit ('check-ignore --no-index --quiet -- "' + $relative + '"')
            if ($ignored.ExitCode -eq 0) { continue }
            if ($ignored.ExitCode -ne 1) { throw ('Cannot check Git ignore rules for: ' + $relative) }
        }
        $paths.Add($relative)
    }
}
$paths.Sort([StringComparer]::Ordinal)
$files = New-Object 'System.Collections.Generic.List[object]'
$hasher = [Security.Cryptography.SHA256]::Create()
try {
    foreach ($relative in $paths) {
        $source = Join-Path $root $relative.Replace('/', '\')
        $sourceItem = Get-Item -LiteralPath $source -Force
        if ($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Source changed into a link: ' + $relative) }
        $bytes = [IO.File]::ReadAllBytes($source)
        # Keep an input UTF-8 BOM as U+FEFF in blob content. Windows PowerShell
        # 5 needs it to distinguish UTF-8 scripts from the local ANSI code page.
        try { $content = $utf8.GetString($bytes) }
        catch [Text.DecoderFallbackException] { throw ('Source is not valid UTF-8: ' + $relative) }
        if ($content.IndexOf([char]0) -ge 0) { throw ('Source is not a text file: ' + $relative) }
        $content = $content.Replace("`r`n", "`n").Replace("`r", "`n")
        $hash = [BitConverter]::ToString($hasher.ComputeHash($utf8.GetBytes($content))).Replace('-', '').ToLowerInvariant()
        $files.Add([pscustomobject][ordered]@{ path = $relative; mode = '100644'; type = 'blob'; content = $content; sha256 = $hash })
    }
}
finally { $hasher.Dispose() }
$payload = [ordered]@{ repository = 'TsuIppaku/Singularity'; branch = 'main'; files = @($files.ToArray()) }
$json = ($payload | ConvertTo-Json -Depth 6).Replace("`r`n", "`n") + "`n"

# Reject existing output links and linked parent directories before creating files.
$candidate = $output
while ($candidate) {
    if (Test-Path -LiteralPath $candidate) {
        $existing = Get-Item -LiteralPath $candidate -Force
        if ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'OutputPath must not traverse a link.' }
    }
    $candidate = [IO.Path]::GetDirectoryName($candidate)
}
$outputDirectory = [IO.Path]::GetDirectoryName($output)
[void][IO.Directory]::CreateDirectory($outputDirectory)
$temporary = Join-Path $outputDirectory ('.github-tree-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($temporary, $json, $utf8)
    if ([IO.File]::Exists($output)) { [IO.File]::Replace($temporary, $output, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temporary, $output) }
}
finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
foreach ($relative in $paths) { Write-Output $relative }
Write-Output ('Files: ' + $files.Count)
