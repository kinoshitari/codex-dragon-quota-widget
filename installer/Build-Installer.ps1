param(
    [Parameter(Mandatory = $true)]
    [string]$PluginRoot,
    [Parameter(Mandatory = $true)]
    [string]$SelfContainedBin,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$pluginName = 'codex-dragon-quota-widget'
$pluginRootPath = (Get-Item -LiteralPath $PluginRoot).FullName
$selfContainedPath = (Get-Item -LiteralPath $SelfContainedBin).FullName
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$buildRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CodexDragonWidgetInstallerBuild-" + [guid]::NewGuid().ToString('N'))
$payloadRoot = Join-Path $buildRoot $pluginName
$bundleRoot = Join-Path $buildRoot 'bundle'

if (Test-Path -LiteralPath $buildRoot) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $payloadRoot, $bundleRoot, $outputPath -Force | Out-Null

foreach ($name in @('.codex-plugin', 'assets', 'scripts', 'skills', 'installer')) {
    Copy-Item -LiteralPath (Join-Path $pluginRootPath $name) -Destination $payloadRoot -Recurse -Force
}
foreach ($name in @('Launch-Dragon-Quota-Widget.bat', 'README.md', 'CURRENT_VERSION_SUMMARY.md', 'LICENSE', 'ASSET-NOTICE.md', 'THIRD-PARTY-NOTICES.md')) {
    $sourcePath = Join-Path $pluginRootPath $name
    if (Test-Path -LiteralPath $sourcePath) {
        Copy-Item -LiteralPath $sourcePath -Destination $payloadRoot -Force
    }
}
New-Item -ItemType Directory -Path (Join-Path $payloadRoot 'bin\win-x64') -Force | Out-Null
Get-ChildItem -LiteralPath $selfContainedPath -Force | Copy-Item -Destination (Join-Path $payloadRoot 'bin\win-x64') -Recurse -Force

$payloadZip = Join-Path $bundleRoot 'codex-dragon-quota-widget-payload.zip'
Compress-Archive -LiteralPath $payloadRoot -DestinationPath $payloadZip -CompressionLevel Optimal -Force
Copy-Item -LiteralPath (Join-Path $pluginRootPath 'installer\Install.cmd') -Destination $bundleRoot -Force
Copy-Item -LiteralPath (Join-Path $pluginRootPath 'installer\Install-Dragon-Quota-Widget.ps1') -Destination $bundleRoot -Force
Copy-Item -LiteralPath (Join-Path $pluginRootPath 'installer\README-Installer.txt') -Destination $bundleRoot -Force

$zipOutput = Join-Path $outputPath 'CodexDragonQuotaWidget-Installer-x64.zip'
Compress-Archive -Path (Join-Path $bundleRoot '*') -DestinationPath $zipOutput -CompressionLevel Optimal -Force

$setupTemp = Join-Path $buildRoot 'CodexDragonQuotaWidget-Setup.exe'
$sedPath = Join-Path $buildRoot 'installer.sed'
$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$setupTemp
FriendlyName=傻龙插件 Installer
AppLaunched=Install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=Install.cmd
UserQuietInstCmd=Install.cmd
SourceFiles=SourceFiles
[Strings]
FILE0=Install.cmd
FILE1=Install-Dragon-Quota-Widget.ps1
FILE2=codex-dragon-quota-widget-payload.zip
[SourceFiles]
SourceFiles0=$bundleRoot\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
"@
Set-Content -LiteralPath $sedPath -Value $sed -Encoding ASCII
$iexpress = Join-Path $env:SystemRoot 'System32\iexpress.exe'
$process = Start-Process -FilePath $iexpress -ArgumentList @('/N', '/Q', $sedPath) -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $setupTemp)) {
    throw "IExpress 构建失败，退出代码：$($process.ExitCode)"
}

$setupOutput = Join-Path $outputPath 'CodexDragonQuotaWidget-Setup-x64.exe'
Copy-Item -LiteralPath $setupTemp -Destination $setupOutput -Force

[pscustomobject]@{
    Setup = $setupOutput
    Zip = $zipOutput
    SetupBytes = (Get-Item -LiteralPath $setupOutput).Length
    ZipBytes = (Get-Item -LiteralPath $zipOutput).Length
} | ConvertTo-Json

Remove-Item -LiteralPath $buildRoot -Recurse -Force
