using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace GDeskAgent;

/// <summary>
/// Mostra o log local do agente (LogLocal): cada tentativa de
/// sincronização, se gravou no GDesk ou não, e o motivo da falha (sem
/// conexão com o servidor, patrimônio duplicado, token inválido...).
/// Serve pra quem está na máquina entender por que o equipamento não
/// apareceu/atualizou no sistema, sem depender de acesso ao GDesk.
/// </summary>
public sealed class LogAgenteForm : Form
{
    private readonly AgentConfig _config;
    private readonly TextBox _caixa = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 9F),
        Dock = DockStyle.Fill,
        BackColor = Color.White,
    };
    private readonly Label _resumo = new() { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 8, 8, 0) };
    private readonly Button _botaoSincronizar = new() { Text = "Sincronizar agora", Width = 140, Height = 30 };
    private readonly Button _botaoAtualizar = new() { Text = "Atualizar", Width = 90, Height = 30 };
    private readonly Button _botaoPasta = new() { Text = "Abrir pasta do log", Width = 140, Height = 30 };
    private readonly Button _botaoFechar = new() { Text = "Fechar", Width = 90, Height = 30 };

    public LogAgenteForm(AgentConfig config)
    {
        _config = config;

        Text = "GDesk Agente — Log de sincronização";
        Width = 820;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;

        var rodape = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8, 8, 8, 0), FlowDirection = FlowDirection.LeftToRight };
        rodape.Controls.AddRange(new Control[] { _botaoSincronizar, _botaoAtualizar, _botaoPasta, _botaoFechar });

        Controls.Add(_caixa);
        Controls.Add(_resumo);
        Controls.Add(rodape);

        _botaoAtualizar.Click += (_, _) => Recarregar();
        _botaoFechar.Click += (_, _) => Close();
        _botaoPasta.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(LogLocal.PastaLogs);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogLocal.PastaLogs}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "GDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        _botaoSincronizar.Click += async (_, _) =>
        {
            _botaoSincronizar.Enabled = false;
            _resumo.ForeColor = Color.Gray;
            _resumo.Text = "Sincronizando...";
            try
            {
                await new ApiClient(_config).SincronizarAsync(InventoryCollector.ColetarComConfig(_config));
            }
            finally
            {
                _botaoSincronizar.Enabled = true;
                Recarregar();
            }
        };

        Load += (_, _) => Recarregar();
    }

    private void Recarregar()
    {
        var resultado = LogLocal.UltimoResultado();
        if (resultado == null)
        {
            _resumo.ForeColor = Color.Gray;
            _resumo.Text = "Nenhuma sincronização registrada ainda nesta máquina.";
        }
        else if (resultado.Value.sucesso)
        {
            _resumo.ForeColor = Color.DarkGreen;
            _resumo.Text = "Última tentativa: gravada no GDesk com sucesso.\n" + resultado.Value.linha;
        }
        else
        {
            _resumo.ForeColor = Color.Firebrick;
            _resumo.Text = "Última tentativa FALHOU — o inventário não foi gravado no GDesk.\n" + resultado.Value.linha;
        }

        var linhas = LogLocal.LerUltimasLinhas();
        _caixa.Text = linhas.Count == 0
            ? "(log vazio)"
            : string.Join(Environment.NewLine, linhas);
    }
}
