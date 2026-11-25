using System.Diagnostics;
using System.Management;

namespace GDeskAgent;

/// <summary>
/// 1.10.11+: descobre em quais sessões do Windows o ícone da bandeja
/// (GDeskAgent.exe --bandeja) está de fato aberto. Antes contava qualquer
/// GDeskAgent.exe na sessão -- com o Painel (--painel) aberto, a bandeja
/// parecia aberta e nunca era reaberta.
/// </summary>
public static class BandejaSessao
{
    /// <summary>Nome do mutex que garante um ícone só por sessão ("Local\" = por sessão).</summary>
    public const string NomeMutex = @"Local\GDeskAgent-Bandeja";

    /// <summary>
    /// Sessões interativas (id &gt; 0) com um processo "--bandeja" rodando.
    /// Rodando como usuário comum, o WMI só devolve a linha de comando dos
    /// próprios processos -- suficiente pra conferir a sessão atual.
    /// Se a consulta falhar, devolve vazio (quem chama tenta abrir; o mutex
    /// em Program.cs impede ícone duplicado).
    /// </summary>
    public static HashSet<int> SessoesComBandeja()
    {
        var sessoes = new HashSet<int>();
        try
        {
            using var busca = new ManagementObjectSearcher(
                "SELECT SessionId, CommandLine FROM Win32_Process WHERE Name = 'GDeskAgent.exe'");
            foreach (var item in busca.Get())
            {
                using (item)
                {
                    var linha = item["CommandLine"] as string ?? "";
                    if (!linha.Contains("--bandeja", StringComparison.OrdinalIgnoreCase)) continue;
                    var sessao = Convert.ToInt32(item["SessionId"]);
                    if (sessao > 0) sessoes.Add(sessao);
                }
            }
        }
        catch
        {
            // Best-effort -- ver comentário acima.
        }
        return sessoes;
    }

    /// <summary>
    /// Abre o ícone da bandeja na sessão atual se ainda não estiver aberto.
    /// Chamado ao abrir o Painel pelo Menu Iniciar: assim, fechar ("minimizar
    /// para a bandeja") o Painel sempre deixa o ícone perto do relógio.
    /// </summary>
    public static void GarantirNaSessaoAtual()
    {
        try
        {
            var sessaoAtual = Process.GetCurrentProcess().SessionId;
            if (SessoesComBandeja().Contains(sessaoAtual)) return;
            Process.Start(new ProcessStartInfo(Instalacao.CaminhoExe, "--bandeja") { UseShellExecute = true });
        }
        catch
        {
            // Não crítico: a tarefa agendada reabre no próximo login / em até 1h.
        }
    }
}
