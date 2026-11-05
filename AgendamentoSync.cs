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
