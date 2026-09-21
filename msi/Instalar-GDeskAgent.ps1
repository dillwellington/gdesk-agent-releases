<#
  Chamado pelo MSI (ação personalizada, como SYSTEM). Baixa o GDeskAgent.exe
  da release do GitHub e roda o instalador que já existe no próprio agente
  (--instalar-elevado): copia o exe pra %ProgramData%\GDeskAgent, grava o
  appsettings.json, registra as tarefas agendadas, atalhos e a entrada em
  Programas e Recursos. Sai com código != 0 se algo falhar (o MSI reverte).
#>
param(
    [Parameter(Mandatory = $true)][string]$Token,
    [string]$ClienteId = "",
    [string]$SetorId = "",
    [string]$Patrimonio = "",
    [string]$Lacre = "",
    [string]$Url = "https://github.com/dillwellington/gdesk-agent-releases/releases/download/agente-latest/GDeskAgent.exe"
)
$ErrorActionPreference = "Stop"
$pasta = Join-Path $env:ProgramData "GDeskAgent"
New-Item -ItemType Directory -Force -Path $pasta | Out-Null
$exe = Join-Path $pasta "GDeskAgentInstalador.exe"

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = "SilentlyContinue"
    Invoke-WebRequest -Uri $Url -OutFile $exe -UseBasicParsing
    if ((Get-Item $exe).Length -lt 50MB) { throw "Arquivo baixado incompleto." }

    $args = @("--instalar-elevado", $Token)
    if ($ClienteId) { $args += @("--cliente-id", $ClienteId) }
    if ($SetorId)   { $args += @("--setor-id", $SetorId) }
    if ($Patrimonio){ $args += @("--patrimonio", $Patrimonio) }
    if ($Lacre)     { $args += @("--numero-lacre", $Lacre) }

    $p = Start-Process -FilePath $exe -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { exit $p.ExitCode }
}
finally {
    Remove-Item $exe -Force -ErrorAction SilentlyContinue
}
exit 0
