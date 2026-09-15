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

$installerSource = Join-Path $PSScriptRoot 'installer\SoundAnchor.Setup.cs'
$installerOutput = Join-Path $outputDirectory 'SoundAnchor-Setup.exe'
if (Test-Path -LiteralPath $installerOutput) {
    Remove-Item -LiteralPath $installerOutput -Force
}

$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$parameters = New-Object System.CodeDom.Compiler.CompilerParameters
$parameters.GenerateExecutable = $true
$parameters.GenerateInMemory = $false
$parameters.OutputAssembly = $installerOutput
$parameters.CompilerOptions = '/target:winexe /optimize+'
[void]$parameters.ReferencedAssemblies.Add('System.dll')
[void]$parameters.ReferencedAssemblies.Add('System.Drawing.dll')
[void]$parameters.ReferencedAssemblies.Add('System.Windows.Forms.dll')
[void]$parameters.EmbeddedResources.Add($output)
$results = $provider.CompileAssemblyFromFile($parameters, $installerSource)
if ($results.Errors.HasErrors) {
    $messages = $results.Errors | ForEach-Object { $_.ToString() }
    throw ($messages -join [Environment]::NewLine)
}

Write-Host "Built: $installerOutput"
