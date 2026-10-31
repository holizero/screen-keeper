[CmdletBinding()]
param(
    [ValidateSet('anycpu','x86','x64')][string]$Platform = 'anycpu',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'build')
)
$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compilerPath)) { $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (!(Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework C# 컴파일러를 찾지 못했습니다.' }
$buildPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $buildPath | Out-Null
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
$commonArguments = @('/nologo','/optimize+','/warn:4','/warnaserror+','/codepage:65001',"/platform:$Platform",'/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll',"/resource:$(Join-Path $PSScriptRoot 'LICENSE'),ScreenKeeper.LICENSE")
$appPath = Join-Path $buildPath '화면지킴이.exe'
& $compilerPath @commonArguments /target:winexe "/out:$appPath" "/win32manifest:$(Join-Path $PSScriptRoot 'src\app.manifest')" "/win32icon:$(Join-Path $PSScriptRoot 'assets\app.ico')" @sourceFiles
if ($LASTEXITCODE -ne 0) { throw '앱 빌드에 실패했습니다.' }
$testPath = Join-Path $buildPath 'ScreenKeeper.Tests.exe'
& $compilerPath @commonArguments /target:exe /main:ScreenKeeper.Tests.TestRunner "/out:$testPath" @sourceFiles (Join-Path $PSScriptRoot 'tests\TestRunner.cs')
if ($LASTEXITCODE -ne 0) { throw '테스트 빌드에 실패했습니다.' }
Write-Output "빌드 완료: $appPath"
