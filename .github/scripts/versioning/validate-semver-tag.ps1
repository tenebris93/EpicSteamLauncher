param(
    [Parameter(Mandatory = $true)]
    [string]$RawTag
)

# Normalize input so callers can pass either refs/tags/*, vX.Y.Z, or X.Y.Z.
$tag = $RawTag.Trim()
if ([string]::IsNullOrWhiteSpace($tag)) {
    throw "Semantic version validation failed: tag value is empty."
}

if ($tag.StartsWith('refs/tags/', [System.StringComparison]::OrdinalIgnoreCase)) {
    $tag = $tag.Substring(10)
}

$normalizedVersion = if ($tag.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
    $tag.Substring(1)
}
else {
    $tag
}

# Accept SemVer 2.0.0 with optional prerelease/build metadata.
$semverPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
if ($normalizedVersion -notmatch $semverPattern) {
    throw "Semantic version validation failed: '$RawTag' is not valid SemVer (expected vMAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH)."
}

# Emit both full version and major.minor so workflows can feed GitSemVerTagOverride.
$parts = $normalizedVersion.Split('.')
$majorMinor = "$($parts[0]).$($parts[1])"
$normalizedTag = "v$normalizedVersion"

Write-Host "Semantic version validated. tag=$normalizedTag version=$normalizedVersion major_minor=$majorMinor"

# Export values for downstream GitHub Actions steps when available.
if ($env:GITHUB_OUTPUT) {
    "tag=$normalizedTag" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "version=$normalizedVersion" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "major_minor=$majorMinor" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
}


