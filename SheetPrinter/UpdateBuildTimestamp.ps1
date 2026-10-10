param(
	[Parameter(Mandatory = $true)]
	[string]$SourceFile
)

$source = [System.IO.File]::ReadAllText($SourceFile)
$pattern = '(?m)(?<prefix>private const string BuildTimestamp = ")[0-9]{8}-[0-9]{6}(?<suffix>";)'
$matches = [System.Text.RegularExpressions.Regex]::Matches($source, $pattern)

if ($matches.Count -ne 1) {
	throw "Expected exactly one BuildTimestamp constant in '$SourceFile'."
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$replacement = '${prefix}' + $timestamp + '${suffix}'
$updatedSource = [System.Text.RegularExpressions.Regex]::Replace($source, $pattern, $replacement)

if ($updatedSource -ne $source) {
	$encoding = New-Object System.Text.UTF8Encoding($false)
	[System.IO.File]::WriteAllText($SourceFile, $updatedSource, $encoding)
}
