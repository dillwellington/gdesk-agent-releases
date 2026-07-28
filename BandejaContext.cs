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

    public BandejaContext(AgentConfig config)
    {
        _config = config;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir chamado", null, (_, _) => AbrirChamado());
        menu.Items.Add("Sincronizar agora", null, async (_, _) => await SincronizarAgoraAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Sair());

        _icone = new NotifyIcon
        {
            Icon = CarregarIconeApp(),
            Text = "GDesk Agente",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icone.DoubleClick += (_, _) => AbrirChamado();
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

    private async Task SincronizarAgoraAsync()
    {
        _icone.Text = "GDesk Agente — sincronizando...";
        var cliente = new ApiClient(_config);
        var (sucesso, mensagem) = await cliente.SincronizarAsync(InventoryCollector.Coletar());
        _icone.Text = "GDesk Agente";
        _icone.ShowBalloonTip(
            4000,
            "GDesk",
            sucesso ? "Inventário sincronizado com sucesso." : $"Falha ao sincronizar: {mensagem}",
            sucesso ? ToolTipIcon.Info : ToolTipIcon.Warning);
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
            _icone.Dispose();
        }
        base.Dispose(disposing);
    }
}
