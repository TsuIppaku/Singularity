# Run with Windows PowerShell 5.1; no GitHub access or product Git changes.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$generator = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\prepare-github-tree.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('OrbitGitHubTreeTests-' + [Guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false, $true)
$checks = 0
function Assert([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw ('FAIL: ' + $Name) }
    $script:checks++
    Write-Output ('PASS: ' + $Name)
}
function Write-Fixture([string]$Relative, [string]$Content) {
    $path = Join-Path $fixture $Relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $Content, $utf8)
}
try {
    [void][IO.Directory]::CreateDirectory($fixture)
    $unicode = [string][char]0x5947 + [char]0x70B9 + [char]0x1F31
    $sourceText = 'sensitive-output-sentinel ' + $unicode + "`r`nline two`rline three`n"
    Write-Fixture 'README.md' $sourceText
    Write-Fixture 'AGENTS.md' 'instructions'
    Write-Fixture '.gitignore' "src/ignored.cs`nsrc/ignored-by-git/`n"
    Write-Fixture '.gitattributes' '* text=auto'
    Write-Fixture 'build.ps1' 'Write-Output build'
    Write-Fixture 'src\Main.cs' $sourceText
    Write-Fixture 'src\Views\Panel.xaml' '<Window />'
    Write-Fixture 'src\settings.json' '{"example":true}'
    Write-Fixture 'tests\TimerTests.cs' 'class TimerTests {}'
    Write-Fixture 'scripts\sample.ps1' 'Write-Output sample'
    Write-Fixture 'docs\guide.txt' $unicode
    Write-Fixture 'docs\diagram.svg' '<svg />'
    $excluded = @('other.md', 'private\Main.cs', 'src\state.json', 'src\state.backup.json', 'src\notes.txt',
        'src\clipboard-slots.json', 'tests\ClipboardHistory.json', 'src\cache\a.cs', 'src\image-cache\meta.json',
        'tests\exports\meta.json', 'src\clipboard-drag-exports\payload.json', 'src\dist\x.cs', 'tests\artifacts\x.cs',
        'src\bin\x.cs', 'src\obj\x.cs', 'src\.vs\x.cs', 'src\.env', 'src\.env.local', 'src\.env.local.json',
        'src\private.pem', 'src\private.key', 'src\certificate.pem.json', 'src\credentials.json', 'src\client_secret_demo.json',
        'src\service-account.json', 'src\tokens.json', 'src\secrets\config.json', 'src\id_rsa', 'src\image.png', 'src\plain.txt',
        'dist\copy.cs', 'artifacts\copy.cs')
    foreach ($path in $excluded) { Write-Fixture $path 'EXCLUDED-CREDENTIAL-SENTINEL' }
    $bomPath = Join-Path $fixture 'src\Bom.cs'
    [IO.File]::WriteAllBytes($bomPath, ([byte[]]@(239, 187, 191) + $utf8.GetBytes($unicode + "`r`n")))
    $preview = @(& $generator -ProjectRoot $fixture)
    $output = Join-Path $fixture 'artifacts\github-tree.json'
    $jsonBytes = [IO.File]::ReadAllBytes($output)
    $json = $utf8.GetString($jsonBytes)
    $tree = $json | ConvertFrom-Json
    Assert ($tree.repository -ceq 'TsuIppaku/Singularity' -and $tree.branch -ceq 'main') 'Repository and branch are fixed to the authorized target'
    Assert ($tree.files.Count -eq 13) 'Only whitelisted source and docs files are included'
    Assert (!($json.Contains('EXCLUDED-CREDENTIAL-SENTINEL'))) 'Runtime data, credentials, binaries, caches and exports stay out'
    Assert (($preview -join "`n") -notmatch 'sensitive-output-sentinel|EXCLUDED-CREDENTIAL-SENTINEL') 'Console preview prints no file contents'
    Assert ($preview[-1] -eq 'Files: 13') 'Console preview reports the included file count'
    Assert ($jsonBytes[0] -eq 123 -and !$json.Contains("`r")) 'Output JSON is UTF-8 without BOM and uses LF'
    $main = $tree.files | Where-Object { $_.path -eq 'src/Main.cs' }
    Assert ($main.content -ceq $sourceText.Replace("`r`n", "`n").Replace("`r", "`n")) 'Unicode survives and CRLF/CR normalize to LF'
    $bom = $tree.files | Where-Object { $_.path -eq 'src/Bom.cs' }
    Assert ($bom.content -ceq ([string][char]0xFEFF + $unicode + "`n")) 'UTF-8 input BOM is preserved in blob content'
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $expectedHash = [BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes($main.content))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    Assert ($main.sha256 -ceq $expectedHash -and $main.mode -ceq '100644' -and $main.type -ceq 'blob') 'Blob metadata hashes the normalized UTF-8 content'
    $secondPreview = @(& $generator -ProjectRoot $fixture -OutputPath 'artifacts\review.json')
    Assert ([IO.File]::ReadAllText((Join-Path $fixture 'artifacts\review.json')) -ceq $json) 'Custom output destination produces the same stable snapshot'
    $refused = $false
    try { $null = & $generator -ProjectRoot $fixture -OutputPath 'src\settings.json' } catch { $refused = $true }
    Assert ($refused -and [IO.File]::ReadAllText((Join-Path $fixture 'src\settings.json')) -ceq '{"example":true}') 'Output cannot overwrite an eligible source file'

    # A junction requires no administrator privilege on Windows. It remains
    # inside the fixture, and the generator must not traverse it.
    $link = Join-Path $fixture 'src\linked'
    $linkCreated = $false
    try {
        $null = New-Item -ItemType Junction -Path $link -Target (Join-Path $fixture 'tests') -ErrorAction Stop
        $linkCreated = $true
        $null = & $generator -ProjectRoot $fixture
        $withLink = [IO.File]::ReadAllText($output) | ConvertFrom-Json
        Assert (@($withLink.files | Where-Object { $_.path -like 'src/linked/*' }).Count -eq 0) 'Reparse-point directories are not traversed'
    }
    finally { if ($linkCreated) { [IO.Directory]::Delete($link, $false) } }

    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $git) {
        # Only this isolated fixture is initialized; the product repository and
        # its config/index are never touched by these tests.
        $null = & $git.Source init --quiet -- $fixture
        if ($LASTEXITCODE -ne 0) { throw 'Isolated Git initialization failed.' }
        Write-Fixture 'src\ignored.cs' 'GIT-IGNORED-SENTINEL'
        Write-Fixture 'src\ignored-by-git\Main.cs' 'GIT-IGNORED-SENTINEL'
        $null = & $generator -ProjectRoot $fixture
        $gitSnapshot = [IO.File]::ReadAllText($output)
        Assert (!$gitSnapshot.Contains('GIT-IGNORED-SENTINEL')) 'Existing Git ignore rules are respected'
        Assert (!(Test-Path -LiteralPath (Join-Path $fixture '.git\index'))) 'Snapshot generation does not create a Git index'
    }
    else { Write-Output 'SKIP: Git executable unavailable for isolated ignore test.' }
    $defaultGenerator = Join-Path $fixture 'scripts\prepare-github-tree.ps1'
    Copy-Item -LiteralPath $generator -Destination $defaultGenerator
    $null = & $defaultGenerator
    $defaultTree = [IO.File]::ReadAllText($output) | ConvertFrom-Json
    Assert (@($defaultTree.files | Where-Object { $_.path -eq 'scripts/prepare-github-tree.ps1' }).Count -eq 1) 'Default project root resolves from the executing script on PowerShell 5'
    Write-Output ('PASS: ' + $checks + ' GitHub tree checks on PowerShell ' + $PSVersionTable.PSVersion.ToString())
}
finally {
    # Only the test-owned random directory is eligible for cleanup. Never
    # traverse a junction; if any remain, remove the junction itself first.
    $fullFixture = [IO.Path]::GetFullPath($fixture)
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($fullFixture) -ne $tempParent -or [IO.Path]::GetFileName($fullFixture) -notmatch '^OrbitGitHubTreeTests-[a-f0-9]{32}$') { throw 'Refusing cleanup outside the isolated test directory.' }
    if ([IO.Directory]::Exists($fullFixture)) { [IO.Directory]::Delete($fullFixture, $true) }
}
