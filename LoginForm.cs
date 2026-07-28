using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace GDeskAgent;

/// <summary>
/// Janela simples exibida por "GDeskAgent.exe --abrir-chamado": pede
/// e-mail/senha (mesma conta usada no site do GDesk), valida contra o
/// backend (POST /auth/login-agente) e, se der certo, abre o navegador
/// padrão já autenticado -- sem o usuário precisar digitar a senha de novo
/// lá. A senha nunca é salva em disco nem enviada para lugar nenhum além
/// dessa chamada HTTPS direta ao backend.
/// </summary>
public sealed class LoginForm : Form
{
    private readonly TextBox _campoEmail = new() { PlaceholderText = "seu.email@empresa.com" };
    private readonly TextBox _campoSenha = new() { PasswordChar = '•', UseSystemPasswordChar = true };
    private readonly Button _botaoEntrar = new() { Text = "Entrar e abrir chamado" };
    private readonly Label _rotuloErro = new() { ForeColor = Color.Firebrick, AutoSize = false };
    private readonly AgentConfig _config;

    public LoginForm(AgentConfig config)
    {
        _config = config;

        Text = "GDesk — Abrir chamado";
        Width = 380;
        Height = 320;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var rotuloEmail = new Label { Text = "E-mail", AutoSize = true, Left = 20, Top = 20 };
        _campoEmail.Left = 20;
        _campoEmail.Top = 42;
        _campoEmail.Width = 320;

        var rotuloSenha = new Label { Text = "Senha", AutoSize = true, Left = 20, Top = 78 };
        _campoSenha.Left = 20;
        _campoSenha.Top = 100;
        _campoSenha.Width = 320;

        _rotuloErro.Left = 20;
        _rotuloErro.Top = 134;
        _rotuloErro.Width = 320;
        _rotuloErro.Height = 46;

        _botaoEntrar.Left = 20;
        _botaoEntrar.Top = 190;
        _botaoEntrar.Width = 320;
        _botaoEntrar.Height = 34;
        _botaoEntrar.Click += BotaoEntrar_Click;

        AcceptButton = _botaoEntrar;

        var rotuloAjuda = new Label
        {
            Text = "Use o mesmo e-mail e senha da sua conta no portal do GDesk.",
            AutoSize = false,
            Left = 20,
            Top = 232,
            Width = 320,
            Height = 40,
            ForeColor = Color.Gray,
            Font = new Font(Font.FontFamily, 8.25f),
        };

        Controls.Add(rotuloEmail);
        Controls.Add(_campoEmail);
        Controls.Add(rotuloSenha);
        Controls.Add(_campoSenha);
        Controls.Add(_rotuloErro);
        Controls.Add(_botaoEntrar);
        Controls.Add(rotuloAjuda);
    }

    private async void BotaoEntrar_Click(object? remetente, EventArgs evento)
    {
        _rotuloErro.Text = "";

        var email = _campoEmail.Text.Trim();
        var senha = _campoSenha.Text;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(senha))
        {
            _rotuloErro.Text = "Preencha e-mail e senha.";
            return;
        }

        _botaoEntrar.Enabled = false;
        _botaoEntrar.Text = "Entrando...";

        var cliente = new AuthClient(_config.ApiBaseUrl);
        var (sucesso, resultado) = await cliente.LoginAsync(email, senha);

        if (!sucesso)
        {
            _rotuloErro.Text = resultado;
            _botaoEntrar.Enabled = true;
            _botaoEntrar.Text = "Entrar e abrir chamado";
            return;
        }

        try
        {
            var url = $"{_config.PortalUrl.TrimEnd('/')}/login-agente.html?ticket={Uri.EscapeDataString(resultado)}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _rotuloErro.Text = $"Não foi possível abrir o navegador: {ex.Message}";
            _botaoEntrar.Enabled = true;
            _botaoEntrar.Text = "Entrar e abrir chamado";
            return;
        }

        Close();
    }
}
