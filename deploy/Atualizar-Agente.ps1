<#
    Atualizar-Agente.ps1  --  atualiza em massa o GDesk Agente já instalado.

    Para máquinas com agente ANTIGO (anterior ao 1.8.0, que ainda não tinha
    atualização automática). Não precisa de token nem de nada por máquina:
    lê o token/cliente/setor/patrimônio/lacre do próprio appsettings.json
    já instalado e reinstala por cima com a versão mais nova publicada
    (release agente-latest do GitHub, conferida por SHA-256).

    - Máquina SEM agente instalado: não faz nada.
    - Agente já na versão mais nova (ou mais nova que a publicada): não faz nada.
    - Precisa rodar como administrador/SYSTEM.
    - Log: C:\ProgramData\GDeskAgent\atualizar-agente.log

    Como distribuir para muitas máquinas de uma vez (escolha um):
      * GPO (domínio): Configuração do Computador > Políticas > Configurações
        do Windows > Scripts > Inicialização > aba "Scripts do PowerShell" >
        Adicionar este .ps1. Roda no próximo boot de cada máquina (como SYSTEM).
      * Ferramenta de gerenciamento (PDQ, Intune, RMM...): rodar como SYSTEM.
      * Manual numa máquina: PowerShell como administrador:
          powershell -ExecutionPolicy Bypass -File .\Atualizar-Agente.ps1
#>
$ErrorActionPreference = 'Stop'
$pasta    = Join-Path $env:ProgramData 'GDeskAgent'
$exe      = Join-Path $pasta 'GDeskAgent.exe'
$config   = Join-Path $pasta 'appsettings.json'
$log      = Join-Path $pasta 'atualizar-agente.log'
$base     = 'https://github.com/dillwellington/gdesk-agent-releases/releases/download/agente-latest'

function Registrar([string]$mensagem) {
    try { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $mensagem" | Add-Content -Path $log -Encoding UTF8 } catch { }
}

function Versao([string]$texto) {
    $limpa = (($texto -split '[+ ]')[0]).Trim()
    $partes = @($limpa -split '\.' | ForEach-Object { [int]($_ -replace '\D', '0') })
    while ($partes.Count -lt 3) { $partes += 0 }
    return [version]::new($partes[0], $partes[1], $partes[2])
}

try {
    if (-not (Test-Path $config)) { exit 0 }   # agente não instalado nesta máquina

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    # O GitHub entrega o .json como application/octet-stream: no PowerShell 5.1
    # o Content vem como byte[] -- converte antes de ler o JSON.
    $bruto = (Invoke-WebRequest -Uri "$base/agente.json" -UseBasicParsing).Content
    if ($bruto -is [byte[]]) { $bruto = [Text.Encoding]::UTF8.GetString($bruto) }
    $manifesto = $bruto | ConvertFrom-Json
    $publicada = Versao $manifesto.versao
    $instalada = if (Test-Path $exe) { Versao (Get-Item $exe).VersionInfo.ProductVersion } else { [version]'0.0.0' }
    if ($instalada -ge $publicada) { exit 0 }

    Registrar "Atualizando agente $instalada -> $publicada..."
    $temp = Join-Path $env:TEMP "GDeskAgent-$($manifesto.versao).exe"
    Invoke-WebRequest -Uri "$base/GDeskAgent.exe" -OutFile $temp -UseBasicParsing
    $hash = (Get-FileHash $temp -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne ([string]$manifesto.sha256).ToLower()) {
        Remove-Item $temp -Force -ErrorAction SilentlyContinue
        throw "SHA-256 do download não confere ($hash) -- nada foi alterado."
    }

    $cfg = Get-Content $config -Raw | ConvertFrom-Json
    if (-not $cfg.AgentToken) { throw "appsettings.json sem AgentToken -- nada foi alterado." }
    $argumentos = "--instalar-elevado `"$($cfg.AgentToken)`""
    if ($cfg.ClienteId)   { $argumentos += " --cliente-id `"$($cfg.ClienteId)`"" }
    if ($cfg.SetorId)     { $argumentos += " --setor-id `"$($cfg.SetorId)`"" }
    if ($cfg.Patrimonio)  { $argumentos += " --patrimonio `"$($cfg.Patrimonio)`"" }
    if ($cfg.NumeroLacre) { $argumentos += " --numero-lacre `"$($cfg.NumeroLacre)`"" }

    $processo = Start-Process -FilePath $temp -ArgumentList $argumentos -Wait -PassThru -WindowStyle Hidden
    Remove-Item $temp -Force -ErrorAction SilentlyContinue
    # 0 = ok; 2 = instalou mas a 1ª sincronização falhou (a tarefa tenta de novo sozinha)
    if ($processo.ExitCode -eq 0 -or $processo.ExitCode -eq 2) {
        Registrar "Agente atualizado para $publicada (código $($processo.ExitCode))."
    } else {
        Registrar "Falha ao instalar a versão $publicada (código $($processo.ExitCode))."
    }
} catch {
    Registrar "ERRO: $($_.Exception.Message)"
}
exit 0
