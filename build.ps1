$ErrorActionPreference = "Continue"
$src = "C:\Users\Administrator\Desktop\HoverFolder\cs"
$out = "$src\HoverExplorer.exe"

function Find-Gac($name) {
    $hits = Get-ChildItem 'C:\Windows\Microsoft.NET\assembly' -Recurse -Filter $name -ErrorAction SilentlyContinue
    $g64 = $hits | Where-Object { $_.FullName -like '*GAC_64*' } | Select-Object -First 1
    if ($g64) { return $g64.FullName }
    $gmsil = $hits | Where-Object { $_.FullName -like '*GAC_MSIL*' } | Select-Object -First 1
    if ($gmsil) { return $gmsil.FullName }
    return $null
}

$refs = @(
    'System.Windows.Forms.dll','System.Drawing.dll','System.dll','System.Core.dll','Accessibility.dll',
    'System.IO.Compression.FileSystem.dll','System.IO.Compression.dll'
)
$gacRefs = @('UIAutomationClient','UIAutomationTypes','PresentationCore','PresentationFramework','WindowsBase','WindowsFormsIntegration','System.Xaml')
foreach ($g in $gacRefs) {
    $p = Find-Gac "$g.dll"
    if (-not $p) { Write-Output "MISSING: $g"; exit 2 }
    $refs += $p
}

if (Test-Path $out) { Set-ItemProperty $out -Name IsReadOnly -Value $false }
$srcFiles = @('AssemblyInfo.cs','CardPalette.cs','Config.cs','EntryListForm.cs','IMediaSession.cs','MciAudioSession.cs','FfmpegAudioSession.cs','FfmpegDownload.cs','ExplorerSupport.cs','ExplorerUia.cs','I18n.cs','Ingest.cs','MediaDecoder.cs','MediaPreview.cs','NativeMethods.cs','PaletteListForm.cs','PerchSupport.cs','PinCardForm.cs','PopupForm.cs','PreviewProvider.cs','Program.cs','RemoteReader.cs','SettingsForm.cs','ShellContextMenu.cs','SlimScrollBar.cs','TcSupport.cs','Win11.cs','WaveDecoder.cs') | ForEach-Object { "$src\$_" }

& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:winexe /out:$out /resource:"$src\donate_qr.png",HoverExplorer.donate_qr.png ($refs | ForEach-Object { "/r:$_" }) $srcFiles
if ($LASTEXITCODE -eq 0) { Write-Output "BUILD OK" } else { Write-Output "BUILD FAIL" }

$fixerOut = "$src\Mp3Fixer.exe"
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:winexe /out:$fixerOut /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.dll /r:System.Core.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "$src\Mp3Fixer.cs"
if ($LASTEXITCODE -eq 0) { Write-Output "FIXER OK" } else { Write-Output "FIXER FAIL" }