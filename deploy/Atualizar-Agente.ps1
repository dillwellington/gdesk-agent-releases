<#
    Atualizar-Agente.ps1  --  atualiza em massa o GDesk Agente já instalado.

    Para máquinas com agente ANTIGO (anterior ao 1.8.0, que ainda não tinha
    atualização automática). Não precisa de token nem de nada por máquina:
    lê o token/cliente/setor/patrimônio/lacre do próprio appsettings.json
    já instalado e reinstala por cima com a versão mais nova publicada,
    conferida por SHA-256.

    ORIGEM DO INSTALADOR
    - Padrão: baixa do release agente-latest do GitHub.
    - Com -Origem \\servidor\pasta: usa o GDeskAgent.exe e o agente.json
      dessa pasta de rede (sem baixar nada). Se a pasta não estiver
      acessível ou estiver incompleta, cai de volta pro GitHub.
      A pasta precisa ter OS DOIS arquivos, baixados do mesmo release:
        GDeskAgent.exe  e  agente.json
      Permissão de leitura (compartilhamento E NTFS) para
      "Computadores do Domínio", porque o script roda como SYSTEM
      (acessa a rede com a conta do computador, não do usuário).
      Sugestão: \\SEU_DOMINIO\NETLOGON\GDeskAgent (já é legível pelos
      computadores do domínio).
      Ao publicar uma versão nova, troque os dois arquivos na pasta.

    - Máquina SEM agente instalado: só instala se receber -Token (token da
      empresa, em Minha Empresa > Token do Agente). Opcionais: -ClienteId e
      -SetorId (ids do GDesk) pra já cair no cliente/setor certos. Sem
      -Token, não faz nada nessas máquinas.
    - Agente já na versão mais nova (ou mais nova que a publicada): não faz nada.
    - Precisa rodar como administrador/SYSTEM.
    - Log: C:\ProgramData\GDeskAgent\atualizar-agente.log

    Como distribuir para muitas máquinas de uma vez (escolha um):
      * GPO (domínio): Configuração do Computador > Políticas > Configurações
        do Windows > Scripts > Inicialização > aba "Scripts do PowerShell" >
        Adicionar este .ps1. Roda no próximo boot de cada máquina (como SYSTEM).
        "Parâmetros do script", por exemplo:
          -Origem \\SEU_DOMINIO\NETLOGON\GDeskAgent -Token SEU_TOKEN [-ClienteId ID] [-SetorId ID]
      * Ferramenta de gerenciamento (PDQ, Intune, RMM...): rodar como SYSTEM.
      * Manual numa máquina: PowerShell como administrador:
          powershell -ExecutionPolicy Bypass -File .\Atualizar-Agente.ps1 -Origem \\servidor\pasta
#>
param(
    [string]$Token,
    [string]$ClienteId,
    [string]$SetorId,
    [string]$Origem
)
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
    $instalado = Test-Path $config
    if (-not $instalado -and -not $Token) { exit 0 }   # sem agente e sem token: nada a fazer

    # Origem: pasta de rede (se informada e acessível) ou GitHub.
    $usarRede = $false
    if ($Origem) {
        $jsonRede = Join-Path $Origem 'agente.json'
        $exeRede  = Join-Path $Origem 'GDeskAgent.exe'
        if ((Test-Path $jsonRede) -and (Test-Path $exeRede)) { $usarRede = $true }
        else {
            if (-not $instalado) { New-Item -ItemType Directory -Force -Path $pasta | Out-Null }
            Registrar "Pasta de rede '$Origem' inacessível ou sem GDeskAgent.exe/agente.json -- usando o GitHub."
        }
    }

    if ($usarRede) {
        $manifesto = Get-Content $jsonRede -Raw -Encoding UTF8 | ConvertFrom-Json
    } else {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        # O GitHub entrega o .json como application/octet-stream: no PowerShell 5.1
        # o Content vem como byte[] -- converte antes de ler o JSON.
        $bruto = (Invoke-WebRequest -Uri "$base/agente.json" -UseBasicParsing).Content
        if ($bruto -is [byte[]]) { $bruto = [Text.Encoding]::UTF8.GetString($bruto) }
        $manifesto = $bruto | ConvertFrom-Json
    }
    $publicada = Versao $manifesto.versao
    $instalada = if ($instalado -and (Test-Path $exe)) { Versao (Get-Item $exe).VersionInfo.ProductVersion } else { [version]'0.0.0' }
    if ($instalado -and $instalada -ge $publicada) { exit 0 }

    $de = if ($usarRede) { "rede ($Origem)" } else { 'GitHub' }
    if ($instalado) { Registrar "Atualizando agente $instalada -> $publicada (origem: $de)..." }
    else { New-Item -ItemType Directory -Force -Path $pasta | Out-Null; Registrar "Instalando agente $publicada (máquina sem agente, origem: $de)..." }
    $temp = Join-Path $env:TEMP "GDeskAgent-$($manifesto.versao).exe"
    if ($usarRede) { Copy-Item -Path $exeRede -Destination $temp -Force }
    else { Invoke-WebRequest -Uri "$base/GDeskAgent.exe" -OutFile $temp -UseBasicParsing }
    $hash = (Get-FileHash $temp -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne ([string]$manifesto.sha256).ToLower()) {
        Remove-Item $temp -Force -ErrorAction SilentlyContinue
        throw "SHA-256 do instalador ($de) não confere com o agente.json ($hash) -- nada foi alterado."
    }

    if ($instalado) {
        $cfg = Get-Content $config -Raw | ConvertFrom-Json
        if (-not $cfg.AgentToken) { throw "appsettings.json sem AgentToken -- nada foi alterado." }
    } else {
        # Instalação nova: dados vindos dos parâmetros do script.
        $cfg = [pscustomobject]@{ AgentToken = $Token; ClienteId = $ClienteId; SetorId = $SetorId; Patrimonio = $null; NumeroLacre = $null }
    }
    $argumentos = "--instalar-elevado `"$($cfg.AgentToken)`""
    if ($cfg.ClienteId)   { $argumentos += " --cliente-id `"$($cfg.ClienteId)`"" }
    if ($cfg.SetorId)     { $argumentos += " --setor-id `"$($cfg.SetorId)`"" }
    if ($cfg.Patrimonio)  { $argumentos += " --patrimonio `"$($cfg.Patrimonio)`"" }
    if ($cfg.NumeroLacre) { $argumentos += " --numero-lacre `"$($cfg.NumeroLacre)`"" }

    # Sem -Wait: ele espera também os processos FILHOS, e o instalador deixa o
    # ícone da bandeja (--bandeja) aberto -- o script ficaria parado pra sempre.
    # WaitForExit() espera só o instalador.
    $processo = Start-Process -FilePath $temp -ArgumentList $argumentos -PassThru -WindowStyle Hidden
    $null = $processo.Handle   # guarda o handle, senão o ExitCode pode vir vazio
    $processo.WaitForExit()
    Remove-Item $temp -Force -ErrorAction SilentlyContinue
    # 0 = ok; 2 = instalou mas a 1ª sincronização falhou (a tarefa tenta de novo sozinha)
    if ($processo.ExitCode -eq 0 -or $processo.ExitCode -eq 2) {
        Registrar "Agente $publicada instalado/atualizado (código $($processo.ExitCode))."
    } else {
        Registrar "Falha ao instalar a versão $publicada (código $($processo.ExitCode))."
    }
} catch {
    Registrar "ERRO: $($_.Exception.Message)"
}
exit 0
