param(
    [string]$DotNet = "dotnet",
    [string]$InnoCompiler = "ISCC.exe",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "Launcher.UI/Launcher.UI.csproj"
$version = ([xml](Get-Content -Raw -LiteralPath $project)).Project.PropertyGroup.Version
$publish = Join-Path $root "obj/windows-publish"
$artifacts = Join-Path $root "artifacts"
New-Item -ItemType Directory -Force $artifacts | Out-Null

$arguments = @("publish", $project, "-c", "Release", "-r", "win-x64", "--self-contained", "true",
    "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:DebugType=None", "-o", $publish)
if ($NoRestore) { $arguments += "--no-restore" }
& $DotNet @arguments
if ($LASTEXITCODE -ne 0) { throw ".NET publish failed ($LASTEXITCODE)." }

Copy-Item -LiteralPath (Join-Path $publish "Launcher.UI.exe") -Destination (Join-Path $artifacts "AwoLauncher.exe") -Force
Copy-Item -LiteralPath (Join-Path $publish "appsettings.json") -Destination $artifacts -Force
& $InnoCompiler "/DAppVersion=$version" "/DPublishDir=$artifacts" (Join-Path $PSScriptRoot "AwoLauncher.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed ($LASTEXITCODE)." }

$checksums = foreach ($file in @("AwoLauncher.exe", "AwoLauncher-Setup.exe")) {
    $hash = Get-FileHash -LiteralPath (Join-Path $artifacts $file) -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $file"
}
$checksums | Set-Content -LiteralPath (Join-Path $artifacts "SHA256SUMS.txt") -Encoding ascii
