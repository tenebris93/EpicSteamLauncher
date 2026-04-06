[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Range,

    [string]$PrTitle = ""
)

$ErrorActionPreference = "Stop"

# Keep this aligned with release note grouping in .github/workflows/release.yml
$allowedPattern = '^(feat|fix|perf|refactor|docs|test|chore)(\([a-z0-9][a-z0-9\-/]*\))?(!)?: .+'
$allowedExamples = @(
    "feat: add profile import command",
    "fix(steam): handle missing shortcuts.vdf",
    "docs: clarify release workflow"
)

$allowByPrefix = @(
    "Merge ",
    'Revert "'
)

$lines = git --no-pager log $Range --no-merges '--pretty=format:%H%x09%s'

if (-not $lines) {
    Write-Host "No commits found in range '$Range'."
    exit 0
}

$invalid = New-Object System.Collections.Generic.List[string]

foreach ($line in $lines) {
    $parts = $line -split "`t", 2

    if ($parts.Length -lt 2) {
        continue
    }

    $sha = $parts[0]
    $subject = $parts[1].Trim()

    $isAllowedByPrefix = $false

    foreach ($prefix in $allowByPrefix) {
        if ($subject.StartsWith($prefix, [System.StringComparison]::Ordinal)) {
            $isAllowedByPrefix = $true
            break
        }
    }

    if ($isAllowedByPrefix) {
        continue
    }

    if (-not ($subject -match $allowedPattern)) {
        $invalid.Add("$sha`t$subject")
    }
}

if (-not [string]::IsNullOrWhiteSpace($PrTitle)) {
    if (-not ($PrTitle -match $allowedPattern)) {
        $invalid.Add("PR_TITLE`t$PrTitle")
    }
}

if ($invalid.Count -gt 0) {
    Write-Host "ERROR: Found commit messages that do not follow the required Conventional Commit format."
    Write-Host ""
    Write-Host "Required format:"
    Write-Host "  <type>(optional-scope): <description>"
    Write-Host ""
    Write-Host "Allowed types: feat, fix, perf, refactor, docs, test, chore"
    Write-Host ""
    Write-Host "Examples:"

    foreach ($example in $allowedExamples) {
        Write-Host "  - $example"
    }

    Write-Host ""
    Write-Host "See CONTRIBUTING.md for the full commit message policy."
    Write-Host ""
    Write-Host "Invalid entries:"

    foreach ($entry in $invalid) {
        $parts = $entry -split "`t", 2
        Write-Host "  - $($parts[0]): $($parts[1])"
    }

    exit 1
}

Write-Host "Commit message validation passed for range '$Range'."




