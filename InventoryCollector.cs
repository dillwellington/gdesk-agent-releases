using System.Management;
using Microsoft.Win32;

namespace GDeskAgent;

/// <summary>
/// Coleta o inventário da máquina via WMI/CIM e registro do Windows.
/// Cada bloco é isolado (try/catch próprio): se uma classe WMI falhar
/// (driver ausente, permissão, namespace inexistente nessa versão do
/// Windows, etc.), só aquele campo específico fica nulo -- nunca derruba a
/// sincronização inteira.
/// </summary>
public static class InventoryCollector
{
    public const string VersaoAgente = "1.2.0";

    public static SincronizarPayload Coletar()
    {
        var payload = new SincronizarPayload
        {
            IdentificadorAgente = ObterMachineGuid(),
            Hostname = Environment.MachineName,
            VersaoAgente = VersaoAgente,
            UsuarioLogado = Environment.UserName,
            Arquitetura = Environment.Is64BitOperatingSystem ? "x64" : "x86",
        };

        Tentar(() => PreencherComputerSystem(payload));
        Tentar(() => PreencherBios(payload));
        Tentar(() => PreencherProcessador(payload));
        Tentar(() => PreencherMemoria(payload));
        Tentar(() => PreencherSistemaOperacional(payload));
        Tentar(() => PreencherArmazenamento(payload));
        Tentar(() => PreencherVideo(payload));
        Tentar(() => PreencherRede(payload));
        Tentar(() => PreencherAtivacaoWindows(payload));
        Tentar(() => PreencherProgramasInstalados(payload));

        return payload;
    }

    /// <summary>
    /// Igual a Coletar(), mas já mesclando cliente_id/patrimonio/
    /// numero_lacre salvos em AgentConfig (preenchidos na instalação só
    /// quando o download foi personalizado por cliente -- ver
    /// ConfiguracaoEmbutida.cs e SelfInstaller.ExecutarInstalacaoElevada).
    /// Usada em toda sincronização (Program.cs e
    /// SelfInstaller.SincronizarAgora), não só na primeira: reenviar é
    /// inofensivo -- o backend só grava patrimonio/numero_lacre uma vez,
    /// se ainda estiverem vazios (ver app/routers/agente.py), e cliente_id
    /// só é aplicado na criação do Recurso.
    /// </summary>
    public static SincronizarPayload ColetarComConfig(AgentConfig config)
    {
        var payload = Coletar();
        if (!string.IsNullOrWhiteSpace(config.ClienteId)) payload.ClienteId = config.ClienteId;
        if (!string.IsNullOrWhiteSpace(config.Patrimonio)) payload.Patrimonio = config.Patrimonio;
        if (!string.IsNullOrWhiteSpace(config.NumeroLacre)) payload.NumeroLacre = config.NumeroLacre;
        return payload;
    }

    private static void Tentar(Action acao)
    {
        try { acao(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GDeskAgent] Aviso: falha ao coletar um campo de inventário: {ex.Message}");
        }
    }

    private static string ObterMachineGuid()
    {
        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var valor = chave?.GetValue("MachineGuid") as string;
            if (!string.IsNullOrWhiteSpace(valor)) return valor;
        }
        catch { /* segue para o fallback abaixo */ }

        // Fallback (não deveria acontecer em Windows normal): usa o
        // hostname. Não é ideal -- muda se o PC for renomeado -- mas evita
        // que a sincronização falhe por completo por causa disso.
        return $"HOSTNAME-{Environment.MachineName}";
    }

    private static List<ManagementObject> Consultar(string wql, string namespacePath = @"root\cimv2")
    {
        var resultado = new List<ManagementObject>();
        using var searcher = new ManagementObjectSearcher(namespacePath, wql);
        foreach (var obj in searcher.Get())
        {
            resultado.Add((ManagementObject)obj);
        }
        return resultado;
    }

    private static void PreencherComputerSystem(SincronizarPayload payload)
    {
        foreach (var cs in Consultar("SELECT Manufacturer, Model, Domain, PartOfDomain FROM Win32_ComputerSystem"))
        {
            payload.Fabricante = cs["Manufacturer"]?.ToString()?.Trim();
            payload.Modelo = cs["Model"]?.ToString()?.Trim();
            var partOfDomain = cs["PartOfDomain"] as bool?;
            payload.DominioRede = partOfDomain == true ? cs["Domain"]?.ToString() : null;
        }

        payload.TipoDispositivo = DeterminarTipoDispositivo();
    }

    private static string DeterminarTipoDispositivo()
    {
        // Códigos de ChassisTypes (Win32_SystemEnclosure) considerados
        // notebook/portátil. Qualquer outro valor (ou falha na consulta)
        // é tratado como "desktop" -- o admin pode corrigir manualmente
        // depois no cadastro do Recurso, isso é só uma sugestão inicial.
        var portateis = new HashSet<ushort> { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };
        try
        {
            foreach (var enclosure in Consultar("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
            {
                if (enclosure["ChassisTypes"] is ushort[] tipos)
                {
                    foreach (var tipo in tipos)
                    {
                        if (portateis.Contains(tipo)) return "notebook";
                    }
                }
            }
        }
        catch { /* segue com "desktop" como padrão */ }
        return "desktop";
    }

    private static void PreencherBios(SincronizarPayload payload)
    {
        foreach (var bios in Consultar("SELECT SerialNumber FROM Win32_BIOS"))
        {
            payload.NumeroSerie = bios["SerialNumber"]?.ToString()?.Trim();
        }
    }

    private static void PreencherProcessador(SincronizarPayload payload)
    {
        var nucleos = 0;
        string? nome = null;
        foreach (var cpu in Consultar("SELECT Name, NumberOfCores FROM Win32_Processor"))
        {
            nome ??= cpu["Name"]?.ToString()?.Trim();
            if (cpu["NumberOfCores"] is uint cores) nucleos += (int)cores;
        }
        payload.Processador = nome;
        payload.NucleosProcessador = nucleos > 0 ? nucleos : null;
    }

    private static void PreencherMemoria(SincronizarPayload payload)
    {
        ulong totalBytes = 0;
        foreach (var mem in Consultar("SELECT Capacity FROM Win32_PhysicalMemory"))
        {
            if (ulong.TryParse(mem["Capacity"]?.ToString(), out var capacidade))
            {
                totalBytes += capacidade;
            }
        }
        if (totalBytes > 0)
        {
            payload.MemoriaRamGb = Math.Round(totalBytes / 1024m / 1024m / 1024m, 2);
        }
    }

    private static void PreencherSistemaOperacional(SincronizarPayload payload)
    {
        foreach (var so in Consultar("SELECT Caption, Version, InstallDate FROM Win32_OperatingSystem"))
        {
            payload.SistemaOperacional = so["Caption"]?.ToString()?.Trim();
            payload.VersaoSistemaOperacional = so["Version"]?.ToString()?.Trim();

            var installDateRaw = so["InstallDate"]?.ToString();
            if (!string.IsNullOrEmpty(installDateRaw))
            {
                try
                {
                    var data = ManagementDateTimeConverter.ToDateTime(installDateRaw);
                    payload.DataFormatacao = data.ToString("yyyy-MM-dd");
                }
                catch { /* formato inesperado, deixa nulo */ }
            }
        }
    }

    private static void PreencherArmazenamento(SincronizarPayload payload)
    {
        ulong totalBytes = 0, livreBytes = 0;
        string? rotulo = null;
        foreach (var disco in Consultar(
            "SELECT DeviceID, Size, FreeSpace, VolumeName FROM Win32_LogicalDisk WHERE DriveType = 3"))
        {
            // DriveType = 3 ("Local Disk") já tira unidade de rede,
            // pendrive e CD/DVD -- mas apps como Google Drive, Dropbox ou
            // OneDrive (modo unidade virtual) também aparecem como
            // DriveType 3, mesmo não sendo um disco físico de verdade.
            // Confirma com EhDiscoFisico (associação WMI com uma partição
            // real) antes de somar, senão o espaço "livre" de um desses
            // apps de nuvem entra na conta como se fosse espaço físico da
            // máquina.
            var deviceId = disco["DeviceID"]?.ToString();
            if (string.IsNullOrEmpty(deviceId) || !EhDiscoFisico(deviceId))
            {
                continue;
            }

            if (ulong.TryParse(disco["Size"]?.ToString(), out var tamanho)) totalBytes += tamanho;
            if (ulong.TryParse(disco["FreeSpace"]?.ToString(), out var livre)) livreBytes += livre;
            rotulo ??= disco["VolumeName"]?.ToString();
        }
        if (totalBytes > 0)
        {
            payload.ArmazenamentoTotalGb = Math.Round(totalBytes / 1024m / 1024m / 1024m, 2);
            payload.ArmazenamentoLivreGb = Math.Round(livreBytes / 1024m / 1024m / 1024m, 2);
        }
        payload.RotuloArmazenamento = string.IsNullOrWhiteSpace(rotulo) ? null : rotulo;

        // SSD x HDD só está disponível via MSFT_PhysicalDisk (namespace de
        // Storage), que não existe em todas as versões/edições do Windows
        // -- por isso fica isolado do resto e falha em silêncio quando
        // indisponível, em vez de derrubar a coleta de armazenamento acima.
        //
        // MediaType (3=HDD, 4=SSD) só descreve a MÍDIA (sólida x rotativa),
        // não a interface -- um disco NVMe também reporta MediaType=4,
        // porque fisicamente é um SSD, então checar só isso classificava
        // todo NVMe como "SSD" (era o que o usuário via, incorretamente).
        // BusType, por sua vez, descreve a interface física (17=NVMe,
        // 11=SATA etc.) -- por isso é checado primeiro: se vier NVMe, usa
        // esse valor (mais específico e mais útil); senão cai pro
        // MediaType (SSD/HDD) de sempre.
        try
        {
            foreach (var disco in Consultar(
                "SELECT MediaType, BusType FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
            {
                var busType = disco["BusType"]?.ToString();
                payload.TipoArmazenamento = busType switch
                {
                    "17" => "NVMe",
                    _ => disco["MediaType"]?.ToString() switch
                    {
                        "3" => "HDD",
                        "4" => "SSD",
                        _ => null,
                    },
                };
                if (payload.TipoArmazenamento != null) break;
            }
        }
        catch { /* namespace de Storage indisponível nesta máquina */ }
    }

    /// <summary>
    /// Confirma se uma unidade lógica (ex.: "C:") é mesmo um disco físico
    /// de verdade, verificando se existe uma partição real associada a
    /// ela (Win32_LogicalDiskToPartition). Unidades virtuais montadas por
    /// apps de sincronização de nuvem (Google Drive, Dropbox etc.) ou por
    /// ferramentas como "subst" aparecem como DriveType=3 no
    /// Win32_LogicalDisk (mesma classificação de um disco local real),
    /// mas não têm partição nenhuma por trás -- essa associação simplesmente
    /// não existe pra elas, e é isso que usamos aqui pra distinguir uma
    /// coisa da outra.
    /// </summary>
    private static bool EhDiscoFisico(string deviceId)
    {
        try
        {
            var particoes = Consultar(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{deviceId}'}} " +
                "WHERE AssocClass=Win32_LogicalDiskToPartition ResultClass=Win32_DiskPartition");
            return particoes.Count > 0;
        }
        catch
        {
            // Se a consulta falhar por qualquer motivo, trata como "não é
            // disco físico" por segurança -- é melhor deixar um disco
            // real de fora por engano (o admin percebe e corrige) do que
            // somar uma unidade de nuvem como se fosse espaço físico.
            return false;
        }
    }

    private static void PreencherVideo(SincronizarPayload payload)
    {
        foreach (var gpu in Consultar("SELECT Name, AdapterRAM FROM Win32_VideoController"))
        {
            payload.PlacaVideo = gpu["Name"]?.ToString()?.Trim();
            if (uint.TryParse(gpu["AdapterRAM"]?.ToString(), out var vram) && vram > 0)
            {
                // AdapterRAM é um campo de 32 bits: em muitas GPUs com 4 GB
                // ou mais de VRAM ele "estoura" e retorna um valor errado
                // (limitação conhecida do WMI, não deste agente). Quando
                // isso acontece o valor fica impreciso -- o admin pode
                // corrigir manualmente no cadastro do Recurso.
                payload.PlacaVideoVramGb = Math.Round(vram / 1024m / 1024m / 1024m, 2);
            }
            break; // usa só o primeiro adaptador (o principal)
        }
    }

    private static void PreencherRede(SincronizarPayload payload)
    {
        foreach (var nic in Consultar(
            "SELECT IPAddress, MACAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"))
        {
            if (nic["IPAddress"] is string[] ips && ips.Length > 0)
            {
                payload.EnderecoIp = ips.FirstOrDefault(ip => ip.Contains('.')) ?? ips[0];
            }
            payload.EnderecoMac = nic["MACAddress"]?.ToString();
            if (payload.EnderecoIp != null) break;
        }
    }

    private static void PreencherAtivacaoWindows(SincronizarPayload payload)
    {
        // ApplicationID fixo do Windows (mesmo em qualquer edição/idioma) --
        // é o valor documentado da Microsoft para SoftwareLicensingProduct.
        const string applicationIdWindows = "55c92734-d682-4d71-983e-d6ec3f16059f";

        foreach (var produto in Consultar(
            "SELECT LicenseStatus FROM SoftwareLicensingProduct " +
            $"WHERE PartialProductKey IS NOT NULL AND ApplicationID = '{applicationIdWindows}'"))
        {
            if (produto["LicenseStatus"] is uint status)
            {
                payload.SistemaOperacionalAtivado = status == 1; // 1 = Licensed
                break;
            }
        }

        // Chave de produto original (OA3.0): só fica gravada na BIOS em
        // máquinas OEM que vieram com Windows pré-instalado -- em VMs,
        // montagens ou upgrades de licença fica vazia mesmo com tudo
        // ativado corretamente. Isso é uma limitação do próprio Windows,
        // não deste agente.
        foreach (var svc in Consultar("SELECT OA3xOriginalProductKey FROM SoftwareLicensingService"))
        {
            var chave = svc["OA3xOriginalProductKey"]?.ToString();
            if (!string.IsNullOrWhiteSpace(chave)) payload.ChaveProduto = chave;
        }
    }

    /// <summary>
    /// Lista de programas instalados via as chaves de "Uninstall" do
    /// registro do Windows -- de propósito NÃO usa a classe WMI
    /// Win32_Product: além de ser notoriamente lenta, consultá-la
    /// dispara silenciosamente uma reconfiguração/reparo de cada MSI
    /// instalado na máquina (efeito colateral documentado da própria
    /// Microsoft), o que pode deixar programas temporariamente
    /// indisponíveis ou até corrompidos. As chaves de Uninstall são o
    /// mesmo lugar de onde "Aplicativos e Recursos" do Windows lê essa
    /// lista, sem nenhum efeito colateral.
    /// </summary>
    private static void PreencherProgramasInstalados(SincronizarPayload payload)
    {
        // WOW6432Node: espelho de 32 bits das chaves de Uninstall numa
        // instalação de 64 bits do Windows (programas de 32 bits não
        // aparecem no local "normal" nesse caso). HKCU: programas
        // instalados só para o usuário atual (não a máquina toda) --
        // alguns instaladores modernos (ex.: navegadores, apps via
        // winget/store) gravam só ali.
        var locais = new (RegistryKey Raiz, string Subchave)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        var programas = new List<ProgramaInstaladoPayload>();
        // Dedup por nome: o mesmo programa pode aparecer tanto na chave
        // de 64 quanto na de 32 bits em alguns casos (ex.: componentes
        // de runtime instalados por vários instaladores diferentes).
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (raiz, subchave) in locais)
        {
            string[] nomesSubchave;
            try
            {
                using var uninstall = raiz.OpenSubKey(subchave);
                if (uninstall is null) continue;
                nomesSubchave = uninstall.GetSubKeyNames();
            }
            catch { continue; }

            foreach (var nomeSubchave in nomesSubchave)
            {
                try
                {
                    using var uninstall = raiz.OpenSubKey(subchave);
                    using var item = uninstall?.OpenSubKey(nomeSubchave);
                    if (item is null) continue;

                    var nome = (item.GetValue("DisplayName") as string)?.Trim();
                    if (string.IsNullOrWhiteSpace(nome)) continue;

                    // SystemComponent=1: componente interno do Windows,
                    // não um "programa" que o usuário reconheceria.
                    // ParentKeyName presente: é um hotfix/atualização
                    // amarrado a outro programa, não uma instalação
                    // própria -- ambos ficam de fora do inventário,
                    // igual ao que "Aplicativos e Recursos" mostra.
                    if (item.GetValue("SystemComponent") is int sistemaComponente && sistemaComponente == 1) continue;
                    if (item.GetValue("ParentKeyName") is string parent && !string.IsNullOrEmpty(parent)) continue;

                    if (!vistos.Add(nome)) continue;

                    string? dataInstalacao = null;
                    var dataRaw = (item.GetValue("InstallDate") as string)?.Trim();
                    if (!string.IsNullOrEmpty(dataRaw) &&
                        DateTime.TryParseExact(dataRaw, "yyyyMMdd", null,
                            System.Globalization.DateTimeStyles.None, out var data))
                    {
                        dataInstalacao = data.ToString("yyyy-MM-dd");
                    }

                    programas.Add(new ProgramaInstaladoPayload
                    {
                        Nome = nome,
                        Versao = (item.GetValue("DisplayVersion") as string)?.Trim(),
                        Fabricante = (item.GetValue("Publisher") as string)?.Trim(),
                        DataInstalacao = dataInstalacao,
                    });
                }
                catch
                {
                    // Uma entrada específica corrompida/sem permissão de
                    // leitura não deve derrubar a coleta das demais.
                }
            }
        }

        payload.ProgramasInstalados = programas;
    }
}
