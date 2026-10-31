[CmdletBinding()]
param([switch]$Native, [ValidateSet('anycpu','x86','x64')][string]$Platform = 'anycpu', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$buildPath = Join-Path $PSScriptRoot "build\$Platform"
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Platform $Platform -OutputDirectory $buildPath }
$testExecutable = Join-Path $buildPath 'ScreenKeeper.Tests.exe'
if (!(Test-Path -LiteralPath $testExecutable)) { throw '테스트 실행 파일이 없습니다.' }
$testArguments = @()
if ($Native) { $testArguments += '--native' }
& $testExecutable @testArguments
if ($LASTEXITCODE -ne 0) { throw "검사에 실패했습니다. 종료 코드: $LASTEXITCODE" }
