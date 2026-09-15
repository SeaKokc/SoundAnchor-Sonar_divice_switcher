param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\dist\SoundAnchor.exe'),
    [string]$ScreenshotPath = (Join-Path $PSScriptRoot '..\artifacts\ui-preview.png'),
    [string]$FeedbackScreenshotPath = (Join-Path $PSScriptRoot '..\artifacts\ui-feedback-preview.png'),
    [string]$DarkScreenshotPath = (Join-Path $PSScriptRoot '..\artifacts\ui-dark-preview.png')
)

$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFile((Resolve-Path $AssemblyPath))
$serviceType = $assembly.GetType('SoundAnchor.AudioDeviceService', $true)
$flowType = $assembly.GetType('SoundAnchor.EDataFlow', $true)
$configType = $assembly.GetType('SoundAnchor.AppConfiguration', $true)
$formType = $assembly.GetType('SoundAnchor.SettingsForm', $true)
$service = [Activator]::CreateInstance($serviceType, $true)

try {
    $renderFlow = [Enum]::ToObject($flowType, 0)
    $captureFlow = [Enum]::ToObject($flowType, 1)
    $outputs = $serviceType.GetMethod('GetActiveDevices').Invoke($service, @($renderFlow))
    $inputs = $serviceType.GetMethod('GetActiveDevices').Invoke($service, @($captureFlow))
    $outputId = $serviceType.GetMethod('GetDefaultDeviceId').Invoke($service, @($renderFlow))
    $inputId = $serviceType.GetMethod('GetDefaultDeviceId').Invoke($service, @($captureFlow))
    $output = $outputs | Where-Object Id -EQ $outputId | Select-Object -First 1
    $input = $inputs | Where-Object Id -EQ $inputId | Select-Object -First 1
    if (-not $output -or -not $input) { throw 'Default audio endpoints were not found.' }

    $args = @($output.Id, $output.Name, $true, $input.Id, $input.Name, $true, $true)
    $config = [Activator]::CreateInstance($configType, [Reflection.BindingFlags]'Instance, Public, NonPublic', $null, $args, $null)
    $form = [Activator]::CreateInstance($formType, [Reflection.BindingFlags]'Instance, Public, NonPublic', $null, @($service, $config), $null)
    try {
        $formType.GetMethod('RefreshDevices').Invoke($form, @())
        $form.Location = [Drawing.Point]::new(-2000, -2000)
        $form.Show()
        [Windows.Forms.Application]::DoEvents()
        $flags = [Reflection.BindingFlags]'Instance, NonPublic'
        $formType.GetField('language', $flags).SetValue($form, 'ru')
        $formType.GetField('darkMode', $flags).SetValue($form, $false)
        $formType.GetMethod('ApplyLanguage', $flags).Invoke($form, @())
        $formType.GetMethod('ApplyTheme', $flags).Invoke($form, @())
        $titleBar = $formType.GetField('titleBar', $flags).GetValue($form)
        $titleBar.SetPreferences('ru', $false)
        $bitmap = New-Object Drawing.Bitmap $form.Width, $form.Height
        try {
            $form.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $form.Width, $form.Height))
            $directory = Split-Path -Parent $ScreenshotPath
            if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
            $bitmap.Save($ScreenshotPath, [Drawing.Imaging.ImageFormat]::Png)
            $feedbackMethod = $formType.GetMethod('ShowFeedback', [Reflection.BindingFlags]'Instance, NonPublic')
            $feedbackMethod.Invoke($form, @('Settings saved and applied', $true))
            [Windows.Forms.Application]::DoEvents()
            $form.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $form.Width, $form.Height))
            $bitmap.Save($FeedbackScreenshotPath, [Drawing.Imaging.ImageFormat]::Png)
            $formType.GetField('language', $flags).SetValue($form, 'en')
            $formType.GetField('darkMode', $flags).SetValue($form, $true)
            $formType.GetMethod('ApplyLanguage', $flags).Invoke($form, @())
            $formType.GetMethod('ApplyTheme', $flags).Invoke($form, @())
            $titleBar.SetPreferences('en', $true)
            $feedbackMethod.Invoke($form, @('Theme and language updated', $true))
            [Windows.Forms.Application]::DoEvents()
            $form.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $form.Width, $form.Height))
            $bitmap.Save($DarkScreenshotPath, [Drawing.Imaging.ImageFormat]::Png)
        } finally { $bitmap.Dispose() }
    } finally { $form.Dispose() }

    Write-Output "Outputs: $($outputs.Count)"
    Write-Output "Inputs: $($inputs.Count)"
    Write-Output "Default output: $($output.Name)"
    Write-Output "Default input: $($input.Name)"
    Write-Output "Screenshot: $ScreenshotPath"
    Write-Output "Feedback screenshot: $FeedbackScreenshotPath"
    Write-Output "Dark screenshot: $DarkScreenshotPath"
} finally {
    $serviceType.GetMethod('Dispose').Invoke($service, @()) | Out-Null
}
