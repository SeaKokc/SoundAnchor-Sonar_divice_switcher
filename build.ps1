param([string]$OutputName = 'SoundAnchor.exe')

$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'src\SoundAnchor.cs'
$outputDirectory = Join-Path $PSScriptRoot 'dist'
$output = Join-Path $outputDirectory $OutputName

if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Force
}

Add-Type -Path $source `
    -ReferencedAssemblies @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll') `
    -OutputAssembly $output `
    -OutputType WindowsApplication

Write-Host "Built: $output"
