[CmdletBinding()]
param([string]$ReleaseDirectory = (Join-Path $PSScriptRoot 'dist\v1.0.0'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$releasePath = [IO.Path]::GetFullPath($ReleaseDirectory)
$expectedSource = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PUBLIC_FILES.txt') -Encoding UTF8 | Where-Object { $_.Trim() } | Sort-Object)
function Confirm-Archive([string]$archivePath, [string[]]$expected, [bool]$sourceArchive) {
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entries = @($archive.Entries | Where-Object { !$_.FullName.EndsWith('/') })
        $actual = @($entries | ForEach-Object {
            $name = $_.FullName.Replace('\','/')
            if ($name -match '(^|/)\.\.(/|$)' -or $name.StartsWith('/')) { throw 'ZIP에 잘못된 경로가 있습니다.' }
            $name.Substring($name.IndexOf('/') + 1)
        } | Sort-Object)
        if (Compare-Object -ReferenceObject $expected -DifferenceObject $actual -CaseSensitive) { throw "ZIP 공개 파일 목록 불일치: $archivePath" }
        foreach ($entry in $entries) {
            $name = $entry.FullName.Replace('\','/'); $name = $name.Substring($name.IndexOf('/') + 1)
            $original = if ($sourceArchive) { Join-Path $PSScriptRoot $name } elseif ($name -eq '화면지킴이.exe') { Join-Path $releasePath $name } else { Join-Path $PSScriptRoot $name }
            $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
            try { $zipHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
            finally { $stream.Dispose(); $sha.Dispose() }
            if ($zipHash -ne (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash) { throw "ZIP 파일 내용 불일치: $name" }
        }
    } finally { $archive.Dispose() }
}
Confirm-Archive (Join-Path $releasePath 'screen-keeper-v1.0.0-source.zip') $expectedSource $true
Confirm-Archive (Join-Path $releasePath 'screen-keeper-v1.0.0-windows.zip') @('LICENSE','사용방법.txt','화면지킴이.exe' | Sort-Object) $false
$hashLines = @(Get-Content -LiteralPath (Join-Path $releasePath 'SHA256SUMS.txt') -Encoding UTF8)
if ($hashLines.Count -ne 3) { throw '체크섬 파일 개수가 다릅니다.' }
foreach ($line in $hashLines) {
    if ($line -notmatch '^([a-f0-9]{64})  ([^\\/]+)$') { throw '잘못된 체크섬 형식입니다.' }
    $expectedHash = $Matches[1]; $name = $Matches[2]
    if ((Get-FileHash -LiteralPath (Join-Path $releasePath $name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) { throw "SHA-256 불일치: $name" }
}
Write-Output '공개 ZIP 목록, 모든 파일 내용 및 SHA-256 검사 통과'
