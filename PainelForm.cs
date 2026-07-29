using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GDeskAgent;

/// <summary>
/// Painel do agente, aberto pelo ícone da bandeja (clique) ou pelos
/// atalhos "GDesk Agente" no Menu Iniciar/Área de Trabalho (--painel, ver
/// Program.cs e SelfInstaller.CriarAtalho) -- pensado pra quem fechou o
/// ícone da bandeja e precisa de outro jeito de achar isso rápido.
///
/// Mostra o Setor/Subsetor ATUAL do recurso (sempre buscado em
/// GET /agente/estado, nunca lido de appsettings.json -- o valor pode ter
/// sido alterado pelo sistema depois da instalação, e aqui é só leitura:
/// definir/alterar setor é feito uma única vez no SetupForm, na
/// instalação, ou depois só pelo sistema, nunca de novo por aqui) e o
/// botão "Abrir chamado" (mesmo comportamento do menu da bandeja: abre o
/// navegador direto na tela de login do GDesk).
/// </summary>
public sealed class PainelForm : Form
{
    private readonly AgentConfig _config;

    private readonly Label _rotuloSetor = new() { AutoSize = false, Left = 20, Width = 340, Height = 22 };
    private readonly Label _rotuloSubsetor = new() { AutoSize = false, Left = 20, Width = 340, Height = 22 };
    private readonly Button _botaoAbrirChamado = new() { Text = "Abrir chamado" };
    private readonly Button _botaoFechar = new() { Text = "Fechar" };

    public PainelForm(AgentConfig config)
    {
        _config = config;

        Text = "GDesk Agente";
        Width = 380;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var y = 18;

        var rotuloTitulo = new Label
        {
            Text = "GDesk Agente",
            AutoSize = true,
            Left = 20,
            Top = y,
            Font = new Font(Font, FontStyle.Bold),
        };
        Controls.Add(rotuloTitulo);
        y += 34;

        _rotuloSetor.Top = y;
        _rotuloSetor.Text = "Setor: carregando...";
        _rotuloSetor.ForeColor = Color.Gray;
        Controls.Add(_rotuloSetor);
        y += 24;

        _rotuloSubsetor.Top = y;
        _rotuloSubsetor.Text = "Subsetor: carregando...";
        _rotuloSubsetor.ForeColor = Color.Gray;
        Controls.Add(_rotuloSubsetor);
        y += 34;

        _botaoAbrirChamado.Left = 20;
        _botaoAbrirChamado.Top = y;
        _botaoAbrirChamado.Width = 340;
        _botaoAbrirChamado.Height = 34;
        _botaoAbrirChamado.Click += (_, _) => AbrirChamado();
        AcceptButton = _botaoAbrirChamado;
        Controls.Add(_botaoAbrirChamado);
        y += 42;

        _botaoFechar.Left = 20;
        _botaoFechar.Top = y;
        _botaoFechar.Width = 340;
        _botaoFechar.Height = 30;
        _botaoFechar.Click += (_, _) => Close();
        Controls.Add(_botaoFechar);
        y += _botaoFechar.Height;

        Height = y + 54;

        Load += async (_, _) => await CarregarSetorAtualAsync();
    }

    private void AbrirChamado()
    {
        // Mesmo comportamento do menu da bandeja (ver BandejaContext.cs):
        // sem formulário próprio de login no agente, só abre o navegador
        // direto na tela de login de sempre do GDesk.
        Process.Start(new ProcessStartInfo($"{_config.PortalUrl.TrimEnd('/')}/login.html") { UseShellExecute = true });
    }

    /// <summary>
    /// Busca o setor_id atual do recurso (pode ter sido alterado pelo
    /// sistema desde a instalação) e a lista de setores da empresa, pra
    /// resolver os nomes de Setor/Subsetor pra exibição. Nunca escreve
    /// nada -- só leitura, ver comentário na classe.
    /// </summary>
    private async Task CarregarSetorAtualAsync()
    {
        var identificador = InventoryCollector.ObterMachineGuid();
        var (sucesso, estado, mensagem) = await new ApiClient(_config).ObterEstadoAsync(identificador);

        if (!sucesso || estado == null)
        {
            _rotuloSetor.ForeColor = Color.Firebrick;
            _rotuloSetor.Text = $"Não foi possível carregar o setor ({mensagem}).";
            _rotuloSubsetor.Text = "";
            return;
        }

        _rotuloSetor.ForeColor = Color.Black;
        _rotuloSubsetor.ForeColor = Color.Black;

        if (string.IsNullOrEmpty(estado.SetorId))
        {
            _rotuloSetor.Text = "Setor: (não definido)";
            _rotuloSubsetor.Text = "Subsetor: —";
            return;
        }

        var atual = estado.Setores.FirstOrDefault(s => s.Id == estado.SetorId);
        if (atual == null)
        {
            _rotuloSetor.Text = "Setor: (não definido)";
            _rotuloSubsetor.Text = "Subsetor: —";
            return;
        }

        if (string.IsNullOrEmpty(atual.SetorPaiId))
        {
            _rotuloSetor.Text = $"Setor: {atual.Nome}";
            _rotuloSubsetor.Text = "Subsetor: —";
        }
        else
        {
            var pai = estado.Setores.FirstOrDefault(s => s.Id == atual.SetorPaiId);
            _rotuloSetor.Text = $"Setor: {pai?.Nome ?? "—"}";
            _rotuloSubsetor.Text = $"Subsetor: {atual.Nome}";
        }
    }
}
