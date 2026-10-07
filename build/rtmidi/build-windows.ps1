param(
    [Parameter(Mandatory = $true)][string]$SourceCheckout,
    [string]$OutputDirectory = "$PSScriptRoot\..\..\bin\rtmidi-build\windows"
)

$ErrorActionPreference = 'Stop'
$pinnedCommit = '1e5b49925aa60065db52de44c366d446a902547b'
$sourcePath = (Resolve-Path -LiteralPath $SourceCheckout).Path
$sourceCommit = & git -C $sourcePath rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $sourceCommit -ne $pinnedCommit) {
    throw "SourceCheckout must be an RtMidi checkout at $pinnedCommit."
}
& git -C $sourcePath diff --exit-code HEAD --
if ($LASTEXITCODE -ne 0) { throw 'RtMidi source checkout has tracked modifications.' }
if ($env:VSCMD_ARG_TGT_ARCH -ne 'x64') {
    throw 'Run from a Visual Studio 2022 x64 developer PowerShell with MSVC 14.38.33130.'
}
Get-Command cl.exe, dumpbin.exe -ErrorAction Stop | Out-Null
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
Push-Location -LiteralPath $outputPath
try {
    & cl.exe /nologo /O2 /MT /EHsc /GR /LD /DUNICODE /D_UNICODE /D__WINDOWS_MM__ /DRTMIDI_EXPORT /DRTMIDI_DO_NOT_ENSURE_UNIQUE_PORTNAMES "$sourcePath\RtMidi.cpp" "$sourcePath\rtmidi_c.cpp" winmm.lib /Fe:rtmidi.dll
    if ($LASTEXITCODE -ne 0) { throw 'RtMidi compilation failed.' }
    & dumpbin.exe /dependents rtmidi.dll
    if ($LASTEXITCODE -ne 0) { throw 'Native dependency inspection failed.' }
    & dumpbin.exe /exports rtmidi.dll
    if ($LASTEXITCODE -ne 0) { throw 'Native export inspection failed.' }
    Get-FileHash -Algorithm SHA256 -LiteralPath "$outputPath\rtmidi.dll"
}
finally { Pop-Location }
