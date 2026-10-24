<#
  Builds every Nas Reader package and, with -Publish, creates the GitHub release.

    .\release.ps1 -Version 1.2.0              # build into dist\
    .\release.ps1 -Version 1.2.0 -Publish     # build, then publish release v1.2.0

  Needs: .NET 10 SDK, Inno Setup 6, JDK 17+, Android SDK, GitHub CLI (gh) logged in.
  The Android signing key lives in keys\ (not in git): keep a copy somewhere safe,
  phones only accept updates signed with the same key.
#>
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [switch] $Publish,
    [string] $Notes
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

if (-not $env:JAVA_HOME) { $env:JAVA_HOME = 'C:\Program Files\Java\jdk-21.0.10' }
if (-not $env:GRADLE_USER_HOME) { $env:GRADLE_USER_HOME = 'D:\Gradle\home' }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 introuvable (winget install JRSoftware.InnoSetup).' }

Write-Host "== Windows $Version" -ForegroundColor Yellow
$csproj = Join-Path $root 'src\NasReader\NasReader.csproj'
(Get-Content $csproj -Raw) -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>" | Set-Content $csproj -Encoding utf8 -NoNewline
dotnet publish $csproj -c Release -o $dist --nologo -v q
if ($LASTEXITCODE) { throw 'dotnet publish a échoué' }
Remove-Item "$dist\*.xml", "$dist\*.pdb" -ErrorAction SilentlyContinue
Copy-Item "$dist\NasReader.exe" "$dist\NasReader-$Version-portable.exe" -Force

& $iscc /Q "/DAppVersion=$Version" (Join-Path $root 'installer\NasReader.iss')
if ($LASTEXITCODE) { throw 'Inno Setup a échoué' }

Write-Host "== Android $Version" -ForegroundColor Yellow
Push-Location (Join-Path $root 'android')
try {
    .\gradlew.bat assembleRelease -q "-PappVersion=$Version"
    if ($LASTEXITCODE) { throw 'Gradle a échoué' }
} finally { Pop-Location }
Copy-Item (Join-Path $root 'android\app\build\outputs\apk\release\app-release.apk') "$dist\NasReader-$Version.apk" -Force

$assets = @("$dist\NasReader-Setup-$Version.exe", "$dist\NasReader-$Version.apk", "$dist\NasReader-$Version-portable.exe")
$assets | ForEach-Object { '{0,8:N1} Mo  {1}' -f ((Get-Item $_).Length / 1MB), (Split-Path $_ -Leaf) }

if ($Publish) {
    Write-Host "== GitHub release v$Version" -ForegroundColor Yellow
    $notesFile = Join-Path $root "releases\v$Version.md"
    if ($Notes) { gh release create "v$Version" @assets --title "Nas Reader $Version" --notes $Notes }
    elseif (Test-Path $notesFile) { gh release create "v$Version" @assets --title "Nas Reader $Version" --notes-file $notesFile }
    else { gh release create "v$Version" @assets --title "Nas Reader $Version" --generate-notes }
    if ($LASTEXITCODE) { throw 'gh release a échoué' }
}
