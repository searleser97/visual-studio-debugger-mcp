[CmdletBinding()]
param(
    [string]$Repository = "searleser97/visual-studio-debugger-mcp",
    [string]$Version = "latest",
    [string]$InstallDirectory = "$env:LOCALAPPDATA\VisualStudioDebuggerMcp"
)

$ErrorActionPreference = "Stop"
$assetName = "visual-studio-debugger-mcp-win-x64.zip"
$releaseUri = if ($Version -eq "latest") {
    "https://api.github.com/repos/$Repository/releases/latest"
}
else {
    "https://api.github.com/repos/$Repository/releases/tags/$Version"
}

$release = Invoke-RestMethod -Uri $releaseUri
$asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
if ($null -eq $asset) {
    throw "Release '$Version' does not contain '$assetName'."
}

$archive = Join-Path ([System.IO.Path]::GetTempPath()) "$([Guid]::NewGuid()).zip"
try {
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive
    New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
    Expand-Archive -Path $archive -DestinationPath $InstallDirectory -Force
}
finally {
    Remove-Item -Path $archive -Force -ErrorAction SilentlyContinue
}

$executable = Join-Path $InstallDirectory "VisualStudioDebuggerMcp.exe"
if (-not (Test-Path $executable)) {
    throw "Installation completed without producing '$executable'."
}

Write-Output "Installed Visual Studio Debugger MCP to $InstallDirectory"
Write-Output "Configure the MCP command as: $executable"
