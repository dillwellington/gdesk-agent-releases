using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace GDeskAgent;

/// <summary>
/// Faz o GDeskAgent.exe se instalar sozinho na máquina: copia para
/// %ProgramData%\GDeskAgent, grava o appsettings.json com o token (e
/// Setor/Subsetor) colados pelo usuário (ver SetupForm), registra a
/// Tarefa Agendada de sincronização periódica e cria os atalhos "GDesk
/// Agente" no Menu Iniciar e na Área de Trabalho (abrem o Painel -- ver
/// CriarAtalho/PainelForm.cs). Tudo isso exige administrador -- por isso o fluxo é sempre:
/// clique do usuário (sem privilégio) -> relança a si mesmo elevado (UAC)
/// -> instância elevada faz o trabalho de verdade. A desinstalação
/// (ExecutarDesinstalacaoElevada) segue exatamente o mesmo padrão de
/// reentrada elevada, na direção contrária.
/// </summary>
public static class SelfInstaller
{
    private const string NomeTarefaAgendada = "GDesk Agente - Sincronizacao";
    private const string NomeTarefaBandeja = "GDesk Agente - Bandeja";

    // Chave onde o Painel de Controle > Programas e Recursos procura os
    // programas instalados na máquina -- criar uma entrada aqui é o que
    // faz "GDesk Agente" aparecer nessa lista, com um botão Desinstalar
    // de verdade (ver RegistrarNoPainelDeControle / RemoverDoPainelDeControle).
    private const string ChaveUninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GDeskAgent";

    /// <summary>
    /// Chamado pela tela de instalação (SetupForm), ainda sem privilégio de
    /// administrador: relança o próprio .exe com "--instalar-elevado
    /// TOKEN" (mais os argumentos opcionais de cliente/patrimônio/lacre,
    /// se houver -- ver MontarArgumentosInstalacao) e pede elevação via
    /// UAC -- a instância nova é que faz a instalação de verdade (ver
    /// ExecutarInstalacaoElevada). clienteId, patrimonio e numeroLacre
    /// tipicamente vêm de ConfiguracaoEmbutida (download personalizado
    /// por cliente) e/ou do que a pessoa digitou na tela, quando
    /// Cliente.agente_patrimonio_obrigatorio/agente_numero_lacre_obrigatorio
    /// exigem isso.
    /// </summary>
    /// <summary>
    /// Devolve true se a instalação local E a primeira sincronização
    /// deram certo; false se a instalação local funcionou mas a primeira
    /// sincronização falhou (rede, backend fora do ar, etc. -- ver
    /// SincronizarAgora/ExecutarInstalacaoElevada). Nesse segundo caso não
    /// é um erro fatal: a Tarefa Agendada tenta de novo sozinha a cada 6
    /// horas. Lança exceção só quando a instalação em si falha de
    /// verdade (token inválido, UAC recusado, etc.) -- ver SetupForm.cs
    /// pra como cada caso vira uma mensagem diferente na tela.
    /// </summary>
    public static bool InstalarComElevacao(string token, string? clienteId = null, string? patrimonio = null, string? numeroLacre = null, string? setorId = null, string? adotarRecursoId = null)
    {
        // Valida o token ANTES de pedir elevação de administrador: evita
        // incomodar o usuário com a janela do UAC quando o token já está
        // claramente errado, e permite mostrar o motivo exato na tela do
        // SetupForm (a instância elevada roda em outro processo e, como
        // abre via UAC/ShellExecute, não dá pra capturar a saída dela).
        var (valido, mensagemValidacao) = ValidarToken(token);
        if (!valido)
        {
            throw new Exception(mensagemValidacao);
        }

        // Environment.ProcessPath (.NET 6+), não
        // Process.GetCurrentProcess().MainModule!.FileName! -- MainModule
        // lança BadImageFormatException (0x8007000B) em apps publicados
        // como single-file (ver ConfiguracaoEmbutida.cs, mesmo problema
        // achado lá primeiro): ele tenta inspecionar o módulo como um PE
        // "normal" pra montar FileVersionInfo etc., e a estrutura de
        // bundle do single-file não bate com o que ele espera.
        // ProcessPath só devolve o caminho do executável que iniciou o
        // processo, sem inspecionar nada.
        var exeAtual = Environment.ProcessPath!;
        var psi = new ProcessStartInfo
        {
            FileName = exeAtual,
            Arguments = MontarArgumentosInstalacao(token, clienteId, patrimonio, numeroLacre, setorId, adotarRecursoId, Environment.ProcessId),
            UseShellExecute = true,
            Verb = "runas",
        };

        Process processo;
        try
        {
            processo = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED -- usuário clicou "Não" na janela do UAC.
            throw new Exception("Instalação cancelada: é preciso aceitar a permissão de administrador para instalar.");
        }

        processo.WaitForExit();

        // Código 2 = convenção própria (ver Program.cs, tratamento de
        // "--instalar-elevado"): instalação local OK, mas a primeira
        // sincronização falhou -- não é motivo pra tratar como erro, só
        // pra avisar diferente na tela (ver SetupForm.cs).
        if (processo.ExitCode == 2)
        {
            return false;
        }
        if (processo.ExitCode != 0)
        {
            throw new Exception($"A instalação falhou (código {processo.ExitCode}). Confira o token e tente de novo.");
        }
        return true;
    }

    /// <summary>
    /// Monta os argumentos de linha de comando de "--instalar-elevado",
    /// incluindo os opcionais "--cliente-id", "--patrimonio",
    /// "--numero-lacre" e "--setor-id" só quando fornecidos -- ver
    /// Program.cs pro parser correspondente. Cada valor vai entre aspas
    /// (podem ter espaço, ex.: "Sala 3 - Recepção").
    /// </summary>
    private static string MontarArgumentosInstalacao(string token, string? clienteId, string? patrimonio, string? numeroLacre, string? setorId, string? adotarRecursoId = null, int? pidPai = null)
    {
        var argumentos = $"--instalar-elevado \"{token}\"";
        if (!string.IsNullOrWhiteSpace(clienteId)) argumentos += $" --cliente-id \"{clienteId}\"";
        if (!string.IsNullOrWhiteSpace(patrimonio)) argumentos += $" --patrimonio \"{patrimonio}\"";
        if (!string.IsNullOrWhiteSpace(numeroLacre)) argumentos += $" --numero-lacre \"{numeroLacre}\"";
        if (!string.IsNullOrWhiteSpace(setorId)) argumentos += $" --setor-id \"{setorId}\"";
        if (!string.IsNullOrWhiteSpace(adotarRecursoId)) argumentos += $" --adotar-recurso-id \"{adotarRecursoId}\"";
        // Id deste processo (o que está esperando a instalação elevada
        // terminar, ver InstalarComElevacao/WaitForExit) -- passado pra
        // EncerrarOutrosProcessosEmExecucao nunca matar ele mesmo (ver
        // comentário lá: sem isso, ao reinstalar rodando um .exe baixado
        // de outro lugar, o processo que mostra a tela SetupForm também
        // se chama "GDeskAgent" e acabava sendo morto pelo próprio
        // processo elevado que ele lançou, fechando a janela sem
        // nenhuma mensagem).
        if (pidPai.HasValue) argumentos += $" --pid-pai {pidPai.Value}";
        return argumentos;
    }

    /// <summary>
    /// Já rodando elevado (reentrada via "--instalar-elevado TOKEN [...]",
    /// vinda do UAC ou de uma Tarefa/Script de GPO que já roda como
    /// SYSTEM): faz a instalação de verdade. Seguro de rodar mais de uma
    /// vez -- sempre sobrescreve o que já existia.
    /// </summary>
    /// <summary>Devolve true se a primeira sincronização deu certo (ver InstalarComElevacao).</summary>
    public static bool ExecutarInstalacaoElevada(string token, string? clienteId = null, string? patrimonio = null, string? numeroLacre = null, string? setorId = null, string? adotarRecursoId = null, int? pidPai = null)
    {
        // Revalida aqui também (mesma checagem de InstalarComElevacao):
        // esta função também é chamada diretamente, sem passar pela tela
        // do usuário, quando o instalador roda via GPO/Script de
        // Inicialização com "--instalar-elevado TOKEN" -- sem isso, um
        // token errado configurado ali instalaria o agente do mesmo jeito
        // e só falharia silenciosamente na primeira sincronização.
        var (valido, mensagemValidacao) = ValidarToken(token);
        if (!valido)
        {
            throw new Exception(mensagemValidacao);
        }

        // Reinstalação/atualização numa máquina que já tem o agente
        // rodando: o ícone da bandeja (GDeskAgent.exe --bandeja) fica com
        // o .exe antigo aberto, e o Windows recusa sobrescrever um
        // executável em uso mesmo com admin (UnauthorizedAccessException
        // em File.Copy, mais abaixo) -- só aparecia como "instalação
        // falhou (código 1)" genérico pra quem clicava Instalar, sem
        // pista nenhuma da causa real. Mesma função usada em
        // ExecutarDesinstalacaoElevada; nunca mata o próprio processo
        // (o que está rodando esta instalação).
        EncerrarOutrosProcessosEmExecucao(pidPai);

        Directory.CreateDirectory(Instalacao.Pasta);

        // Pasta de logs: o ícone da bandeja roda como usuário comum e
        // precisa gravar aqui (S-1-5-32-545 = grupo Users, em qualquer idioma do Windows).
        Directory.CreateDirectory(LogLocal.PastaLogs);
        ExecutarComando("icacls.exe", $"\"{LogLocal.PastaLogs}\" /grant *S-1-5-32-545:(OI)(CI)M");

        // Environment.ProcessPath (.NET 6+), não
        // Process.GetCurrentProcess().MainModule!.FileName! -- MainModule
        // lança BadImageFormatException (0x8007000B) em apps publicados
        // como single-file (ver ConfiguracaoEmbutida.cs, mesmo problema
        // achado lá primeiro): ele tenta inspecionar o módulo como um PE
        // "normal" pra montar FileVersionInfo etc., e a estrutura de
        // bundle do single-file não bate com o que ele espera.
        // ProcessPath só devolve o caminho do executável que iniciou o
        // processo, sem inspecionar nada.
        var exeAtual = Environment.ProcessPath!;
        if (!string.Equals(Path.GetFullPath(exeAtual), Path.GetFullPath(Instalacao.CaminhoExe), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(exeAtual, Instalacao.CaminhoExe, overwrite: true);
        }

        var configJson = JsonSerializer.Serialize(
            new
            {
                ApiBaseUrl = "https://gdesk-backend.onrender.com",
                AgentToken = token,
                PortalUrl = "https://gdeskapp.com.br",
                IntervalMinutes = 360,
                ClienteId = clienteId,
                Patrimonio = patrimonio,
                NumeroLacre = numeroLacre,
                SetorId = setorId,
            },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Instalacao.CaminhoConfig, configJson);

        RegistrarTarefaAgendada();
        RegistrarTarefaBandeja();
        CriarAtalhoMenuIniciar();
        CriarAtalhoAreaTrabalho();
        RegistrarNoPainelDeControle();
        var sincronizouComSucesso = SincronizarAgora(adotarRecursoId);
        IniciarBandejaAgora();
        return sincronizouComSucesso;
    }

    /// <summary>
    /// Cria a entrada em Painel de Controle > Programas e Recursos
    /// ("Desinstalar ou alterar um programa"). UninstallString aponta pro
    /// próprio .exe instalado com "--desinstalar" -- é o mesmo binário que
    /// clicar em "Desinstalar" ali executa; ver Program.cs e
    /// SolicitarDesinstalacaoComElevacao/ExecutarDesinstalacaoElevada.
    /// </summary>
    private static void RegistrarNoPainelDeControle()
    {
        using var chave = Registry.LocalMachine.CreateSubKey(ChaveUninstall);
        chave.SetValue("DisplayName", "GDesk Agente");
        chave.SetValue("DisplayVersion", InventoryCollector.VersaoAgente);
        chave.SetValue("Publisher", "GD Soluções");
        chave.SetValue("DisplayIcon", Instalacao.CaminhoExe);
        chave.SetValue("InstallLocation", Instalacao.Pasta);
        chave.SetValue("UninstallString", $"\"{Instalacao.CaminhoExe}\" --desinstalar");
        chave.SetValue("NoModify", 1, RegistryValueKind.DWord);
        chave.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Chama GET /agente/validar-token pra confirmar que o token digitado
    /// existe e pertence a uma empresa ativa, sem gravar nada -- usa um
    /// AgentConfig "solto" (só com o token), já que ainda não escrevemos
    /// o appsettings.json de verdade neste ponto.
    /// </summary>
    private static (bool valido, string mensagem) ValidarToken(string token)
    {
        var configValidacao = new AgentConfig { AgentToken = token };
        return new ApiClient(configValidacao).ValidarTokenAsync().GetAwaiter().GetResult();
    }

    private static void RegistrarTarefaAgendada()
    {
        // Remove uma tarefa anterior primeiro (reinstalação/atualização de
        // versão) -- schtasks /Create sozinho falharia se já existisse.
        ExecutarComando("schtasks.exe", $"/Delete /TN \"{NomeTarefaAgendada}\" /F");

        var horaAtual = DateTime.Now.ToString("HH:mm");
        var argumentos =
            $"/Create /TN \"{NomeTarefaAgendada}\" /TR \"\\\"{Instalacao.CaminhoExe}\\\"\" " +
            $"/SC HOURLY /MO 6 /ST {horaAtual} /RU SYSTEM /RL HIGHEST /F";
        ExecutarComando("schtasks.exe", argumentos);
    }

    /// <summary>
    /// Ícone fixo na bandeja (perto do relógio) pra abrir chamado ou
    /// forçar uma sincronização rapidamente -- roda como o próprio usuário
    /// que logar (não SYSTEM, precisa de sessão interativa pra aparecer na
    /// tela), ao contrário da Tarefa de sincronização. "/RL LIMITED" evita
    /// que o Windows peça elevação toda vez que a tarefa disparar --
    /// mostrar um ícone na bandeja não precisa de privilégio nenhum.
    /// </summary>
    private static void RegistrarTarefaBandeja()
    {
        ExecutarComando("schtasks.exe", $"/Delete /TN \"{NomeTarefaBandeja}\" /F");

        var argumentos =
            $"/Create /TN \"{NomeTarefaBandeja}\" /TR \"\\\"{Instalacao.CaminhoExe}\\\" --bandeja\" " +
            $"/SC ONLOGON /RL LIMITED /F";
        ExecutarComando("schtasks.exe", argumentos);
    }

    /// <summary>
    /// Mostra o ícone na bandeja imediatamente após instalar, sem esperar
    /// o usuário fazer logoff/login de novo pra Tarefa "ONLOGON" disparar
    /// sozinha. Não é crítico: se falhar por algum motivo, o ícone aparece
    /// de qualquer forma no próximo login.
    ///
    /// Observação: como ExecutarInstalacaoElevada roda elevado (UAC ou
    /// SYSTEM via GPO), esta primeira instância da bandeja também sobe
    /// elevada -- não afeta o funcionamento, mas é só desta vez; toda
    /// execução seguinte (via a Tarefa Agendada, "/RL LIMITED") já roda
    /// normal, sem privilégio de administrador. Se a instalação veio de
    /// um script/GPO rodando como SYSTEM (sem sessão interativa), este
    /// Process.Start simplesmente não aparece pra ninguém -- o ícone
    /// mesmo assim vai aparecer no primeiro login real do usuário.
    /// </summary>
    private static void IniciarBandejaAgora()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Instalacao.CaminhoExe, "--bandeja") { UseShellExecute = true });
        }
        catch
        {
            // Não crítico -- ver comentário acima.
        }
    }

    private const string NomeAtalho = "GDesk Agente.lnk";

    /// <summary>
    /// Atalho no Menu Iniciar apontando pro Painel (--painel: mostra Setor/
    /// Subsetor e o botão "Abrir chamado" -- ver PainelForm.cs), não mais
    /// direto pro navegador -- assim a pessoa acha um jeito só de abrir
    /// chamado ou conferir o setor, em vez de dois atalhos parecidos.
    /// </summary>
    private static void CriarAtalhoMenuIniciar()
    {
        var pastaMenuIniciar = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        CriarAtalho(Path.Combine(pastaMenuIniciar, NomeAtalho));
    }

    /// <summary>
    /// Mesmo atalho, também na Área de Trabalho de todo mundo que usar
    /// esta máquina (CommonDesktopDirectory) -- pedido explícito: se o
    /// ícone da bandeja for fechado, ainda tem que dar pra achar o painel
    /// fácil, sem precisar procurar no Menu Iniciar.
    /// </summary>
    private static void CriarAtalhoAreaTrabalho()
    {
        var pastaAreaTrabalho = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        CriarAtalho(Path.Combine(pastaAreaTrabalho, NomeAtalho));
    }

    /// <summary>
    /// WScript.Shell via COM tardio (sem precisar de nenhum pacote NuGet
    /// extra) -- é a forma clássica e mais simples de criar um atalho
    /// .lnk a partir de C#. Se o Windows Script Host estiver desabilitado
    /// (bem raro, e normalmente só em máquinas com políticas de segurança
    /// bem restritivas), só não cria o atalho; isso não é crítico o
    /// suficiente pra falhar a instalação inteira.
    /// </summary>
    private static void CriarAtalho(string caminhoAtalho)
    {
        var tipoShell = Type.GetTypeFromProgID("WScript.Shell");
        if (tipoShell == null) return;

        dynamic shell = Activator.CreateInstance(tipoShell)!;
        try
        {
            dynamic atalho = shell.CreateShortcut(caminhoAtalho);
            atalho.TargetPath = Instalacao.CaminhoExe;
            atalho.Arguments = "--painel";
            atalho.Description = "Abrir o painel do Agente GDesk (abrir chamado, ver setor)";
            atalho.WorkingDirectory = Instalacao.Pasta;
            atalho.Save();
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Roda a primeira sincronização na hora, sem esperar o primeiro
    /// intervalo da Tarefa Agendada. O token já foi validado antes de
    /// chegar aqui (ver ValidarToken), então uma falha nesse ponto é
    /// outra coisa (rede instável, backend fora do ar, rede do cliente
    /// bloqueando a conexão, etc.) -- não desfaz a instalação. Devolve
    /// false nesse caso (ver ExecutarInstalacaoElevada/
    /// InstalarComElevacao/SetupForm, que usam isso pra avisar o usuário
    /// em vez de mostrar "instalado com sucesso" quando na prática o
    /// Recurso ainda não apareceu no GDesk); a Tarefa Agendada tenta de
    /// novo sozinha a cada 6 horas.
    /// </summary>
    private static bool SincronizarAgora(string? adotarRecursoId = null)
    {
        try
        {
            var config = AgentConfig.Carregar();
            var payload = InventoryCollector.ColetarComConfig(config);
            // So vem preenchido nesta chamada, vindo de --adotar-recurso-id
            // (ver ExecutarInstalacaoElevada/InstalarComElevacao/SetupForm) --
            // nunca gravado em AgentConfig/appsettings.json, porque so faz
            // sentido na sincronizacao que reatribui o Recurso; depois disso
            // o identificador_agente ja bate direto (ver Program.cs).
            if (!string.IsNullOrWhiteSpace(adotarRecursoId)) payload.ConfirmarAtualizacaoRecursoId = adotarRecursoId;
            var resultado = new ApiClient(config).SincronizarAsync(payload).GetAwaiter().GetResult();
            if (!resultado.sucesso)
            {
                Console.Error.WriteLine($"[GDeskAgent] Aviso: primeira sincronização falhou ({resultado.mensagem}). Confira o token em Minha Empresa.");
            }
            return resultado.sucesso;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GDeskAgent] Aviso: não foi possível sincronizar agora ({ex.Message}).");
            return false;
        }
    }

    private static void ExecutarComando(string arquivo, string argumentos)
    {
        using var processo = Process.Start(new ProcessStartInfo(arquivo, argumentos)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        processo?.WaitForExit();
    }

    /// <summary>
    /// Chamado por "--desinstalar" (o que o botão Desinstalar do Painel de
    /// Controle executa), ainda sem privilégio de administrador: relança o
    /// próprio .exe com "--desinstalar-elevado" e pede elevação via UAC --
    /// mesmo padrão de InstalarComElevacao, na direção contrária.
    /// </summary>
    public static void SolicitarDesinstalacaoComElevacao()
    {
        // Environment.ProcessPath (.NET 6+), não
        // Process.GetCurrentProcess().MainModule!.FileName! -- MainModule
        // lança BadImageFormatException (0x8007000B) em apps publicados
        // como single-file (ver ConfiguracaoEmbutida.cs, mesmo problema
        // achado lá primeiro): ele tenta inspecionar o módulo como um PE
        // "normal" pra montar FileVersionInfo etc., e a estrutura de
        // bundle do single-file não bate com o que ele espera.
        // ProcessPath só devolve o caminho do executável que iniciou o
        // processo, sem inspecionar nada.
        var exeAtual = Environment.ProcessPath!;
        var psi = new ProcessStartInfo
        {
            FileName = exeAtual,
            Arguments = "--desinstalar-elevado",
            UseShellExecute = true,
            Verb = "runas",
        };

        Process processo;
        try
        {
            processo = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED -- usuário clicou "Não" na janela do UAC.
            throw new Exception("Desinstalação cancelada: é preciso aceitar a permissão de administrador para desinstalar.");
        }

        processo.WaitForExit();
        if (processo.ExitCode != 0)
        {
            throw new Exception($"A desinstalação falhou (código {processo.ExitCode}).");
        }
    }

    /// <summary>
    /// Já rodando elevado (reentrada via "--desinstalar-elevado", vinda do
    /// UAC): desfaz tudo que ExecutarInstalacaoElevada criou -- Tarefas
    /// Agendadas, atalho do Menu Iniciar, entrada no Painel de Controle e,
    /// por fim, a própria pasta de instalação (ver AgendarRemocaoDaPasta:
    /// isso acontece depois que este processo encerrar, já que ele mesmo
    /// roda de dentro dessa pasta).
    /// </summary>
    public static void ExecutarDesinstalacaoElevada()
    {
        ExecutarComando("schtasks.exe", $"/Delete /TN \"{NomeTarefaAgendada}\" /F");
        ExecutarComando("schtasks.exe", $"/Delete /TN \"{NomeTarefaBandeja}\" /F");

        EncerrarOutrosProcessosEmExecucao();
        RemoverAtalhoMenuIniciar();
        RemoverAtalhoAreaTrabalho();
        RemoverDoPainelDeControle();
        AgendarRemocaoDaPasta();
    }

    /// <summary>
    /// Encerra qualquer outra instância do GDeskAgent.exe rodando nesta
    /// máquina (o ícone da bandeja, tipicamente). Usada tanto antes de
    /// apagar a pasta de instalação (ExecutarDesinstalacaoElevada -- sem
    /// isso, o .exe da bandeja ficaria em uso e a remoção falharia
    /// parcialmente) quanto antes de sobrescrever o .exe numa
    /// reinstalação/atualização (ExecutarInstalacaoElevada -- mesmo
    /// problema: File.Copy por cima de um .exe em execução lança
    /// UnauthorizedAccessException mesmo com admin). Nunca encerra o
    /// próprio processo atual (o que está executando esta chamada).
    /// </summary>
    private static void EncerrarOutrosProcessosEmExecucao(int? pidPreservar = null)
    {
        var pidAtual = Environment.ProcessId;
        foreach (var processo in Process.GetProcessesByName("GDeskAgent"))
        {
            if (processo.Id == pidAtual) continue;
            // pidPreservar (só vem de ExecutarInstalacaoElevada -- ver
            // MontarArgumentosInstalacao) é o processo que lançou esta
            // instalação e está esperando ela terminar (InstalarComElevacao/
            // WaitForExit); sem preservar ele, ele também se chama
            // "GDeskAgent" e seria morto por engano.
            if (pidPreservar.HasValue && processo.Id == pidPreservar.Value) continue;
            try
            {
                processo.Kill();
                // Espera o processo terminar de verdade (até 2s) antes de
                // seguir -- Kill() só pede o encerramento; sem esperar, o
                // handle do .exe pode ainda estar aberto quando o chamador
                // (ExecutarInstalacaoElevada) tenta sobrescrevê-lo logo em
                // seguida, voltando a dar UnauthorizedAccessException.
                processo.WaitForExit(2000);
            }
            catch { /* processo já pode ter encerrado sozinho -- não crítico */ }
        }
    }

    private static void RemoverAtalhoMenuIniciar()
    {
        var pastaMenuIniciar = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        try { File.Delete(Path.Combine(pastaMenuIniciar, NomeAtalho)); }
        catch { /* não crítico */ }
        // Remove também o atalho antigo (versões anteriores do agente),
        // caso a máquina tenha sido instalada antes desta mudança.
        try { File.Delete(Path.Combine(pastaMenuIniciar, "Abrir Chamado GDesk.lnk")); }
        catch { /* não crítico */ }
    }

    private static void RemoverAtalhoAreaTrabalho()
    {
        var pastaAreaTrabalho = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        try { File.Delete(Path.Combine(pastaAreaTrabalho, NomeAtalho)); }
        catch { /* não crítico */ }
    }

    private static void RemoverDoPainelDeControle()
    {
        try { Registry.LocalMachine.DeleteSubKeyTree(ChaveUninstall, throwOnMissingSubKey: false); }
        catch { /* não crítico */ }
    }

    /// <summary>
    /// Apaga %ProgramData%\GDeskAgent -- inclusive o próprio .exe em
    /// execução neste exato momento, por isso não pode ser feito
    /// diretamente por este processo (Directory.Delete falharia ou
    /// deixaria lixo pra trás com o arquivo ainda em uso). Em vez disso,
    /// dispara um processo auxiliar destacado (cmd.exe) que espera alguns
    /// segundos -- tempo de sobra pra este processo encerrar de vez -- e
    /// só então apaga a pasta inteira.
    /// </summary>
    private static void AgendarRemocaoDaPasta()
    {
        var pasta = Instalacao.Pasta;
        var comando = $"/C timeout /t 3 /nobreak >nul & rmdir /s /q \"{pasta}\"";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", comando)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch
        {
            // Não crítico -- se falhar, a pasta fica órfã em ProgramData
            // (sem Tarefa Agendada, atalho ou entrada no Painel de
            // Controle, então não afeta o funcionamento do Windows), e o
            // usuário pode apagar manualmente depois.
        }
    }
}
