# Builds the Total Commander auto-install package.
#
# Output: dist\TotalGram-<version>.zip — Total Commander offers to install any
# archive it can open that holds pluginst.inf in its root, so the extension is
# just .zip (see https://www.ghisler.ch/wiki/index.php/Plugins_Automated_Installation).
#
# Version is derived from the build time: yy.MM.dd.HHmm

$ErrorActionPreference = 'Stop'

$now = Get-Date
$version = $now.ToString('yy.MM.dd.HHmm')      # shown to the user, e.g. 26.09.24.1932
$numericVersion = $now.ToString('yy.M.d.HHmm') # assembly metadata rejects leading zeros

$root = $PSScriptRoot
$project = Join-Path $root 'TotalGram\TotalGram.csproj'
$distDir = Join-Path $root 'dist'
$stageDir = Join-Path $distDir 'package'
$releaseDir = Join-Path $distDir 'release'

Write-Host "TotalGram $version"

# The changelog travels inside the package, so this build has to be in it already.
# Entries are written under "## Unreleased"; the version is only known here.
$changelog = Join-Path $root 'TotalGram\CHANGELOG.md'
$text = [System.IO.File]::ReadAllText($changelog)
if ($text -match '(?m)^## Unreleased[ \t]*$') {
    $text = $text -replace '(?m)^## Unreleased[ \t]*$', "## $version"
    [System.IO.File]::WriteAllText($changelog, $text, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "changelog: Unreleased -> $version"
} elseif ($text -notmatch ('(?m)^## ' + [regex]::Escape($version) + '$')) {
    Write-Warning "CHANGELOG.md has no '## Unreleased' section: this build is not described in it"
}

# vswhere lives outside PATH on some machines, and the native linker is found through it
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
if (Test-Path $vswhere) { $env:PATH = "$vswhere;$env:PATH" }

foreach ($rid in @('win-x86', 'win-x64')) {
    Write-Host "publishing $rid..."
    dotnet publish $project -c Release -r $rid --self-contained `
        -p:Version=$numericVersion `
        -p:FileVersion=$numericVersion `
        -p:InformationalVersion=$version `
        | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }
}

$nativeX86 = Join-Path $root 'TotalGram\bin\Release\net10.0-windows\win-x86\native\TotalGram.dll'
$nativeX64 = Join-Path $root 'TotalGram\bin\Release\net10.0-windows\win-x64\native\TotalGram.dll'

Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

# a 64-bit plugin must sit next to the 32-bit one under the same name plus "64"
Copy-Item $nativeX86 (Join-Path $stageDir 'TotalGram.wfx')
Copy-Item $nativeX64 (Join-Path $stageDir 'TotalGram.wfx64')
Copy-Item (Join-Path $root 'TotalGram\README.md') (Join-Path $stageDir 'README.md')
Copy-Item (Join-Path $root 'TotalGram\CHANGELOG.md') (Join-Path $stageDir 'CHANGELOG.md')

$inf = @"
[plugininstall]
description=TotalGram - browse Telegram chats as a file system
descriptionRUS=TotalGram - чаты Telegram как файловая система
version=$version
type=wfx
file=TotalGram.wfx
defaultdir=TotalGram
"@ -replace "`r?`n", "`r`n"

# Total Commander reads this INI through the ANSI codepage, so UTF-8 would turn the
# Russian description into mojibake in the install dialog. descriptionRUS is only
# ever read by a Russian Total Commander, so codepage 1251 is the right target.
[System.Text.Encoding]::RegisterProvider([System.Text.CodePagesEncodingProvider]::Instance)
[System.IO.File]::WriteAllText(
    (Join-Path $stageDir 'pluginst.inf'),
    $inf,
    [System.Text.Encoding]::GetEncoding(1251))

Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = Join-Path $distDir "TotalGram-$version.zip"
Remove-Item $package -Force -ErrorAction SilentlyContinue
# includeBaseDirectory must stay false, otherwise pluginst.inf ends up in a subfolder
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stageDir, $package, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# loose binaries for manual installation and for the development loop
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
foreach ($pair in @(@{ From = $nativeX86; To = 'TotalGram.wfx' }, @{ From = $nativeX64; To = 'TotalGram.wfx64' })) {
    $target = Join-Path $releaseDir $pair.To
    try { Copy-Item $pair.From $target -Force }
    catch { Write-Warning "cannot update $target (Total Commander may be running): $($_.Exception.Message)" }
}

Write-Host ""
Write-Host "package: $package"
[System.IO.Compression.ZipFile]::OpenRead($package).Entries |
    ForEach-Object { Write-Host ("  {0,-20} {1,10:N0} bytes" -f $_.Name, $_.Length) }
