<#
.SYNOPSIS
    VDA Hub Server Controller Build & Publish Script
#>

param (
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "./publish"
)

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  VDA Hub Server Controller - Single File Exe Derleyici   " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

Write-Host "[1/3] Proje temizleniyor..." -ForegroundColor Yellow
dotnet clean -c $Configuration

Write-Host "[2/3] Bağımsız tek dosya (.exe) derleniyor ($Runtime)..." -ForegroundColor Yellow
dotnet publish -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $OutputDir

if ($LASTEXITCODE -eq 0) {
    Write-Host "[3/3] Derleme BAŞARILI!" -ForegroundColor Green
    Write-Host "Çıktı Dizini: $OutputDir" -ForegroundColor White
    Get-ChildItem -Path $OutputDir | Select-Object Name, @{Name="Boyut (MB)"; Expression={[math]::Round($_.Length / 1MB, 2)}}, LastWriteTime | Format-Table -AutoSize
} else {
    Write-Host "[HATA] Derleme başarısız oldu!" -ForegroundColor Red
}
