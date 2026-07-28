using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GDeskAgent;

/// <summary>
/// Tela mostrada na primeira vez que o GDeskAgent.exe roda nesta máquina
/// (nenhum appsettings.json ainda em %ProgramData%\GDeskAgent -- ver
/// Instalacao.JaInstalado). Faz o resto sozinha: copia o .exe para
/// %ProgramData%\GDeskAgent, cria a Tarefa Agendada de sincronização e o
/// atalho "Abrir Chamado GDesk" no Menu Iniciar (ver SelfInstaller).
///
/// Quando o .exe foi baixado a partir de Minha Empresa ou de um Cliente
/// específico (ver ConfiguracaoEmbutida.cs), o token já vem pré-
/// preenchido (campo fica só-leitura) e a pessoa não precisa colar nada
/// -- e, se o cliente exigir, aparecem também os campos de etiqueta de
/// patrimônio e/ou número do lacre, obrigatórios antes de liberar o
/// botão Instalar. Sem configuração embutida (.exe genérico, ou renomeado
/// antes de rodar), a tela cai no fluxo antigo: só pede o token colado à
/// mão.
/// </summary>
public sealed class SetupForm : Form
{
    private readonly ConfiguracaoEmbutida? _configEmbutida;

    private readonly TextBox _campoToken = new() { PlaceholderText = "Cole aqui o token da empresa" };
    private readonly TextBox _campoPatrimonio = new() { PlaceholderText = "Ex.: PAT-00123" };
    private readonly TextBox _campoNumeroLacre = new() { PlaceholderText = "Ex.: LAC-45678" };
    private readonly Button _botaoInstalar = new() { Text = "Instalar" };
    private readonly Label _rotuloErro = new() { ForeColor = Color.Firebrick, AutoSize = false };

    public SetupForm(ConfiguracaoEmbutida? configEmbutida = null)
    {
        _configEmbutida = configEmbutida;
        var tokenDetectado = !string.IsNullOrWhiteSpace(_configEmbutida?.Token);

        Text = "GDesk — Instalar agente";
        Width = 400;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        // Layout sequencial de cima pra baixo (em vez de posições fixas)
        // porque o número de campos varia: token sempre aparece, mas
        // patrimônio/lacre só quando o cliente do download exige (ver
        // ConfiguracaoEmbutida.PatrimonioObrigatorio/NumeroLacreObrigatorio).
        var y = 18;

        var rotuloTitulo = new Label
        {
            Text = "Instalar o Agente GDesk nesta máquina",
            AutoSize = true,
            Left = 20,
            Top = y,
            Font = new Font(Font, FontStyle.Bold),
        };
        Controls.Add(rotuloTitulo);
        y += 38;

        var rotuloToken = new Label
        {
            Text = tokenDetectado ? "Token da empresa (detectado automaticamente)" : "Token da empresa",
            AutoSize = true,
            Left = 20,
            Top = y,
        };
        Controls.Add(rotuloToken);
        y += 22;

        _campoToken.Left = 20;
        _campoToken.Top = y;
        _campoToken.Width = 340;
        if (tokenDetectado)
        {
            _campoToken.Text = _configEmbutida!.Token;
            _campoToken.ReadOnly = true;
            _campoToken.TabStop = false;
            _campoToken.BackColor = SystemColors.Control;
        }
        Controls.Add(_campoToken);
        y += 30;

        if (tokenDetectado)
        {
            var rotuloDetectado = new Label
            {
                Text = _configEmbutida!.ClienteId != null
                    ? "Empresa e cliente detectados a partir do arquivo baixado."
                    : "Empresa detectada a partir do arquivo baixado.",
                AutoSize = false,
                Left = 20,
                Top = y,
                Width = 340,
                Height = 20,
                ForeColor = Color.Gray,
                Font = new Font(Font.FontFamily, 8.25f),
            };
            Controls.Add(rotuloDetectado);
            y += 26;
        }
        else
        {
            var rotuloAjuda = new Label
            {
                Text = "Encontre em: GDesk > Minha Empresa > Agente GDesk > Copiar.",
                AutoSize = false,
                Left = 20,
                Top = y,
                Width = 340,
                Height = 32,
                ForeColor = Color.Gray,
                Font = new Font(Font.FontFamily, 8.25f),
            };
            Controls.Add(rotuloAjuda);
            y += 38;

            // TEMPORÁRIO -- diagnóstico de por que a detecção automática
            // não achou nada (ver ConfiguracaoEmbutida.UltimoDiagnostico).
            // Remover depois que a causa for encontrada.
            var rotuloDiagnostico = new Label
            {
                Text = ConfiguracaoEmbutida.UltimoDiagnostico,
                AutoSize = false,
                Left = 20,
                Top = y,
                Width = 340,
                Height = 60,
                ForeColor = Color.Firebrick,
                Font = new Font(Font.FontFamily, 7.5f),
            };
            Controls.Add(rotuloDiagnostico);
            y += 64;
        }

        if (_configEmbutida?.PatrimonioObrigatorio == true)
        {
            var rotuloPatrimonio = new Label { Text = "Etiqueta de patrimônio *", AutoSize = true, Left = 20, Top = y };
            Controls.Add(rotuloPatrimonio);
            y += 22;

            _campoPatrimonio.Left = 20;
            _campoPatrimonio.Top = y;
            _campoPatrimonio.Width = 340;
            Controls.Add(_campoPatrimonio);
            y += 34;
        }

        if (_configEmbutida?.NumeroLacreObrigatorio == true)
        {
            var rotuloLacre = new Label { Text = "Número do lacre *", AutoSize = true, Left = 20, Top = y };
            Controls.Add(rotuloLacre);
            y += 22;

            _campoNumeroLacre.Left = 20;
            _campoNumeroLacre.Top = y;
            _campoNumeroLacre.Width = 340;
            Controls.Add(_campoNumeroLacre);
            y += 34;
        }

        _rotuloErro.Left = 20;
        _rotuloErro.Top = y;
        _rotuloErro.Width = 340;
        _rotuloErro.Height = 44;
        Controls.Add(_rotuloErro);
        y += 48;

        _botaoInstalar.Left = 20;
        _botaoInstalar.Top = y;
        _botaoInstalar.Width = 340;
        _botaoInstalar.Height = 34;
        _botaoInstalar.Click += BotaoInstalar_Click;
        AcceptButton = _botaoInstalar;
        Controls.Add(_botaoInstalar);
        y += _botaoInstalar.Height;

        // +54 aproxima a barra de título + borda da janela (FixedDialog) e
        // uma margem inferior -- mesma folga usada na tela original
        // (Height=280 pra um conteúdo terminando em y=226). Como este
        // projeto foi escrito sem o SDK do .NET disponível pra
        // compilar/testar (ver README.md), vale conferir visualmente na
        // primeira execução numa máquina Windows de verdade e ajustar se
        // sobrar/faltar espaço.
        Height = y + 54;
    }

    private async void BotaoInstalar_Click(object? remetente, EventArgs evento)
    {
        _rotuloErro.ForeColor = Color.Firebrick;
        _rotuloErro.Text = "";

        var token = _campoToken.Text.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            _rotuloErro.Text = "Cole o token da empresa antes de continuar.";
            return;
        }

        var patrimonio = _campoPatrimonio.Text.Trim();
        if (_configEmbutida?.PatrimonioObrigatorio == true && string.IsNullOrWhiteSpace(patrimonio))
        {
            _rotuloErro.Text = "Preencha a etiqueta de patrimônio antes de continuar.";
            return;
        }

        var numeroLacre = _campoNumeroLacre.Text.Trim();
        if (_configEmbutida?.NumeroLacreObrigatorio == true && string.IsNullOrWhiteSpace(numeroLacre))
        {
            _rotuloErro.Text = "Preencha o número do lacre antes de continuar.";
            return;
        }

        _botaoInstalar.Enabled = false;
        _botaoInstalar.Text = "Instalando...";
        _rotuloErro.ForeColor = Color.Gray;
        _rotuloErro.Text = "Validando token e instalando... pode levar até 1 minuto na primeira vez (o servidor às vezes precisa \"acordar\").";

        try
        {
            // Roda a instalação (validação de token + elevação + cópia de
            // arquivos + Tarefa Agendada) numa thread separada -- ela é
            // toda síncrona/bloqueante (inclusive chamadas de rede), e
            // rodar isso direto na thread da tela travaria a fila de
            // mensagens do Windows, fazendo a janela aparecer como "Não
            // está respondendo" até terminar.
            await Task.Run(() => SelfInstaller.InstalarComElevacao(
                token,
                _configEmbutida?.ClienteId,
                string.IsNullOrWhiteSpace(patrimonio) ? null : patrimonio,
                string.IsNullOrWhiteSpace(numeroLacre) ? null : numeroLacre));
            MessageBox.Show(
                "Agente GDesk instalado com sucesso!\n\nProcure \"Abrir Chamado GDesk\" no Menu Iniciar sempre que precisar registrar um chamado.",
                "GDesk",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            _rotuloErro.ForeColor = Color.Firebrick;
            _rotuloErro.Text = ex.Message;
            _botaoInstalar.Enabled = true;
            _botaoInstalar.Text = "Instalar";
        }
    }
}
