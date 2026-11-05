using System.Globalization;
using System.Security.Principal;

namespace GDeskAgent;

/// <summary>
/// 1.10.2+: a Tarefa Agendada (SYSTEM) roda a cada 10 min, mas a
/// sincronização COMPLETA (inventário + GDesk Remoto + verificação de
/// atualização) só acontece quando:
///   - passou 6h desde a última completa que deu certo, ou
///   - o GDesk pediu ("Sincronizar agora" / "Sincronizar todos" na tela de
///     Recursos -- GET /agente/pedido-sincronizacao), ou
///   - o agente foi chamado com --forcar.
/// No resto das execuções só pergunta ao servidor se há pedido e sai.
/// </summary>
public static class AgendamentoSync
{
    private static readonly TimeSpan IntervaloCompleto = TimeSpan.FromHours(6);
    private static string CaminhoUltimaCompleta => Path.Combine(Instalacao.Pasta, "ultima-sync-completa.txt");
    private static string CaminhoVersaoTarefa => Path.Combine(Instalacao.Pasta, "tarefa-versao.txt");

    public static bool EhAdministrador()
    {
        try
        {
            using var identidade = WindowsIdentity.GetCurrent();
            return identidade.IsSystem || new WindowsPrincipal(identidade).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Máquinas que já tinham o agente: a atualização automática só troca o
    /// .exe, não regrava a Tarefa Agendada -- aqui ela é regravada uma vez por
    /// versão (pega o intervalo novo de 10 min).
    /// </summary>
    public static void GarantirTarefaAtualizada()
    {
        if (!EhAdministrador()) return;
        try
        {
            var gravada = File.Exists(CaminhoVersaoTarefa) ? File.ReadAllText(CaminhoVersaoTarefa).Trim() : "";
            if (gravada == InventoryCollector.VersaoAgente) return;
            SelfInstaller.RegistrarTarefaAgendada(apagarAntes: false);
            File.WriteAllText(CaminhoVersaoTarefa, InventoryCollector.VersaoAgente);
            LogLocal.Registrar("INFO", "Tarefa agendada atualizada (verificação a cada 10 min).");
        }
        catch (Exception ex)
        {
            LogLocal.Registrar("ERRO", $"Não consegui atualizar a tarefa agendada: {ex.Message}");
        }
    }

    /// <summary>
    /// 1.10.3+: o ícone da bandeja pode sumir (atualização do agente fecha o
    /// processo e a reabertura falha, alguém fecha no Gerenciador de Tarefas,
    /// travamento...) e só voltava no próximo logon. A tarefa SYSTEM, a cada
    /// 10 min, confere: tem usuário logado (explorer.exe numa sessão
    /// interativa) e nenhum GDeskAgent rodando nessa sessão? Então dispara a
    /// tarefa "GDesk Agente - Bandeja" de novo.
    /// </summary>
    public static void GarantirBandeja()
    {
        if (!EhAdministrador()) return;
        try
        {
            var sessoesComUsuario = System.Diagnostics.Process.GetProcessesByName("explorer")
                .Select(p => { try { return p.SessionId; } catch { return 0; } })
                .Where(id => id > 0)
                .ToHashSet();
            if (sessoesComUsuario.Count == 0) return;
            var sessoesComBandeja = System.Diagnostics.Process.GetProcessesByName("GDeskAgent")
                .Select(p => { try { return p.SessionId; } catch { return 0; } })
                .Where(id => id > 0)
                .ToHashSet();
            if (sessoesComUsuario.All(sessoesComBandeja.Contains)) return;

            // No máximo 1 tentativa por hora (ex.: máquina com outro usuário
            // logado, pra quem a tarefa da bandeja não roda -- não fica
            // tentando e enchendo o log a cada 10 min).
            var marcador = Path.Combine(Instalacao.Pasta, "bandeja-reaberta.txt");
            if (File.Exists(marcador) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marcador) < TimeSpan.FromHours(1)) return;
            File.WriteAllText(marcador, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            using var processo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "schtasks.exe", "/Run /TN \"GDesk Agente - Bandeja\"")
            { UseShellExecute = false, CreateNoWindow = true });
            processo?.WaitForExit(15000);
            LogLocal.Registrar("INFO", "Ícone da bandeja não estava aberto -- reaberto.");
        }
        catch (Exception ex)
        {
            LogLocal.Registrar("ERRO", $"Não consegui reabrir o ícone da bandeja: {ex.Message}");
        }
    }

    public static async Task<bool> PrecisaSincronizarAsync(AgentConfig config)
    {
        var ultima = LerUltimaCompleta();
        if (ultima == null || DateTime.UtcNow - ultima.Value >= IntervaloCompleto) return true;
        try
        {
            var identificador = InventoryCollector.ObterIdentificadorAgente();
            if (string.IsNullOrWhiteSpace(identificador)) return false;
            var pedido = await new ApiClient(config).ConsultarPedidoSincronizacaoAsync(identificador);
            if (pedido) LogLocal.Registrar("INFO", "Pedido de sincronização recebido do sistema (GDesk).");
            return pedido;
        }
        catch
        {
            return false;
        }
    }

    public static void RegistrarCompleta()
    {
        try { File.WriteAllText(CaminhoUltimaCompleta, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)); }
        catch { /* sem permissão (rodando como usuário comum) -- a tarefa SYSTEM grava */ }
    }

    private static DateTime? LerUltimaCompleta()
    {
        try
        {
            if (!File.Exists(CaminhoUltimaCompleta)) return null;
            return DateTime.Parse(File.ReadAllText(CaminhoUltimaCompleta).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }
}
