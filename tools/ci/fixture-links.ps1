param(
    [Parameter(Mandatory)][ValidateSet('Link', 'Cleanup')][string]$Action,
    [Parameter(Mandatory)][string]$CheckoutRoot,
    [Parameter(Mandatory)][string]$Manifest,
    [string]$FixtureRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$checkout = (Resolve-Path -LiteralPath $CheckoutRoot).Path
$checkoutEntry = Get-Item -LiteralPath $checkout
if (-not $checkoutEntry.PSIsContainer -or ($checkoutEntry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The checkout root must be a physical directory.'
}
$prefix = $checkout.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$relativePaths = @('client-data', 'artifacts/shop-tables-survey')
$allowedTargets = @($relativePaths | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $checkout $_)) })

function Save-Ownership($Record) {
    $Record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Manifest -Encoding utf8
}

function Assert-PhysicalParents([string]$Target) {
    $parent = Split-Path -Parent $Target
    while ($parent -ne $checkout) {
        if (-not $parent.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture parent escaped the checkout.' }
        $item = Get-Item -LiteralPath $parent -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Fixture ancestors must not be links.' }
        $parent = Split-Path -Parent $parent
    }
}

if ($Action -eq 'Link') {
    if (Test-Path -LiteralPath $Manifest) { throw 'This attempt already has a fixture ownership manifest.' }
    if (-not $FixtureRoot) { throw 'FixtureRoot is required when linking fixtures.' }
    $fixture = (Resolve-Path -LiteralPath $FixtureRoot).Path
    $record = [PSCustomObject]@{ Version = 1; Checkout = $checkout; Links = @() }
    Save-Ownership $record
    foreach ($relative in $relativePaths) {
        $source = (Resolve-Path -LiteralPath (Join-Path $fixture $relative)).Path
        if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Missing fixture directory: $relative" }
        $target = [IO.Path]::GetFullPath((Join-Path $checkout $relative))
        Assert-PhysicalParents $target
        if (Get-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue) { throw "Fixture target already exists: $relative" }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        New-Item -ItemType Junction -Path $target -Target $source | Out-Null
        $record.Links += [PSCustomObject]@{ Target = $target; Source = $source }
        # Persist each successful creation; a later failure must not lose earlier ownership.
        Save-Ownership $record
    }
}
else {
    if (-not (Test-Path -LiteralPath $Manifest)) { return }
    $record = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
    if ($record.Version -ne 1 -or $record.Checkout -ne $checkout) { throw 'Fixture ownership does not match this checkout.' }
    foreach ($link in $record.Links) {
        $target = [IO.Path]::GetFullPath($link.Target)
        if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $target -notin $allowedTargets) {
            throw 'Fixture ownership names a path outside the allowed checkout locations.'
        }
        Assert-PhysicalParents $target
        $entry = Get-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        if (-not $entry) { continue }
        if ($entry.LinkType -ne 'Junction' -or -not ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Owned fixture path is no longer a junction: $target"
        }
        if (@($entry.Target).Count -ne 1 -or [IO.Path]::GetFullPath(@($entry.Target)[0]) -ne $link.Source) {
            throw "Owned fixture junction now has a different target: $target"
        }
        # Nonrecursive deletion removes only this junction, never its fixture target.
        [IO.Directory]::Delete($target)
    }
}
