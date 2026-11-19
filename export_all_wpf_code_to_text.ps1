param (
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [string]$OutputFile = "WPF_All_Code.txt"
)

# Resolve the project path
$ProjectPath = (Resolve-Path $ProjectPath).Path

# Put output file in the project root
$OutputPath = Join-Path $ProjectPath $OutputFile

# Folders we do NOT want to scan
$ExcludedDirectories = @(
    "bin",
    "obj",
    ".vs",
    ".git"
)

function Test-ExcludedPath {
    param ([string]$FullName)

    $relativePath = $FullName.Substring($ProjectPath.Length)

    foreach ($directory in $ExcludedDirectories) {
        if ($relativePath -match "[\\/]\Q$directory\E([\\/]|$)") {
            return $true
        }
    }

    return $false
}

# Find every .cs and .xaml file recursively
$SourceFiles = Get-ChildItem -Path $ProjectPath -Recurse -File |
    Where-Object {
        ($_.Extension -in ".cs", ".xaml") -and
        (-not (Test-ExcludedPath $_.FullName))
    } |
    Sort-Object FullName

# Start building output
$Builder = New-Object System.Text.StringBuilder

[void]$Builder.AppendLine("============================================================")
[void]$Builder.AppendLine("WPF APPLICATION SOURCE CODE")
[void]$Builder.AppendLine("============================================================")
[void]$Builder.AppendLine()
[void]$Builder.AppendLine("Project Root:")
[void]$Builder.AppendLine($ProjectPath)
[void]$Builder.AppendLine()
[void]$Builder.AppendLine("Files Included: $($SourceFiles.Count)")
[void]$Builder.AppendLine()


# ------------------------------------------------------------
# FILE LIST
# ------------------------------------------------------------

[void]$Builder.AppendLine("============================================================")
[void]$Builder.AppendLine("FILE LIST")
[void]$Builder.AppendLine("============================================================")
[void]$Builder.AppendLine()

foreach ($File in $SourceFiles) {
    $RelativePath = $File.FullName.Substring($ProjectPath.Length).TrimStart('\')
    [void]$Builder.AppendLine($RelativePath)
}

[void]$Builder.AppendLine()
[void]$Builder.AppendLine()


# ------------------------------------------------------------
# COMPLETE SOURCE CODE
# ------------------------------------------------------------

[void]$Builder.AppendLine("============================================================")
[void]$Builder.AppendLine("COMPLETE SOURCE CODE")
[void]$Builder.AppendLine("============================================================")

foreach ($File in $SourceFiles) {

    $RelativePath = $File.FullName.Substring($ProjectPath.Length).TrimStart('\')

    [void]$Builder.AppendLine()
    [void]$Builder.AppendLine()
    [void]$Builder.AppendLine("################################################################")
    [void]$Builder.AppendLine("FILE: $RelativePath")
    [void]$Builder.AppendLine("################################################################")
    [void]$Builder.AppendLine()

    try {
        $Content = Get-Content -Path $File.FullName -Raw -ErrorAction Stop
        [void]$Builder.AppendLine($Content)
    }
    catch {
        [void]$Builder.AppendLine("[ERROR READING FILE: $($_.Exception.Message)]")
    }

    [void]$Builder.AppendLine()
    [void]$Builder.AppendLine("################################################################")
    [void]$Builder.AppendLine("END FILE: $RelativePath")
    [void]$Builder.AppendLine("################################################################")
}


# ------------------------------------------------------------
# WRITE FILE
# ------------------------------------------------------------

$Builder.ToString() | Set-Content -Path $OutputPath -Encoding UTF8

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "WPF source export complete!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
Write-Host "Files exported: $($SourceFiles.Count)"
Write-Host ""
Write-Host "Output file:"
Write-Host $OutputPath -ForegroundColor Cyan
Write-Host ""