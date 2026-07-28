using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace GDeskAgent;

/// <summary>
/// Faz o GDeskAgent.exe se instalar sozinho na máquina: copia para
/// %ProgramData%\GDeskAgent, grava o appsettings.json com o token colado
/// pelo usuário (ver SetupForm), registra a Tarefa Agendada de
/// sincronização periódica e cria o atalho "Abrir Chamado GDesk" no Menu
/// Iniciar. Tudo isso exige administrador -- por isso o fluxo é sempre:
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
    public static void InstalarComElevacao(string token, string? clienteId = null, string? patrimonio = null, string? numeroLacre = null)
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

        var exeAtual = Process.GetCurrentProcess().MainModule!.FileName!;
        var psi = new ProcessStartInfo
        {
            FileName = exeAtual,
            Arguments = MontarArgumentosInstalacao(token, clienteId, patrimonio, numeroLacre),
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
        if (processo.ExitCode != 0)
        {
            throw new Exception($"A instalação falhou (código {processo.ExitCode}). Confira o token e tente de novo.");
        }
    }

    /// <summary>
    /// Monta os argumentos de linha de comando de "--instalar-elevado",
    /// incluindo os opcionais "--cliente-id", "--patrimonio" e
    /// "--numero-lacre" só quando fornecidos -- ver Program.cs pro parser
    /// correspondente. Cada valor vai entre aspas (podem ter espaço,
    /// ex.: "Sala 3 - Recepção").
    /// </summary>
    private static string MontarArgumentosInstalacao(string token, string? clienteId, string? patrimonio, string? numeroLacre)
    {
        var argumentos = $"--instalar-elevado \"{token}\"";
        if (!string.IsNullOrWhiteSpace(clienteId)) argumentos += $" --cliente-id \"{clienteId}\"";
        if (!string.IsNullOrWhiteSpace(patrimonio)) argumentos += $" --patrimonio \"{patrimonio}\"";
        if (!string.IsNullOrWhiteSpace(numeroLacre)) argumentos += $" --numero-lacre \"{numeroLacre}\"";
        return argumentos;
    }

    /// <summary>
    /// Já rodando elevado (reentrada via "--instalar-elevado TOKEN [...]",
    /// vinda do UAC ou de uma Tarefa/Script de GPO que já roda como
    /// SYSTEM): faz a instalação de verdade. Seguro de rodar mais de uma
    /// vez -- sempre sobrescreve o que já existia.
    /// </summary>
    public static void ExecutarInstalacaoElevada(string token, string? clienteId = null, string? patrimonio = null, string? numeroLacre = null)
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

        Directory.CreateDirectory(Instalacao.Pasta);

        var exeAtual = Process.GetCurrentProcess().MainModule!.FileName!;
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
            },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Instalacao.CaminhoConfig, configJson);

        RegistrarTarefaAgendada();
        RegistrarTarefaBandeja();
        CriarAtalhoMenuIniciar();
        RegistrarNoPainelDeControle();
        SincronizarAgora();
        IniciarBandejaAgora();
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

    private static void CriarAtalhoMenuIniciar()
    {
        var pastaMenuIniciar = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        var caminhoAtalho = Path.Combine(pastaMenuIniciar, "Abrir Chamado GDesk.lnk");

        // WScript.Shell via COM tardio (sem precisar de nenhum pacote
        // NuGet extra) -- é a forma clássica e mais simples de criar um
        // atalho .lnk a partir de C#. Se o Windows Script Host estiver
        // desabilitado (bem raro, e normalmente só em máquinas com
        // políticas de segurança bem restritivas), só não cria o atalho;
        // isso não é crítico o suficiente pra falhar a instalação inteira.
        var tipoShell = Type.GetTypeFromProgID("WScript.Shell");
        if (tipoShell == null) return;

        dynamic shell = Activator.CreateInstance(tipoShell)!;
        try
        {
            dynamic atalho = shell.CreateShortcut(caminhoAtalho);
            atalho.TargetPath = Instalacao.CaminhoExe;
            atalho.Arguments = "--abrir-chamado";
            atalho.Description = "Abrir o portal do GDesk para registrar ou acompanhar chamados";
            atalho.WorkingDirectory = Instalacao.Pasta;
            atalho.Save();
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    private static void SincronizarAgora()
    {
        // Roda a primeira sincronização na hora, sem esperar o primeiro
        // intervalo da Tarefa Agendada. O token já foi validado antes de
        // chegar aqui (ver ValidarToken), então uma falha nesse ponto é
        // outra coisa (rede instável, backend fora do ar etc.) -- não
        // desfaz a instalação, só avisa no console; a Tarefa Agendada
        // tenta de novo sozinha mais tarde.
        try
        {
            var config = AgentConfig.Carregar();
            var resultado = new ApiClient(config).SincronizarAsync(InventoryCollector.ColetarComConfig(config)).GetAwaiter().GetResult();
            if (!resultado.sucesso)
            {
                Console.Error.WriteLine($"[GDeskAgent] Aviso: primeira sincronização falhou ({resultado.mensagem}). Confira o token em Minha Empresa.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GDeskAgent] Aviso: não foi possível sincronizar agora ({ex.Message}).");
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
        var exeAtual = Process.GetCurrentProcess().MainModule!.FileName!;
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
        RemoverDoPainelDeControle();
        AgendarRemocaoDaPasta();
    }

    /// <summary>
    /// Encerra qualquer outra instância do GDeskAgent.exe rodando nesta
    /// máquina (o ícone da bandeja, tipicamente) antes de apagar a pasta
    /// de instalação -- sem isso, o arquivo do .exe da bandeja ficaria em
    /// uso e a remoção da pasta falharia parcialmente. Nunca encerra o
    /// próprio processo atual (o que está executando esta desinstalação).
    /// </summary>
    private static void EncerrarOutrosProcessosEmExecucao()
    {
        var pidAtual = Environment.ProcessId;
        foreach (var processo in Process.GetProcessesByName("GDeskAgent"))
        {
            if (processo.Id == pidAtual) continue;
            try { processo.Kill(); }
            catch { /* processo já pode ter encerrado sozinho -- não crítico */ }
        }
    }

    private static void RemoverAtalhoMenuIniciar()
    {
        var pastaMenuIniciar = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        var caminhoAtalho = Path.Combine(pastaMenuIniciar, "Abrir Chamado GDesk.lnk");
        try { File.Delete(caminhoAtalho); }
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
