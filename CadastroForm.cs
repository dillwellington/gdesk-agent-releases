using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GDeskAgent;

/// <summary>
/// Alteração de cliente, setor/subsetor, patrimônio e lacre pelo agente.
/// Exige login (e-mail/senha) de um usuário "agente" ou "admin_cliente" da
/// empresa, validado no servidor (POST /agente/cadastro/autorizar). Depois
/// de autorizado, "Salvar" envia o login de novo (guardado só em memória
/// enquanto esta janela está aberta) em POST /agente/cadastro/alterar --
/// quem alterou fica registrado no log do recurso. Os novos valores também
/// vão pro cadastro-manual.json, pra sincronizações seguintes não
/// reverterem a alteração.
/// </summary>
public sealed class CadastroForm : Form
{
    private readonly AgentConfig _config;
    private readonly string _identificador = InventoryCollector.ObterMachineGuid();

    private readonly TextBox _campoEmail = new() { Left = 20, Width = 340, PlaceholderText = "E-mail do usuário agente/administrador" };
    private readonly TextBox _campoSenha = new() { Left = 20, Width = 340, UseSystemPasswordChar = true, PlaceholderText = "Senha" };
    private readonly Button _botaoEntrar = new() { Text = "Entrar", Left = 20, Width = 340, Height = 30 };

    private readonly ComboBox _comboCliente = new() { DropDownStyle = ComboBoxStyle.DropDownList, Left = 20, Width = 340 };
    private readonly ComboBox _comboSetor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Left = 20, Width = 340 };
    private readonly ComboBox _comboSubsetor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Left = 20, Width = 340 };
    private readonly TextBox _campoPatrimonio = new() { Left = 20, Width = 340 };
    private readonly TextBox _campoLacre = new() { Left = 20, Width = 340 };
    private readonly Button _botaoSalvar = new() { Text = "Salvar", Left = 20, Width = 165, Height = 30 };
    private readonly Button _botaoCancelar = new() { Text = "Cancelar", Left = 195, Width = 165, Height = 30 };
    private readonly Label _rotuloStatus = new() { AutoSize = false, Left = 20, Width = 340, Height = 44, ForeColor = Color.Gray };

    private readonly List<Control> _controlesEdicao = new();
    private List<SetorAgenteItem> _setores = new();
    private string _email = "";
    private string _senha = "";
    private bool _carregando;

    private sealed class ItemCombo
    {
        public string Id { get; }
        public string Nome { get; }
        public ItemCombo(string id, string nome) { Id = id; Nome = nome; }
        public override string ToString() => Nome;
    }

    public CadastroForm(AgentConfig config)
    {
        _config = config;
        Text = "GDesk Agente — Alterar cadastro";
        Width = 400;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var y = 16;
        Controls.Add(new Label { Text = "Para alterar o cadastro, entre com um usuário agente ou administrador do GDesk.", AutoSize = false, Left = 20, Top = y, Width = 340, Height = 34 });
        y += 40;
        _campoEmail.Top = y; Controls.Add(_campoEmail); y += 30;
        _campoSenha.Top = y; Controls.Add(_campoSenha); y += 34;
        _botaoEntrar.Top = y; Controls.Add(_botaoEntrar); y += 42;

        AdicionarEdicao("Cliente", _comboCliente, ref y);
        AdicionarEdicao("Setor", _comboSetor, ref y);
        AdicionarEdicao("Subsetor", _comboSubsetor, ref y);
        AdicionarEdicao("Patrimônio", _campoPatrimonio, ref y);
        AdicionarEdicao("Nº do lacre", _campoLacre, ref y);

        _botaoSalvar.Top = y; _botaoCancelar.Top = y;
        Controls.Add(_botaoSalvar); Controls.Add(_botaoCancelar);
        _controlesEdicao.Add(_botaoSalvar);
        y += 38;
        _rotuloStatus.Top = y; Controls.Add(_rotuloStatus);
        y += 48;
        Height = y + 40;

        foreach (var c in _controlesEdicao) c.Enabled = false;

        _comboSetor.SelectedIndexChanged += (_, _) => { if (!_carregando) PopularSubsetores(null); };
        _botaoEntrar.Click += async (_, _) => await EntrarAsync();
        _botaoSalvar.Click += async (_, _) => await SalvarAsync();
        _botaoCancelar.Click += (_, _) => Close();
        AcceptButton = _botaoEntrar;
    }

    private void AdicionarEdicao(string rotulo, Control controle, ref int y)
    {
        Controls.Add(new Label { Text = rotulo, AutoSize = true, Left = 20, Top = y });
        y += 18;
        controle.Top = y;
        Controls.Add(controle);
        _controlesEdicao.Add(controle);
        y += 30;
    }

    private void Status(string texto, Color cor)
    {
        _rotuloStatus.ForeColor = cor;
        _rotuloStatus.Text = texto;
    }

    private async Task EntrarAsync()
    {
        _email = _campoEmail.Text.Trim();
        _senha = _campoSenha.Text;
        if (_email.Length == 0 || _senha.Length == 0)
        {
            Status("Informe e-mail e senha.", Color.Firebrick);
            return;
        }

        _botaoEntrar.Enabled = false;
        Status("Verificando...", Color.Gray);
        try
        {
            var api = new ApiClient(_config);
            var (ok, cadastro, msg) = await api.AutorizarCadastroAsync(_identificador, _email, _senha);
            if (!ok || cadastro == null)
            {
                _senha = "";
                Status(msg, Color.Firebrick);
                return;
            }

            var (okEstado, estado, _) = await api.ObterEstadoAsync(_identificador);
            _setores = okEstado && estado != null ? estado.Setores : new();

            _carregando = true;
            _comboCliente.Items.Clear();
            foreach (var c in cadastro.Clientes) _comboCliente.Items.Add(new ItemCombo(c.Id, c.Nome));
            foreach (var item in _comboCliente.Items.Cast<ItemCombo>()) if (item.Id == cadastro.ClienteId) _comboCliente.SelectedItem = item;

            _comboSetor.Items.Clear();
            foreach (var s in _setores.Where(s => string.IsNullOrEmpty(s.SetorPaiId)).OrderBy(s => s.Nome)) _comboSetor.Items.Add(new ItemCombo(s.Id, s.Nome));
            var atual = _setores.FirstOrDefault(s => s.Id == cadastro.SetorId);
            var principalId = atual == null ? null : (string.IsNullOrEmpty(atual.SetorPaiId) ? atual.Id : atual.SetorPaiId);
            var subId = atual != null && !string.IsNullOrEmpty(atual.SetorPaiId) ? atual.Id : null;
            foreach (var item in _comboSetor.Items.Cast<ItemCombo>()) if (item.Id == principalId) _comboSetor.SelectedItem = item;
            PopularSubsetores(subId);
            _carregando = false;

            _campoPatrimonio.Text = cadastro.Patrimonio ?? "";
            _campoLacre.Text = cadastro.NumeroLacre ?? "";

            foreach (var c in _controlesEdicao) c.Enabled = true;
            _comboSubsetor.Enabled = _comboSubsetor.Items.Count > 0;
            _campoEmail.Enabled = false;
            _campoSenha.Enabled = false;
            AcceptButton = _botaoSalvar;
            Status($"Autorizado por {cadastro.UsuarioNome}. Altere os dados e clique em Salvar.", Color.DarkGreen);
        }
        finally
        {
            _carregando = false;
            _botaoEntrar.Enabled = _campoEmail.Enabled;
        }
    }

    private void PopularSubsetores(string? selecionarId)
    {
        _comboSubsetor.Items.Clear();
        _comboSubsetor.Enabled = false;
        if (_comboSetor.SelectedItem is not ItemCombo setor) return;
        foreach (var f in _setores.Where(s => s.SetorPaiId == setor.Id).OrderBy(s => s.Nome)) _comboSubsetor.Items.Add(new ItemCombo(f.Id, f.Nome));
        if (_comboSubsetor.Items.Count == 0) return;
        _comboSubsetor.Enabled = _botaoSalvar.Enabled;
        foreach (var item in _comboSubsetor.Items.Cast<ItemCombo>()) if (item.Id == selecionarId) _comboSubsetor.SelectedItem = item;
    }

    private async Task SalvarAsync()
    {
        if (_comboSubsetor.Items.Count > 0 && _comboSubsetor.SelectedItem == null)
        {
            Status("Escolha o subsetor.", Color.Firebrick);
            return;
        }

        var clienteId = (_comboCliente.SelectedItem as ItemCombo)?.Id;
        var setorId = (_comboSubsetor.SelectedItem as ItemCombo)?.Id ?? (_comboSetor.SelectedItem as ItemCombo)?.Id;
        var patrimonio = _campoPatrimonio.Text.Trim();
        var lacre = _campoLacre.Text.Trim();

        _botaoSalvar.Enabled = false;
        Status("Salvando...", Color.Gray);
        try
        {
            var api = new ApiClient(_config);
            var (ok, cadastro, msg) = await api.AlterarCadastroAsync(_identificador, _email, _senha, clienteId, setorId, patrimonio, lacre);
            if (!ok || cadastro == null)
            {
                Status(msg, Color.Firebrick);
                return;
            }

            // Guarda local pra as próximas sincronizações não reverterem.
            AgentConfig.GravarCadastroManual(new AgentConfig.CadastroManual
            {
                ClienteId = cadastro.ClienteId,
                SetorId = cadastro.SetorId,
                Patrimonio = cadastro.Patrimonio,
                NumeroLacre = cadastro.NumeroLacre,
            });
            LogLocal.Registrar("INFO", $"Cadastro alterado no painel do agente por {cadastro.UsuarioNome} (cliente/setor/patrimônio/lacre).");

            await api.SincronizarAsync(InventoryCollector.ColetarComConfig(_config));
            MessageBox.Show(this, "Cadastro atualizado.", "GDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        finally
        {
            _botaoSalvar.Enabled = true;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _senha = "";
        base.OnFormClosed(e);
    }
}
