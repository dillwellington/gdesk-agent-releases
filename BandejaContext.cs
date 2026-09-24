using System.Diagnostics;
using System.Windows.Forms;

namespace GDeskAgent;

/// <summary>
/// Ícone fixo na bandeja do sistema (perto do relógio), rodando o tempo
/// todo enquanto o usuário está logado -- inicia sozinho via a Tarefa
/// Agendada "GDesk Agente - Bandeja" (gatilho ONLOGON, ver
/// SelfInstaller.RegistrarTarefaBandeja). Não substitui a sincronização
/// periódica (essa continua na Tarefa Agendada de 6 em 6 horas, separada
/// e headless) -- é só um jeito rápido de abrir chamado ou forçar uma
/// sincronização, sem precisar procurar no Menu Iniciar.
///
/// Não tem janela principal: um ApplicationContext sem Form nenhum mantém
/// o Application.Run() rodando (e portanto o ícone visível) até o próprio
/// usuário escolher "Sair" no menu.
/// </summary>
public sealed class BandejaContext : ApplicationContext
{
    private readonly NotifyIcon _icone;
    private readonly AgentConfig _config;
    private PainelForm? _painel;
    private readonly System.Windows.Forms.Timer _timerPedido;
    private bool _sincronizando;

    public BandejaContext(AgentConfig config)
    {
        _config = config;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Painel", null, (_, _) => AbrirPainel());
        menu.Items.Add("Abrir chamado", null, (_, _) => AbrirChamado());
        menu.Items.Add("Sincronizar agora", null, async (_, _) => await SincronizarAgoraAsync());
        menu.Items.Add("Ver log de sincronização", null, (_, _) => new LogAgenteForm(_config).Show());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Sair());

        _icone = new NotifyIcon
        {
            Icon = CarregarIconeApp(),
            Text = "GDesk Agente",
            Visible = true,
            ContextMenuStrip = menu,
        };
        // "Forçar sincronização" pelo sistema: a cada ~5 min pergunta ao servidor
        // se há pedido pendente pra esta máquina (jitter de até 1 min pra não
        // gerar rajada quando muitos agentes sobem juntos).
        _timerPedido = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 + Random.Shared.Next(0, 60_000) };
        _timerPedido.Tick += async (_, _) => await VerificarPedidoAsync();
        _timerPedido.Start();

        // Clique esquerdo (único) abre o Painel -- o botão direito já abre
        // o menu de contexto sozinho, sem precisar de handler nenhum aqui.
        _icone.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) AbrirPainel();
        };
    }

    /// <summary>
    /// Reaproveita a mesma janela se já estiver aberta (em vez de empilhar
    /// várias em cliques repetidos no ícone) -- só traz pra frente.
    /// </summary>
    private void AbrirPainel()
    {
        if (_painel is { IsDisposed: false })
        {
            _painel.Activate();
            return;
        }

        _painel = new PainelForm(_config);
        _painel.Show();
        _painel.Activate();
    }

    /// <summary>
    /// Usa o mesmo ícone do .exe (embutido via ApplicationIcon no .csproj,
    /// ver Assets/icone.ico) em vez de um ícone genérico do Windows --
    /// extrai direto do próprio executável em execução, então não precisa
    /// duplicar o arquivo de ícone em lugar nenhum.
    /// </summary>
    private static System.Drawing.Icon CarregarIconeApp()
    {
        try
        {
            return System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    private void AbrirChamado()
    {
        // Sem formulário próprio de login no agente -- só abre o
        // navegador direto na tela de login de sempre do GDesk; a pessoa
        // entra lá mesmo, com o e-mail/senha dela.
        Process.Start(new ProcessStartInfo($"{_config.PortalUrl.TrimEnd('/')}/login.html") { UseShellExecute = true });
    }

    /// <summary>
    /// Consulta o pedido de sincronização feito pelo sistema (tela de
    /// Recursos) e, se houver, sincroniza. Pedido atendido = o servidor passa
    /// a ver ultima_sincronizacao maior que o horário do pedido.
    /// </summary>
    private async Task VerificarPedidoAsync()
    {
        if (_sincronizando) return;
        try
        {
            var identificador = InventoryCollector.ObterIdentificadorAgente();
            if (string.IsNullOrWhiteSpace(identificador)) return;
            if (!await new ApiClient(_config).ConsultarPedidoSincronizacaoAsync(identificador)) return;

            LogLocal.Registrar("INFO", "Pedido de sincronização recebido do sistema (GDesk).");
            await Task.Delay(Random.Shared.Next(0, 45_000)); // espalha a carga entre as máquinas
            await SincronizarAgoraAsync(silencioso: true);
        }
        catch (Exception ex)
        {
            LogLocal.Registrar("ERRO", $"Falha ao atender o pedido de sincronização: {ex.Message}");
        }
    }

    private async Task SincronizarAgoraAsync(bool silencioso = false)
    {
        if (_sincronizando) return;
        _sincronizando = true;
        try
        {
            _icone.Text = "GDesk Agente — sincronizando...";
            var cliente = new ApiClient(_config);
            var (sucesso, mensagem) = await cliente.SincronizarAsync(InventoryCollector.ColetarComConfig(_config));
            _icone.Text = "GDesk Agente";
            // Pedido vindo do sistema: só avisa se falhar, pra não incomodar o usuário à toa.
            if (silencioso && sucesso) return;
            _icone.ShowBalloonTip(
                4000,
                "GDesk",
                sucesso ? "Inventário sincronizado com sucesso." : $"Falha ao sincronizar: {mensagem}",
                sucesso ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        finally
        {
            _sincronizando = false;
        }
    }

    private void Sair()
    {
        _icone.Visible = false;
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timerPedido.Dispose();
            _icone.Dispose();
        }
        base.Dispose(disposing);
    }
}
