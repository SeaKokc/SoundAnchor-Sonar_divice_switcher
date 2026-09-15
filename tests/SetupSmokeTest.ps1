param(
    [string]$SetupPath = (Join-Path $PSScriptRoot '..\dist\SoundAnchor-Setup.exe'),
    [string]$ScreenshotPath = (Join-Path $PSScriptRoot '..\artifacts\setup-preview.png')
)

$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFile((Resolve-Path $SetupPath))
$resourceName = $assembly.GetManifestResourceNames() | Where-Object { $_ -like '*SoundAnchor.exe' } | Select-Object -First 1
if (-not $resourceName) { throw 'Embedded SoundAnchor payload was not found.' }

$stream = $assembly.GetManifestResourceStream($resourceName)
try {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $payloadHash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
    finally { $sha.Dispose() }
}
finally { $stream.Dispose() }

$formType = $assembly.GetType('SoundAnchorSetup.SetupForm', $true)
$form = [Activator]::CreateInstance($formType, $true)
try {
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    $bitmap = New-Object Drawing.Bitmap($form.Width, $form.Height)
    try {
        $form.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
        $directory = Split-Path -Parent $ScreenshotPath
        if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
        $bitmap.Save($ScreenshotPath, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}
finally { $form.Dispose() }

Write-Host "Setup: $SetupPath"
Write-Host "Embedded payload: $resourceName"
Write-Host "Payload SHA256: $payloadHash"
Write-Host "Screenshot: $ScreenshotPath"
