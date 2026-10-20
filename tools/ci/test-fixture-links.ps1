Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'fixture-links.ps1'
$testRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('bff-ci-links-' + [Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$ownedLinks = [Collections.Generic.List[string]]::new()

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Expect-Failure([scriptblock]$Work) {
    $failed = $false
    try { & $Work } catch { $failed = $true }
    Require $failed 'Expected ownership validation to fail.'
}

try {
    $fixtures = Join-Path $testRoot 'fixtures'
    foreach ($relative in @('client-data', 'artifacts/shop-tables-survey')) {
        $directory = Join-Path $fixtures $relative
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $directory 'keep.txt') -Value 'fixture'
    }
    $normal = Join-Path $testRoot 'normal'
    New-Item -ItemType Directory -Path $normal | Out-Null
    $manifest = Join-Path $testRoot 'normal.json'
    & $helper -Action Link -CheckoutRoot $normal -FixtureRoot $fixtures -Manifest $manifest
    $ownedLinks.Add((Join-Path $normal 'client-data'))
    $ownedLinks.Add((Join-Path $normal 'artifacts/shop-tables-survey'))
    & $helper -Action Cleanup -CheckoutRoot $normal -Manifest $manifest
    Require (-not (Test-Path -LiteralPath (Join-Path $normal 'client-data'))) 'Owned link was not removed.'
    Require (Test-Path -LiteralPath (Join-Path $fixtures 'client-data/keep.txt')) 'Cleanup removed fixture content.'

    foreach ($firstExisting in @($true, $false)) {
        $checkout = Join-Path $testRoot ('existing-' + $firstExisting)
        New-Item -ItemType Directory -Path $checkout | Out-Null
        $relative = if ($firstExisting) { 'client-data' } else { 'artifacts/shop-tables-survey' }
        $existing = Join-Path $checkout $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $existing) -Force | Out-Null
        New-Item -ItemType Junction -Path $existing -Target (Join-Path $fixtures $relative) | Out-Null
        $ownedLinks.Add($existing)
        $attemptManifest = Join-Path $testRoot ('existing-' + $firstExisting + '.json')
        Expect-Failure { & $helper -Action Link -CheckoutRoot $checkout -FixtureRoot $fixtures -Manifest $attemptManifest }
        if (-not $firstExisting) { $ownedLinks.Add((Join-Path $checkout 'client-data')) }
        & $helper -Action Cleanup -CheckoutRoot $checkout -Manifest $attemptManifest
        Require ((Get-Item -LiteralPath $existing).LinkType -eq 'Junction') 'Cleanup removed a preexisting link.'
        $count = if ($firstExisting) { 0 } else { 1 }
        Require (@((Get-Content -LiteralPath $attemptManifest -Raw | ConvertFrom-Json).Links).Count -eq $count) 'Partial ownership was not saved.'
        if (-not $firstExisting) {
            Require (-not (Test-Path -LiteralPath (Join-Path $checkout 'client-data'))) 'Earlier successful link was not reclaimed.'
        }
    }

    $outside = Join-Path $testRoot 'outside'
    New-Item -ItemType Junction -Path $outside -Target (Join-Path $fixtures 'client-data') | Out-Null
    $ownedLinks.Add($outside)
    $badManifest = Join-Path $testRoot 'outside.json'
    @{ Version = 1; Checkout = $normal; Links = @(@{Target=$outside;Source=(Join-Path $fixtures 'client-data')}) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $badManifest
    Expect-Failure { & $helper -Action Cleanup -CheckoutRoot $normal -Manifest $badManifest }
    Require ((Get-Item -LiteralPath $outside).LinkType -eq 'Junction') 'Outside junction was removed.'

    $changed = Join-Path $testRoot 'changed'
    New-Item -ItemType Directory -Path $changed | Out-Null
    $changedManifest = Join-Path $testRoot 'changed.json'
    & $helper -Action Link -CheckoutRoot $changed -FixtureRoot $fixtures -Manifest $changedManifest
    $changedTarget = Join-Path $changed 'client-data'
    $ownedLinks.Add($changedTarget)
    $ownedLinks.Add((Join-Path $changed 'artifacts/shop-tables-survey'))
    [IO.Directory]::Delete($changedTarget)
    New-Item -ItemType Junction -Path $changedTarget -Target (Join-Path $fixtures 'artifacts/shop-tables-survey') | Out-Null
    Expect-Failure { & $helper -Action Cleanup -CheckoutRoot $changed -Manifest $changedManifest }
    Require ((Get-Item -LiteralPath $changedTarget).LinkType -eq 'Junction') 'Retargeted junction was removed.'
    Require (Test-Path -LiteralPath (Join-Path $changedTarget 'keep.txt')) 'Retargeted fixture content was removed.'

    $linkedParent = Join-Path $testRoot 'linked-parent'
    New-Item -ItemType Directory -Path $linkedParent | Out-Null
    $parentLink = Join-Path $linkedParent 'artifacts'
    New-Item -ItemType Junction -Path $parentLink -Target (Join-Path $fixtures 'artifacts') | Out-Null
    $ownedLinks.Add($parentLink)
    $parentManifest = Join-Path $testRoot 'linked-parent.json'
    Expect-Failure { & $helper -Action Link -CheckoutRoot $linkedParent -FixtureRoot $fixtures -Manifest $parentManifest }
    $ownedLinks.Add((Join-Path $linkedParent 'client-data'))
    & $helper -Action Cleanup -CheckoutRoot $linkedParent -Manifest $parentManifest
    Require ((Get-Item -LiteralPath $parentLink).LinkType -eq 'Junction') 'Preexisting parent link was removed.'
    Require (Test-Path -LiteralPath (Join-Path $fixtures 'artifacts/shop-tables-survey/keep.txt')) 'Parent link validation damaged fixtures.'

    $normalDirectory = Join-Path $normal 'client-data'
    New-Item -ItemType Directory -Path $normalDirectory | Out-Null
    Set-Content -LiteralPath (Join-Path $normalDirectory 'keep.txt') -Value 'not a junction'
    Expect-Failure { & $helper -Action Cleanup -CheckoutRoot $normal -Manifest $manifest }
    Require (Test-Path -LiteralPath (Join-Path $normalDirectory 'keep.txt')) 'Replacement directory was removed.'
    Write-Output 'PASS fixture ownership: cleanup, preexisting targets, outside path, retargeted junction, linked ancestor and replaced directory.'
}
finally {
    # Every path was created by this test beneath its unique temporary root.
    foreach ($link in $ownedLinks) {
        if (-not [IO.Path]::GetFullPath($link).StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Test cleanup escaped its temporary root.'
        }
        $entry = Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
        if ($entry -and $entry.LinkType -eq 'Junction') { [IO.Directory]::Delete($link) }
    }
    if (-not $testRoot.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Test root escaped the temporary directory.'
    }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
