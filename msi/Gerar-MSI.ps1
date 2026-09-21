# Gera GDeskAgent.msi (precisa do .NET SDK 8; o WiX v4 é instalado como ferramenta do dotnet).
#   cd gdesk-agent\msi ; .\Gerar-MSI.ps1
$ErrorActionPreference = "Stop"
dotnet build .\GDeskAgent.wixproj -c Release
Write-Host "MSI gerado em: $PWD\bin\x64\Release\GDeskAgent.msi"
