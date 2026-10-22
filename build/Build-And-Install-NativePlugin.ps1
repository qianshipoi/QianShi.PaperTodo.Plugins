<#
.SYNOPSIS
Builds native PaperTodo plugins from src/ and installs them into a PaperTodo plugins folder.

.DESCRIPTION
Discovers every plugin under src\<PluginName>\ (a project with a sibling plugin.json), publishes it
in Release for win-x64, copies the manifest into the package, removes what the host already provides
(shared abstractions, PDB/XML, WebView2 loader), then installs the package to
<InstallTo>\plugins\<plugin id>\ while preserving that plugin's existing .runtime\ directory.

With no -Plugin it builds every discovered plugin, so adding a new plugin needs no script change.
PaperTodo cannot hot-replace a loaded native plugin: exit PaperTodo before installing.

.PARAMETER Plugin
One plugin to build: its folder name under src\, or a path to its .csproj. Defaults to every
plugin found under src\.

.PARAMETER PaperTodoRoot
PaperTodo checkout used for the host contract project (PaperTodo.Plugin.Abstractions).
Defaults to the sibling .\PaperTodo folder of this repository.

.PARAMETER InstallTo
Folder that holds the host's plugins\ directory. Defaults to <PaperTodoRoot>.
Point it at a build output (for example 输出\PaperTodo-v4.0.0-beta1) to deploy straight into a
locally built host.

.PARAMETER OutputDirectory
Used with -SkipInstall. Receives the packaged plugin instead of installing it.
Defaults to .\dist\<plugin id>.

.PARAMETER RuntimeIdentifier
.NET runtime identifier. Defaults to win-x64, which is what PaperTodo ships.

.PARAMETER SkipInstall
Only package the plugins. PaperTodo does not need to be closed and no plugins folder is touched.

.EXAMPLE
.\build\Build-And-Install-NativePlugin.ps1

.EXAMPLE
.\build\Build-And-Install-NativePlugin.ps1 -Plugin QianShi.PaperTodo.Archive

.EXAMPLE
.\build\Build-And-Install-NativePlugin.ps1 -InstallTo 'PaperTodo\输出\PaperTodo-v4.0.0-beta1'

.EXAMPLE
.\build\Build-And-Install-NativePlugin.ps1 -SkipInstall
#>
param(
    [string]$Plugin,
    [string]$PaperTodoRoot,
    [string]$InstallTo,
    [string]$OutputDirectory,
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory ".."))
$srcRoot = Join-Path $repoRoot "src"

function Resolve-FullPath {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$BaseDirectory
    )

    if ([System.IO.Path]::IsPathRooted($Value)) {
        return [System.IO.Path]::GetFullPath($Value)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $BaseDirectory $Value))
}

function Get-PluginProject {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$ManifestPath
    )

    $manifest = Get-Content -LiteralPath $ManifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
    $pluginId = [string]$manifest.id
    $entryName = [string]$manifest.entry
    if ([string]$manifest.kind -ine "native") {
        throw "$ManifestPath is not a native plugin manifest."
    }
    if ([string]::IsNullOrWhiteSpace($pluginId) -or $pluginId -notmatch '^[A-Za-z0-9._-]{3,120}$') {
        throw "$ManifestPath contains an invalid plugin id: '$pluginId'"
    }
    if ([string]::IsNullOrWhiteSpace($entryName) -or
        [System.IO.Path]::GetFileName($entryName) -ne $entryName -or
        [System.IO.Path]::GetExtension($entryName) -ine ".dll") {
        throw "$ManifestPath entry must be a DLL file name in the plugin root: '$entryName'"
    }

    [pscustomobject]@{
        ProjectPath  = $ProjectPath
        ManifestPath = $ManifestPath
        PluginId     = $pluginId
        EntryName    = $entryName
    }
}

# ---------------------------------------------------------------- discovery

if (-not (Test-Path -LiteralPath $srcRoot -PathType Container)) {
    throw "Plugin source folder not found: $srcRoot"
}

$candidates = @()
if ([string]::IsNullOrWhiteSpace($Plugin)) {
    foreach ($directory in Get-ChildItem -LiteralPath $srcRoot -Directory | Sort-Object Name) {
        $manifest = Join-Path $directory.FullName "plugin.json"
        $project = Get-ChildItem -LiteralPath $directory.FullName -Filter "*.csproj" -File |
            Select-Object -First 1
        if ((Test-Path -LiteralPath $manifest -PathType Leaf) -and $project) {
            $candidates += Get-PluginProject -ProjectPath $project.FullName -ManifestPath $manifest
        }
    }
}
else {
    # Accept a plugin folder name (resolved under src\), a path relative to the repository root, or
    # an absolute path to the project file.
    $resolved = Resolve-FullPath -Value $Plugin -BaseDirectory $repoRoot
    if (-not (Test-Path -LiteralPath $resolved)) {
        $resolved = Resolve-FullPath -Value $Plugin -BaseDirectory $srcRoot
    }
    if (Test-Path -LiteralPath $resolved -PathType Container) {
        $project = Get-ChildItem -LiteralPath $resolved -Filter "*.csproj" -File | Select-Object -First 1
        $resolved = $project.FullName
    }
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Plugin project not found: $Plugin"
    }
    $manifest = Join-Path (Split-Path -Parent $resolved) "plugin.json"
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        throw "Plugin manifest not found next to the project: $manifest"
    }
    $candidates += Get-PluginProject -ProjectPath $resolved -ManifestPath $manifest
}

if ($candidates.Count -eq 0) {
    throw "No plugin found under $srcRoot (a plugin is a project with a sibling plugin.json)."
}

# ---------------------------------------------------------------- locations

if ([string]::IsNullOrWhiteSpace($PaperTodoRoot)) {
    $PaperTodoRoot = Join-Path $repoRoot "PaperTodo"
}
$PaperTodoRoot = Resolve-FullPath -Value $PaperTodoRoot -BaseDirectory $repoRoot

# The plugin compiles against the host contract project, so the checkout must be present even when
# the build is only packaged and not installed.
$abstractionsProject = Join-Path $PaperTodoRoot "PaperTodo.Plugin.Abstractions\PaperTodo.Plugin.Abstractions.csproj"
if (-not (Test-Path -LiteralPath $abstractionsProject -PathType Leaf)) {
    $hint = ""
    if (Test-Path -LiteralPath (Join-Path $repoRoot ".gitmodules") -PathType Leaf) {
        $hint = "`nPaperTodo is a git submodule of this repository. Initialise it first:" +
            "`n    git submodule update --init --recursive"
    }
    throw "PaperTodo plugin contract project not found: $abstractionsProject$hint"
}

if (-not $SkipInstall) {
    if (Get-Process -Name "PaperTodo" -ErrorAction SilentlyContinue) {
        throw "Please exit PaperTodo before replacing a native plugin. Use -SkipInstall to only build a package."
    }

    if ([string]::IsNullOrWhiteSpace($InstallTo)) {
        $InstallTo = $PaperTodoRoot
    }
    $InstallTo = Resolve-FullPath -Value $InstallTo -BaseDirectory $repoRoot
    $pluginsRoot = [System.IO.Path]::GetFullPath((Join-Path $InstallTo "plugins"))
    New-Item -ItemType Directory -Force -Path $pluginsRoot | Out-Null
}

# ---------------------------------------------------------------- build loop

$sharedAssemblyNames = @(
    "PaperTodo.Plugin.Abstractions",
    "WinRT.Runtime",
    "Microsoft.Windows.SDK.NET",
    "Microsoft.Web.WebView2.Core",
    "Microsoft.Web.WebView2.Wpf",
    "Microsoft.Web.WebView2.WinForms"
)
$sharedFileNames = foreach ($name in $sharedAssemblyNames) {
    "$name.dll"
    "$name.pdb"
    "$name.xml"
}

$results = @()
foreach ($candidate in $candidates) {
    $stagingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) (
        "PaperTodo.NativePlugin-" + [System.Guid]::NewGuid().ToString("N"))
    $publishDirectory = Join-Path $stagingDirectory "publish"
    $packageDirectory = Join-Path $stagingDirectory "package"

    try {
        New-Item -ItemType Directory -Force -Path $publishDirectory, $packageDirectory | Out-Null

        Write-Host ("Publishing {0} ({1}, Release)…" -f $candidate.PluginId, $RuntimeIdentifier)
        & dotnet publish $candidate.ProjectPath `
            -c Release `
            -r $RuntimeIdentifier `
            --self-contained false `
            -o $publishDirectory `
            /p:DebugType=none `
            /p:DebugSymbols=false `
            /p:GenerateDocumentationFile=false
        if ($LASTEXITCODE -ne 0) {
            throw "Plugin build failed with exit code $LASTEXITCODE for $($candidate.PluginId)."
        }

        Get-ChildItem -LiteralPath $publishDirectory -Force |
            Copy-Item -Destination $packageDirectory -Recurse -Force
        Copy-Item -LiteralPath $candidate.ManifestPath `
            -Destination (Join-Path $packageDirectory "plugin.json") -Force

        # The host already loads these assemblies; a second copy makes the plugin fail to load.
        Get-ChildItem -LiteralPath $packageDirectory -File -Recurse -Force |
            Where-Object {
                $_.Extension -ieq ".pdb" -or
                $_.Name -ieq "WebView2Loader.dll" -or
                $sharedFileNames -contains $_.Name
            } |
            Remove-Item -Force

        Get-ChildItem -LiteralPath $packageDirectory -Filter "*.xml" -File -Recurse -Force |
            Where-Object {
                Test-Path -LiteralPath ([System.IO.Path]::ChangeExtension($_.FullName, ".dll"))
            } |
            Remove-Item -Force

        Get-ChildItem -LiteralPath $packageDirectory -Directory -Recurse -Force |
            Sort-Object FullName -Descending |
            Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } |
            Remove-Item -Force

        $requiredFiles = @(
            (Join-Path $packageDirectory $candidate.EntryName),
            (Join-Path $packageDirectory (
                [System.IO.Path]::GetFileNameWithoutExtension($candidate.EntryName) + ".deps.json")),
            (Join-Path $packageDirectory "plugin.json")
        )
        foreach ($requiredFile in $requiredFiles) {
            if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
                throw "Required plugin output is missing after cleanup: $requiredFile"
            }
        }

        if ($SkipInstall) {
            if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
                $destination = Join-Path $repoRoot ("dist\" + $candidate.PluginId)
            }
            elseif ($candidates.Count -gt 1) {
                $destination = Join-Path (
                    Resolve-FullPath -Value $OutputDirectory -BaseDirectory $repoRoot) $candidate.PluginId
            }
            else {
                $destination = Resolve-FullPath -Value $OutputDirectory -BaseDirectory $repoRoot
            }

            New-Item -ItemType Directory -Force -Path $destination | Out-Null
            Get-ChildItem -LiteralPath $destination -Force |
                Where-Object { $_.Name -ne ".runtime" } |
                Remove-Item -Recurse -Force
            Get-ChildItem -LiteralPath $packageDirectory -Force |
                Copy-Item -Destination $destination -Recurse -Force
            Write-Host ("Packaged {0} to {1}" -f $candidate.PluginId, $destination)
            $results += [pscustomobject]@{ PluginId = $candidate.PluginId; Destination = $destination }
            continue
        }

        $targetDirectory = [System.IO.Path]::GetFullPath((Join-Path $pluginsRoot $candidate.PluginId))
        $expectedPrefix = $pluginsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
            [System.IO.Path]::DirectorySeparatorChar
        if (-not $targetDirectory.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Plugin output path escaped the plugins directory: $targetDirectory"
        }

        New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
        Get-ChildItem -LiteralPath $targetDirectory -Force |
            Where-Object { $_.Name -ne ".runtime" } |
            Remove-Item -Recurse -Force
        Get-ChildItem -LiteralPath $packageDirectory -Force |
            Copy-Item -Destination $targetDirectory -Recurse -Force
        Write-Host ("Installed {0} to {1}" -f $candidate.PluginId, $targetDirectory)
        $results += [pscustomobject]@{ PluginId = $candidate.PluginId; Destination = $targetDirectory }
    }
    finally {
        if (Test-Path -LiteralPath $stagingDirectory) {
            Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host ""
Write-Host ("Plugins built: {0}" -f $results.Count)
foreach ($result in $results) {
    Write-Host ("  {0} -> {1}" -f $result.PluginId, $result.Destination)
}
