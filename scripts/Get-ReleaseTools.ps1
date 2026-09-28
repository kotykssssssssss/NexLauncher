# Run from Build-Release.ps1. Only downloads official, signed build prerequisites.
param([Parameter(Mandatory)][string]$ToolsDirectory)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $ToolsDirectory | Out-Null

function Assert-Signature([string]$Path, [string]$Publisher) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch $Publisher) {
        throw "Invalid signature or publisher: $Path"
    }
}

$inno = Join-Path $ToolsDirectory 'innosetup-7.1.0-x64.exe'
if (!(Test-Path -LiteralPath $inno)) {
    Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' -OutFile $inno -TimeoutSec 180
}
if ((Get-FileHash -LiteralPath $inno -Algorithm SHA256).Hash -ne '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f') {
    throw 'Inno Setup 7.1.0 checksum mismatch. Remove the cached tool and retry.'
}
Assert-Signature $inno 'CN=Pyrsys B\.V\.'
$compilerDirectory = Join-Path $ToolsDirectory 'InnoSetup'
# Re-extract the verified distribution: do not trust a stale/tampered local compiler.
$process = Start-Process -FilePath $inno -ArgumentList @('/PORTABLE=1', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS', ('/DIR="' + $compilerDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $compilerDirectory 'ISCC.exe'))) { throw 'Could not prepare Inno Setup compiler.' }

$webview = Join-Path $ToolsDirectory 'MicrosoftEdgeWebview2Setup.exe'
if (!(Test-Path -LiteralPath $webview)) {
    # Evergreen bootstrapper: Microsoft intentionally services this official URL.
    Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $webview -TimeoutSec 180
}
Assert-Signature $webview 'O=Microsoft Corporation(?:,|$)'
