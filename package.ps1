[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$releaseVersion = '1.0.0'
& (Join-Path $PSScriptRoot 'test.ps1')
$releasePath = Join-Path $PSScriptRoot "dist\v$releaseVersion"
New-Item -ItemType Directory -Force -Path $releasePath | Out-Null
$stagePath = Join-Path $PSScriptRoot ('build\package-' + [Guid]::NewGuid().ToString('N'))
$sourceStage = Join-Path $stagePath "screen-keeper-v$releaseVersion-source"
$binaryStage = Join-Path $stagePath "screen-keeper-v$releaseVersion-windows"
New-Item -ItemType Directory -Path $sourceStage,$binaryStage | Out-Null
$publicFiles = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PUBLIC_FILES.txt') -Encoding UTF8 | Where-Object { $_.Trim() })
foreach ($relativePath in $publicFiles) {
    if ([IO.Path]::IsPathRooted($relativePath) -or $relativePath -match '(^|[\/])\.\.([\/]|$)') { throw '공개 목록에 잘못된 경로가 있습니다.' }
    $sourceFile = Join-Path $PSScriptRoot $relativePath
    $item = Get-Item -LiteralPath $sourceFile
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "공개 목록은 일반 파일만 허용합니다: $relativePath" }
    $destination = Join-Path $sourceStage $relativePath
    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath $sourceFile -Destination $destination
}
$appExecutable = Join-Path $PSScriptRoot 'build\anycpu\화면지킴이.exe'
Copy-Item -LiteralPath $appExecutable -Destination (Join-Path $releasePath '화면지킴이.exe')
Copy-Item -LiteralPath $appExecutable -Destination (Join-Path $binaryStage '화면지킴이.exe')
foreach ($name in @('LICENSE','사용방법.txt')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $binaryStage $name) }
$binaryZip = Join-Path $releasePath "screen-keeper-v$releaseVersion-windows.zip"
$sourceZip = Join-Path $releasePath "screen-keeper-v$releaseVersion-source.zip"
Compress-Archive -LiteralPath $binaryStage -DestinationPath $binaryZip -Force
Compress-Archive -LiteralPath $sourceStage -DestinationPath $sourceZip -Force
$checksumLines = foreach ($file in @((Join-Path $releasePath '화면지킴이.exe'),$binaryZip,$sourceZip)) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(),[IO.Path]::GetFileName($file)
}
[IO.File]::WriteAllLines((Join-Path $releasePath 'SHA256SUMS.txt'),[string[]]$checksumLines,(New-Object Text.UTF8Encoding($false)))
& (Join-Path $PSScriptRoot 'verify-package.ps1') -ReleaseDirectory $releasePath
Write-Output "공개용 배포 자료 생성 완료: $releasePath"
