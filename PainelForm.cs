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
/// GET /agente/estado) e permite alterá-lo (botão "Atualizar setor"), além
/// do botão "Abrir chamado" (mesmo comportamento do menu da bandeja: abre o
/// navegador direto na tela de login do GDesk).
/// </summary>
public sealed class PainelForm : Form
{
    private readonly AgentConfig _config;

    private readonly Label _rotuloSetor = new() { Text = "Setor", AutoSize = true, Left = 20 };
    private readonly Label _rotuloSubsetor = new() { Text = "Subsetor", AutoSize = true, Left = 20 };
    private readonly ComboBox _comboSetor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Left = 20, Width = 340, Enabled = false };
    private readonly ComboBox _comboSubsetor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Left = 20, Width = 340, Enabled = false };
    private readonly Label _rotuloStatusSetor = new() { AutoSize = false, Left = 20, Width = 340, Height = 34, ForeColor = Color.Gray };
    private readonly Button _botaoAtualizarSetor = new() { Text = "Atualizar setor", Enabled = false };
    private List<SetorAgenteItem> _setores = new();
    private bool _carregandoCombos;

    private sealed class ItemCombo
    {
        public string Id { get; }
        public string Nome { get; }
        public ItemCombo(string id, string nome) { Id = id; Nome = nome; }
        public override string ToString() => Nome;
    }
    private readonly Label _rotuloSync = new() { AutoSize = false, Left = 20, Width = 340, Height = 40 };
    private readonly Button _botaoLog = new() { Text = "Ver log de sincronização" };
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
        Controls.Add(_rotuloSetor);
        y += 20;
        _comboSetor.Top = y;
        _comboSetor.SelectedIndexChanged += (_, _) => { if (!_carregandoCombos) PopularSubsetores(null); };
        Controls.Add(_comboSetor);
        y += 30;

        _rotuloSubsetor.Top = y;
        Controls.Add(_rotuloSubsetor);
        y += 20;
        _comboSubsetor.Top = y;
        Controls.Add(_comboSubsetor);
        y += 32;

        _botaoAtualizarSetor.Left = 20;
        _botaoAtualizarSetor.Top = y;
        _botaoAtualizarSetor.Width = 340;
        _botaoAtualizarSetor.Height = 30;
        _botaoAtualizarSetor.Click += async (_, _) => await AtualizarSetorAsync();
        Controls.Add(_botaoAtualizarSetor);
        y += 34;

        _rotuloStatusSetor.Top = y;
        _rotuloStatusSetor.Text = "Carregando setores...";
        Controls.Add(_rotuloStatusSetor);
        y += 38;

        _rotuloSync.Top = y;
        Controls.Add(_rotuloSync);
        y += 46;

        _botaoLog.Left = 20;
        _botaoLog.Top = y;
        _botaoLog.Width = 340;
        _botaoLog.Height = 30;
        _botaoLog.Click += (_, _) =>
        {
            var log = new LogAgenteForm(_config);
            log.FormClosed += (_, _) => AtualizarStatusSincronizacao();
            log.Show(this);
        };
        Controls.Add(_botaoLog);
        y += 40;

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

        AtualizarStatusSincronizacao();
        Load += async (_, _) => await CarregarSetorAtualAsync();
    }

    /// <summary>
    /// Resumo da última tentativa de sincronização (do log local): deixa
    /// claro, direto no painel, quando o inventário NÃO chegou no GDesk.
    /// </summary>
    private void AtualizarStatusSincronizacao()
    {
        var ultimo = LogLocal.UltimoResultado();
        if (ultimo == null)
        {
            _rotuloSync.ForeColor = Color.Gray;
            _rotuloSync.Text = "Sincronização: nenhuma tentativa registrada ainda.";
        }
        else if (ultimo.Value.sucesso)
        {
            _rotuloSync.ForeColor = Color.DarkGreen;
            _rotuloSync.Text = "Última sincronização: OK\n" + ultimo.Value.linha[..Math.Min(19, ultimo.Value.linha.Length)];
        }
        else
        {
            _rotuloSync.ForeColor = Color.Firebrick;
            _rotuloSync.Text = "Última sincronização FALHOU — não gravou no GDesk. Veja o log.\n" + ultimo.Value.linha[..Math.Min(19, ultimo.Value.linha.Length)];
        }
    }

    private void AbrirChamado()
    {
        // Mesmo comportamento do menu da bandeja (ver BandejaContext.cs):
        // sem formulário próprio de login no agente, só abre o navegador
        // direto na tela de login de sempre do GDesk.
        Process.Start(new ProcessStartInfo($"{_config.PortalUrl.TrimEnd('/')}/login.html") { UseShellExecute = true });
    }

    /// <summary>
    /// Busca o setor_id atual do recurso e a lista de setores da empresa
    /// (GET /agente/estado) e preenche os combos já com o setor atual
    /// selecionado.
    /// </summary>
    private async Task CarregarSetorAtualAsync()
    {
        var identificador = InventoryCollector.ObterMachineGuid();
        var (sucesso, estado, mensagem) = await new ApiClient(_config).ObterEstadoAsync(identificador);

        if (!sucesso || estado == null)
        {
            _rotuloStatusSetor.ForeColor = Color.Firebrick;
            _rotuloStatusSetor.Text = $"Não foi possível carregar o setor ({mensagem}).";
            return;
        }

        _setores = estado.Setores;
        _carregandoCombos = true;
        try
        {
            _comboSetor.Items.Clear();
            foreach (var s in _setores.Where(s => string.IsNullOrEmpty(s.SetorPaiId)).OrderBy(s => s.Nome))
                _comboSetor.Items.Add(new ItemCombo(s.Id, s.Nome));

            var atual = _setores.FirstOrDefault(s => s.Id == estado.SetorId);
            string? principalId = atual == null ? null : (string.IsNullOrEmpty(atual.SetorPaiId) ? atual.Id : atual.SetorPaiId);
            string? subsetorId = atual != null && !string.IsNullOrEmpty(atual.SetorPaiId) ? atual.Id : null;

            foreach (var item in _comboSetor.Items.Cast<ItemCombo>())
                if (item.Id == principalId) _comboSetor.SelectedItem = item;

            _comboSetor.Enabled = _comboSetor.Items.Count > 0;
            PopularSubsetores(subsetorId);
        }
        finally
        {
            _carregandoCombos = false;
        }

        _botaoAtualizarSetor.Enabled = _comboSetor.Enabled;
        _rotuloStatusSetor.ForeColor = Color.Gray;
        _rotuloStatusSetor.Text = _comboSetor.Enabled
            ? "Escolha o setor/subsetor e clique em Atualizar setor."
            : "Nenhum setor cadastrado nesta empresa.";
    }

    private void PopularSubsetores(string? selecionarId)
    {
        _comboSubsetor.Items.Clear();
        _comboSubsetor.Enabled = false;
        if (_comboSetor.SelectedItem is not ItemCombo setor) return;

        foreach (var f in _setores.Where(s => s.SetorPaiId == setor.Id).OrderBy(s => s.Nome))
            _comboSubsetor.Items.Add(new ItemCombo(f.Id, f.Nome));

        if (_comboSubsetor.Items.Count == 0) return;
        _comboSubsetor.Enabled = true;
        foreach (var item in _comboSubsetor.Items.Cast<ItemCombo>())
            if (item.Id == selecionarId) _comboSubsetor.SelectedItem = item;
    }

    /// <summary>
    /// Grava o setor escolhido (setor-manual.txt, que prevalece sobre o do
    /// appsettings) e sincroniza na hora; o servidor aplica o setor do
    /// agente em toda sincronização.
    /// </summary>
    private async Task AtualizarSetorAsync()
    {
        if (_comboSetor.SelectedItem is not ItemCombo setor)
        {
            _rotuloStatusSetor.ForeColor = Color.Firebrick;
            _rotuloStatusSetor.Text = "Escolha o setor.";
            return;
        }
        if (_comboSubsetor.Enabled && _comboSubsetor.SelectedItem == null)
        {
            _rotuloStatusSetor.ForeColor = Color.Firebrick;
            _rotuloStatusSetor.Text = "Escolha o subsetor.";
            return;
        }

        var setorId = (_comboSubsetor.SelectedItem as ItemCombo)?.Id ?? setor.Id;
        var nome = (_comboSubsetor.SelectedItem as ItemCombo)?.Nome ?? setor.Nome;

        if (!AgentConfig.GravarSetorManual(setorId))
        {
            _rotuloStatusSetor.ForeColor = Color.Firebrick;
            _rotuloStatusSetor.Text = "Não foi possível salvar o setor nesta máquina.";
            return;
        }

        _botaoAtualizarSetor.Enabled = false;
        _rotuloStatusSetor.ForeColor = Color.Gray;
        _rotuloStatusSetor.Text = "Atualizando...";
        LogLocal.Registrar("INFO", $"Setor alterado manualmente no painel do agente para \"{nome}\" ({setorId}).");
        try
        {
            var (sucesso, mensagem) = await new ApiClient(_config).SincronizarAsync(InventoryCollector.ColetarComConfig(_config));
            AtualizarStatusSincronizacao();
            if (sucesso)
            {
                await CarregarSetorAtualAsync();
                _rotuloStatusSetor.ForeColor = Color.DarkGreen;
                _rotuloStatusSetor.Text = $"Setor atualizado para \"{nome}\".";
            }
            else
            {
                _rotuloStatusSetor.ForeColor = Color.Firebrick;
                _rotuloStatusSetor.Text = $"Setor salvo, mas a sincronização falhou: {mensagem}";
            }
        }
        finally
        {
            _botaoAtualizarSetor.Enabled = true;
        }
    }
}
