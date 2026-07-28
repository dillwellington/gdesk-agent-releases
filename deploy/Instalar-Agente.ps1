<#
    Instalar-Agente.ps1

    Script de instalação do Agente GDesk, pensado para rodar via GPO
    (Configuração do Computador > Políticas > Configurações do Windows >
    Scripts > Inicialização), atribuído à OU com os computadores desejados.

    O que ele faz:
      1. Copia o agente (exe publicado + appsettings.json) de um
         compartilhamento de rede para C:\Program Files\GDeskAgent
      2. Cria/atualiza uma Tarefa Agendada que roda o agente a cada X
         horas, como SYSTEM (não depende de nenhum usuário logado)
      3. Cria um atalho no Menu Iniciar ("Abrir Chamado GDesk") que
         qualquer usuário logado pode clicar para abrir o portal e
         registrar um chamado

    Como usar:
      1. Compile o agente (dotnet publish -- veja README.md) e copie a
         pasta publicada inteira (GDeskAgent.exe + appsettings.json, já
         com o AgentToken da empresa preenchido) para um compartilhamento
         de rede legível por todos os computadores do domínio, ex.:
           \\SEUSERVIDOR\GDeskAgent$\
      2. Ajuste o parâmetro -CaminhoOrigem abaixo (ou passe via GPO) para
         esse caminho
      3. No GPO: cole este .ps1 na pasta de scripts do próprio GPO
         (Editor de Gerenciamento de Política de Grupo > Configuração do
         Computador > Scripts > Inicialização > Adicionar), e associe o
         GPO à OU com os computadores alvo
      4. Nas máquinas alvo: gpupdate /force (ou aguardar o próximo reboot,
         já que scripts de inicialização de computador rodam no boot)

    Observação: como alternativa a "Startup Script", também é possível
    distribuir via Group Policy Preferences > Scheduled Tasks apontando
    direto para este .ps1 (equivalente, só muda onde o GPO registra a
    execução).
#>

param(
    # Por padrão, instala a partir da própria pasta onde este script está
    # (funciona tanto para o download manual -- exe/appsettings.json/este
    # script todos juntos na mesma pasta extraída do zip -- quanto para
    # uma pasta de rede no cenário de GPO, se for essa a pasta escolhida
    # para guardar o instalador). Só use -CaminhoOrigem explicitamente se
    # quiser instalar a partir de um lugar diferente de onde este .ps1 está.
    [string]$CaminhoOrigem = $PSScriptRoot,
    [string]$CaminhoDestino = "$env:ProgramFiles\GDeskAgent",
    [int]$IntervaloHoras = 6
)

$ErrorActionPreference = "Stop"

function Log($mensagem) {
    Write-Output "[Instalar-Agente] $mensagem"
}

# 1. Copia os arquivos (sempre sobrescreve, para pegar atualizações de versão do agente)
Log "Copiando agente de '$CaminhoOrigem' para '$CaminhoDestino'..."
New-Item -ItemType Directory -Force -Path $CaminhoDestino | Out-Null
Copy-Item -Path (Join-Path $CaminhoOrigem "*") -Destination $CaminhoDestino -Recurse -Force

$exePath = Join-Path $CaminhoDestino "GDeskAgent.exe"
if (-not (Test-Path $exePath)) {
    throw "GDeskAgent.exe não encontrado em '$CaminhoDestino' após a cópia -- verifique se '$CaminhoOrigem' contém o publish do agente."
}

# 2. Tarefa agendada de sincronização periódica (roda como SYSTEM, sem depender de usuário logado)
$nomeTarefaSync = "GDesk Agente - Sincronizacao"
Log "Registrando tarefa agendada '$nomeTarefaSync' (repete a cada $IntervaloHoras horas)..."

$acao = New-ScheduledTaskAction -Execute $exePath
# RepetitionDuration precisa de um valor válido para o Agendador de Tarefas
# (TimeSpan.MaxValue gera uma duração XML fora do intervalo aceito e o
# Register-ScheduledTask falha com "valor formatado incorretamente ou fora
# do intervalo") -- 10 anos já cobre "para sempre" na prática.
$gatilho = New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Hours $IntervaloHoras) `
    -RepetitionDuration (New-TimeSpan -Days 3650)
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$configuracoes = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopOnIdleEnd `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 10)

Unregister-ScheduledTask -TaskName $nomeTarefaSync -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $nomeTarefaSync -Action $acao -Trigger $gatilho `
    -Principal $principal -Settings $configuracoes -Force | Out-Null

# Roda uma vez imediatamente, para não esperar o primeiro intervalo completo
Start-ScheduledTask -TaskName $nomeTarefaSync

# 3. Atalho "Abrir Chamado GDesk" no Menu Iniciar (visível para todos os usuários da máquina)
$pastaMenuIniciar = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs"
$caminhoAtalho = Join-Path $pastaMenuIniciar "Abrir Chamado GDesk.lnk"
Log "Criando atalho '$caminhoAtalho'..."

$shell = New-Object -ComObject WScript.Shell
$atalho = $shell.CreateShortcut($caminhoAtalho)
$atalho.TargetPath = $exePath
$atalho.Arguments = "--abrir-chamado"
$atalho.Description = "Fazer login e abrir o portal do GDesk para registrar ou acompanhar chamados"
$atalho.WorkingDirectory = $CaminhoDestino
$atalho.Save()

Log "Instalação concluída com sucesso."
