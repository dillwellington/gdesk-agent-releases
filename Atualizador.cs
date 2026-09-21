using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;

namespace GDeskAgent;

/// <summary>
/// Atualização automática do agente. Fluxo:
///
/// 1. A tarefa agendada (SYSTEM, a cada 6h) sincroniza e depois chama
///    TentarAtualizarAsync: pergunta ao servidor (GET /agente/atualizacao)
///    se há versão mais nova. O servidor só diz "sim" quando o dono do
///    produto definiu AGENTE_VERSAO_ATUAL + AGENTE_SHA256 no Render.
/// 2. Baixa o .exe novo pra %ProgramData%\GDeskAgent\atualizacao\
///    GDeskAgentNovo.exe e confere o SHA-256 informado pelo servidor
///    (arquivo corrompido/adulterado é descartado, nada muda).
/// 3. Roda o .exe novo com --aplicar-atualizacao e encerra: o novo
///    processo (ver AplicarAtualizacao) espera o antigo sair, fecha a
///    bandeja, copia a si mesmo por cima de GDeskAgent.exe (com cópia de
///    segurança .bak, restaurada se a troca falhar) e reinicia tudo.
///
/// Só roda com privilégio de administrador (a tarefa agendada, SYSTEM):
/// a bandeja (usuário comum) nunca atualiza -- ProgramData não é gravável
/// por ela. Tudo é best-effort e vai pro log local; uma falha nunca
/// derruba a sincronização.
/// </summary>
public static class Atualizador
{
    private const string NomeProcesso = "GDeskAgent";
    private const long TamanhoMinimoBytes = 50_000_000; // o binário self-contained passa de 150 MB

    private const string TarefaAtualizacao = "GDesk Agente - Atualizacao";
    private const string TarefaSincronizacao = "GDesk Agente - Sincronizacao";

    private static void Schtasks(string argumentos)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", argumentos) { UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit(30000);
    }

    private static string PastaAtualizacao => Path.Combine(Instalacao.Pasta, "atualizacao");
    private static string CaminhoNovo => Path.Combine(PastaAtualizacao, "GDeskAgentNovo.exe");

    private static bool EhAdministrador()
    {
        try
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Devolve true se disparou a atualização (o processo atual deve encerrar logo em seguida).</summary>
    public static async Task<bool> TentarAtualizarAsync(AgentConfig config)
    {
        try
        {
            var exeAtual = Environment.ProcessPath;
            if (exeAtual == null || !EhAdministrador()) return false;
            // Só o binário instalado se atualiza (nunca um .exe solto rodando de outra pasta).
            if (!string.Equals(Path.GetFullPath(exeAtual), Path.GetFullPath(Instalacao.CaminhoExe), StringComparison.OrdinalIgnoreCase)) return false;

            // Resto de uma atualização anterior (tarefa temporária).
            Schtasks($"/Delete /TN \"{TarefaAtualizacao}\" /F");

            var info = await new ApiClient(config).VerificarAtualizacaoAsync(InventoryCollector.VersaoAgente);
            if (info == null) return false;

            LogLocal.Registrar("INFO", $"Atualização disponível: {InventoryCollector.VersaoAgente} -> {info.Versao}. Baixando...");

            try { if (Directory.Exists(PastaAtualizacao)) Directory.Delete(PastaAtualizacao, recursive: true); } catch { /* resto de tentativa anterior */ }
            Directory.CreateDirectory(PastaAtualizacao);

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
            using (var resposta = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                resposta.EnsureSuccessStatusCode();
                await using var origem = await resposta.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var destino = File.Create(CaminhoNovo);
                await origem.CopyToAsync(destino).ConfigureAwait(false);
            }

            var tamanho = new FileInfo(CaminhoNovo).Length;
            string hash;
            await using (var fluxo = File.OpenRead(CaminhoNovo))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(fluxo).ConfigureAwait(false)).ToLowerInvariant();
            }
            if (tamanho < TamanhoMinimoBytes || !string.Equals(hash, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                LogLocal.Registrar("ERRO", $"Atualização descartada: arquivo baixado não confere (tamanho {tamanho} bytes, SHA-256 {hash}). Nada foi alterado.");
                try { File.Delete(CaminhoNovo); } catch { }
                return false;
            }

            LogLocal.Registrar("INFO", $"Atualização {info.Versao} baixada e verificada. Aplicando...");
            // Roda como tarefa agendada própria (SYSTEM), fora da árvore desta
            // execução: o Agendador pode encerrar processos filhos quando esta
            // tarefa terminar, e a etapa de troca precisa sobreviver a isso.
            Schtasks($"/Create /TN \"{TarefaAtualizacao}\" /TR \"\\\"{CaminhoNovo}\\\" --aplicar-atualizacao\" /SC ONCE /ST 00:00 /RU SYSTEM /RL HIGHEST /F");
            Schtasks($"/Run /TN \"{TarefaAtualizacao}\"");
            return true;
        }
        catch (Exception ex)
        {
            LogLocal.Registrar("ERRO", $"Falha ao atualizar o agente: {ex.Message}. A versão atual continua instalada.");
            return false;
        }
    }

    /// <summary>
    /// Executado pelo .exe NOVO (--aplicar-atualizacao): troca o GDeskAgent.exe
    /// instalado por este arquivo. Devolve o código de saída do processo.
    /// </summary>
    public static int AplicarAtualizacao()
    {
        var eu = Environment.ProcessPath!;
        var alvo = Instalacao.CaminhoExe;
        var backup = alvo + ".bak";
        try
        {
            // Encerra o agente antigo (sincronização em curso) e a bandeja.
            var meuPid = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName(NomeProcesso))
            {
                if (p.Id == meuPid) continue;
                try { p.Kill(entireProcessTree: false); p.WaitForExit(15000); } catch { /* já saiu */ }
            }

            var copiado = false;
            Exception? ultimoErro = null;
            for (var tentativa = 0; tentativa < 20 && !copiado; tentativa++)
            {
                try
                {
                    if (File.Exists(alvo)) File.Copy(alvo, backup, overwrite: true);
                    File.Copy(eu, alvo, overwrite: true);
                    copiado = true;
                }
                catch (Exception ex)
                {
                    ultimoErro = ex;
                    Thread.Sleep(1500);
                }
            }

            if (!copiado)
            {
                RestaurarBackup(alvo, backup);
                LogLocal.Registrar("ERRO", $"Atualização não aplicada (não foi possível substituir o executável: {ultimoErro?.Message}). Versão anterior mantida.");
                return 1;
            }

            // Reinicia o agente (sincroniza já com a versão nova) e a bandeja.
            LogLocal.Registrar("OK", "Agente atualizado com sucesso. Reiniciando.");
            try { Schtasks($"/Run /TN \"{TarefaSincronizacao}\""); } catch { }
            try { Schtasks("/Run /TN \"GDesk Agente - Bandeja\""); } catch { /* a bandeja volta no próximo login */ }
            return 0;
        }
        catch (Exception ex)
        {
            RestaurarBackup(alvo, backup);
            LogLocal.Registrar("ERRO", $"Falha ao aplicar a atualização: {ex.Message}. Versão anterior mantida.");
            return 1;
        }
    }

    private static void RestaurarBackup(string alvo, string backup)
    {
        try
        {
            if (File.Exists(backup) && (!File.Exists(alvo) || new FileInfo(alvo).Length < TamanhoMinimoBytes))
            {
                File.Copy(backup, alvo, overwrite: true);
            }
        }
        catch { /* melhor esforço */ }
    }
}
