using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using GDeskAgent;

// OutputType=WinExe (ver GDeskAgent.csproj) evita janela preta quando a
// Tarefa Agendada roda o agente sozinha, mas como efeito colateral o
// processo não fica automaticamente conectado ao console de quem chamou
// (PowerShell/cmd) -- Console.WriteLine roda sem erro, só não aparece em
// lugar nenhum, e o próprio PowerShell nem espera o processo terminar
// (por isso o prompt volta na hora, sem nada impresso). AttachConsole(-1)
// pede pra usar o console do processo pai, SE existir um -- se o agente
// foi iniciado sem console (Tarefa Agendada, duplo clique no Explorer),
// essa chamada só falha em silêncio (retorna false) e nada muda.
AttachConsole(-1);

// Uso:
//   GDeskAgent.exe                    -> primeira vez nesta máquina: mostra a tela de instalação (pede o token da empresa).
//                                         já instalado: sincroniza o inventário (é isso que a Tarefa Agendada chama).
//   GDeskAgent.exe --abrir-chamado    -> abre o navegador direto na tela de login do GDesk (a pessoa loga lá mesmo,
//                                        sem nenhum formulário dentro do agente).
//   GDeskAgent.exe --bandeja          -> uso interno: ícone fixo na bandeja (perto do relógio), iniciado sozinho
//                                        no login do Windows -- ver SelfInstaller.RegistrarTarefaBandeja e
//                                        BandejaContext. Não é pra ser chamado manualmente (mas não tem problema
//                                        rodar -- só mostra o ícone com o menu de sempre).
//   GDeskAgent.exe --teste            -> só coleta e imprime o inventário no console, sem enviar nada (debug local).
//   GDeskAgent.exe --painel           -> abre o painel do agente (Setor/Subsetor atuais + botão "Abrir chamado") --
//                                        é o que os atalhos "GDesk Agente" (Menu Iniciar/Área de Trabalho, ver
//                                        SelfInstaller.CriarAtalho) e o clique no ícone da bandeja abrem.
//   GDeskAgent.exe --instalar-elevado TOKEN [--cliente-id ID] [--patrimonio "..."] [--numero-lacre "..."] [--setor-id ID]
//                                     -> uso interno: reentrada já elevada (UAC ou GPO) que faz a instalação de
//                                        verdade -- ver SelfInstaller. Os opcionais só fazem sentido vindos de um
//                                        download personalizado por cliente (ver ConfiguracaoEmbutida.cs) e/ou do
//                                        Setor/Subsetor escolhido no SetupForm. Não é pra ser chamado manualmente.
//   GDeskAgent.exe --desinstalar      -> é o que o botão "Desinstalar" do Painel de Controle > Programas e
//                                        Recursos executa (ver SelfInstaller.RegistrarNoPainelDeControle);
//                                        pede elevação via UAC e desfaz a instalação.
//   GDeskAgent.exe --desinstalar-elevado
//                                     -> uso interno: reentrada já elevada que remove Tarefas Agendadas, atalho,
//                                        entrada no Painel de Controle e a pasta de instalação -- ver
//                                        SelfInstaller. Não é pra ser chamado manualmente.

if (args.Length >= 2 && args[0] == "--instalar-elevado")
{
    try
    {
        // --cliente-id/--patrimonio/--numero-lacre são opcionais -- só
        // vêm preenchidos quando o download foi personalizado por
        // cliente (ver ConfiguracaoEmbutida.cs/SetupForm.cs) e/ou o
        // cliente exige etiqueta de patrimônio/número do lacre.
        SelfInstaller.ExecutarInstalacaoElevada(
            args[1],
            ObterArgumento(args, "--cliente-id"),
            ObterArgumento(args, "--patrimonio"),
            ObterArgumento(args, "--numero-lacre"),
            ObterArgumento(args, "--setor-id"));
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[GDeskAgent] Falha na instalação: {ex.Message}");
        // TEMPORÁRIO -- este processo roda elevado via UAC (Verb=runas),
        // então o console.Error acima quase nunca chega visível pra quem
        // clicou em Instalar no SetupForm (é outro processo -- ver
        // comentário em SelfInstaller.InstalarComElevacao). Grava a
        // exceção completa (com stack trace) num arquivo em %TEMP%, que
        // sobrevive independente de console, pra dar pra diagnosticar o
        // "código 1" genérico que aparece na tela. Remover depois de
        // achar a causa.
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "gdeskagent-erro-instalacao.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}");
        }
        catch { /* diagnóstico best-effort */ }
        return 1;
    }
}

if (args.Contains("--desinstalar-elevado"))
{
    try
    {
        SelfInstaller.ExecutarDesinstalacaoElevada();
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[GDeskAgent] Falha na desinstalação: {ex.Message}");
        return 1;
    }
}

if (args.Contains("--desinstalar"))
{
    // Chamado pelo Painel de Controle, ainda sem privilégio de
    // administrador -- pede elevação (UAC) e relança com
    // "--desinstalar-elevado" (ver SelfInstaller). O Painel de Controle já
    // pede confirmação ("Tem certeza que deseja desinstalar?") antes de
    // chegar até aqui, então não repetimos essa pergunta.
    try
    {
        SelfInstaller.SolicitarDesinstalacaoComElevacao();
        return 0;
    }
    catch (Exception ex)
    {
        MessageBox.Show(ex.Message, "GDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return 1;
    }
}

if (args.Contains("--bandeja"))
{
    // Chamado sozinho pela Tarefa Agendada "GDesk Agente - Bandeja"
    // (gatilho ONLOGON) ou logo depois da instalação -- se por algum
    // motivo o agente ainda não estiver instalado nesta máquina, só
    // encerra em silêncio (sem MessageBox: ninguém está olhando essa
    // execução automática pra ler um aviso).
    if (!Instalacao.JaInstalado)
    {
        return 1;
    }

    var configBandeja = AgentConfig.Carregar();
    ApplicationConfiguration.Initialize();
    Application.Run(new BandejaContext(configBandeja));
    return 0;
}

if (args.Contains("--abrir-chamado"))
{
    if (!Instalacao.JaInstalado)
    {
        MessageBox.Show(
            "O agente ainda não foi instalado nesta máquina. Dê duplo clique no GDeskAgent.exe (sem opções) primeiro.",
            "GDesk",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return 1;
    }

    // Sem formulário próprio de login no agente -- só abre o navegador
    // direto na tela de login de sempre do GDesk; a pessoa entra lá
    // mesmo, com o e-mail/senha dela.
    var configAbrirChamado = AgentConfig.Carregar();
    Process.Start(new ProcessStartInfo($"{configAbrirChamado.PortalUrl.TrimEnd('/')}/login.html") { UseShellExecute = true });
    return 0;
}

if (args.Contains("--painel"))
{
    if (!Instalacao.JaInstalado)
    {
        MessageBox.Show(
            "O agente ainda não foi instalado nesta máquina. Dê duplo clique no GDeskAgent.exe (sem opções) primeiro.",
            "GDesk",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return 1;
    }

    var configPainel = AgentConfig.Carregar();
    ApplicationConfiguration.Initialize();
    Application.Run(new PainelForm(configPainel));
    return 0;
}

if (!Instalacao.JaInstalado)
{
    // Primeira execução nesta máquina (nenhum appsettings.json em
    // %ProgramData%\GDeskAgent ainda) -- mostra a telinha de instalação
    // em vez de tentar sincronizar com uma configuração que não existe.
    // Se o .exe foi baixado personalizado (Minha Empresa ou um Cliente
    // específico -- ver ConfiguracaoEmbutida.cs), o token (e, se
    // aplicável, o cliente e a obrigatoriedade de patrimônio/lacre) já
    // vêm detectados a partir dos bytes do próprio binário, sem precisar
    // colar nada.
    //
    // TEMPORÁRIO -- envolto num try/catch amplo (+ handlers de exceção
    // global logo abaixo) só pra investigar um caso em que o processo
    // estava terminando em silêncio, sem MessageBox nem nada no console,
    // antes mesmo da tela aparecer -- WinExe sem esse tipo de rede de
    // segurança pode morrer sem deixar rastro nenhum. Depois de achar a
    // causa, dá pra simplificar de volta.
    // Sem isso, dependendo da configuração, o WinForms deixa a exceção
    // matar o processo em vez de disparar o evento ThreadException.
    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
    Application.ThreadException += (_, e) =>
        MessageBox.Show($"Erro na tela (ThreadException):\n\n{e.Exception}", "GDesk - erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        MessageBox.Show($"Erro fatal (UnhandledException):\n\n{e.ExceptionObject}", "GDesk - erro", MessageBoxButtons.OK, MessageBoxIcon.Error);

    try
    {
        var configEmbutida = ConfiguracaoEmbutida.DetectarNoProprioBinario();
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm(configEmbutida));
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Erro ao abrir a tela de instalação:\n\n{ex}", "GDesk - erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return 1;
    }
    return 0;
}

if (args.Contains("--teste"))
{
    var payloadTeste = InventoryCollector.Coletar();
    Console.WriteLine(JsonSerializer.Serialize(payloadTeste, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

var config = AgentConfig.Carregar();
var cliente = new ApiClient(config);
var (sucesso, mensagem) = await cliente.SincronizarAsync(InventoryCollector.ColetarComConfig(config));

if (sucesso)
{
    Console.WriteLine($"[GDeskAgent] Sincronizado com sucesso: {mensagem}");
    return 0;
}

Console.Error.WriteLine($"[GDeskAgent] Falha ao sincronizar: {mensagem}");
return 1;

// Lê o valor que vem depois de uma flag nomeada em args (ex.: "--cliente-id"
// -> o item seguinte no array) -- usado por "--instalar-elevado" pra pegar
// os argumentos opcionais de cliente/patrimônio/lacre (ver
// SelfInstaller.MontarArgumentosInstalacao, que é quem monta essa mesma
// linha de comando na reentrada elevada). Retorna null se a flag não
// aparecer ou não tiver valor depois dela.
static string? ObterArgumento(string[] argumentos, string nome)
{
    var indice = Array.IndexOf(argumentos, nome);
    return (indice >= 0 && indice + 1 < argumentos.Length) ? argumentos[indice + 1] : null;
}

// ATTACH_PARENT_PROCESS = -1 -- ver comentário lá em cima, antes do "Uso:".
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern bool AttachConsole(int dwProcessId);
