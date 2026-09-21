using System.Text;
using Microsoft.Win32;

namespace Meows.Plugins.Rehome.Services;

/// <summary>What a key row is: a key to write down, or the honest word for why there is none.</summary>
public enum KeyStanding { Key, Digital, Account, Partial, NotFound }

/// <summary>One licence row: what it is for, the key if there is one, and what to make of it.</summary>
public sealed record LicenceRow(string Product, string? Key, KeyStanding Standing, string Detail);

/// <summary>
/// What the key finders do, done once and written down: the Windows key decoded from the
/// registry, the one in the firmware where the machine came with one, and what Office keeps,
/// which is the last five characters and no more. Read once, exported to the drive the user
/// chose, kept nowhere: it is an export, not a vault.
/// </summary>
public static class Keys
{
    /// <summary>The keys Windows writes for itself under a digital licence. Not yours to keep, and no use on the new install.</summary>
    private static readonly Dictionary<string, string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["YTMG3-N6DKC-DKB77-7M9GH-8HVX7"] = "Home",
        ["4CPRK-NM3K3-X6XXQ-RXX86-WXCHW"] = "Home N",
        ["VK7JG-NPHTM-C97JM-9MPGT-3V66T"] = "Pro",
        ["2B87N-8KFHP-DKV6R-Y2C8J-PKCKT"] = "Pro N",
        ["DXG7C-N36C4-C4HTG-X4T3X-2YV77"] = "Pro for Workstations",
        ["NW6C2-QMPVW-D7KKK-3GKT6-VCFB2"] = "Education",
        ["YNMGQ-8RYV3-4PGQ3-C8XTP-7CFBY"] = "Education N",
        ["NPPR9-FWDCX-D2C8J-H872K-2YT43"] = "Enterprise",
        ["XGVPP-NMH47-7TTHJ-W3FW7-8HV2C"] = "Enterprise N",
        ["BT79Q-G7N6G-PGBYW-4YWX6-6F4BT"] = "Home Single Language",
        ["7HNRX-D8KGG-3RRTM-XMDQ4-2X7DD"] = "Home Country Specific",
    };

    public static async Task<IReadOnlyList<LicenceRow>> ReadAsync(CancellationToken token)
    {
        var rows = new List<LicenceRow>();
        if (!OperatingSystem.IsWindows())
            return rows;

        var edition = Edition();
        var (status, partial, channel) = await LicenceStatusAsync(token);
        var registryKey = RegistryKey();
        var firmwareKey = await FirmwareKeyAsync(token);

        // The registry key, with what it is.
        if (registryKey is not null)
        {
            if (Generic.TryGetValue(registryKey, out var generic))
                rows.Add(new LicenceRow($"Windows {edition}", registryKey, KeyStanding.Digital, $"generic {generic} key; the licence is digital and comes back on its own when the hardware matches"));
            else
                rows.Add(new LicenceRow($"Windows {edition}", registryKey, KeyStanding.Key, $"from the registry{(channel is null ? "" : $", {channel}")}{(status is null ? "" : $", {status}")}"));
        }
        else
            rows.Add(new LicenceRow($"Windows {edition}", null, KeyStanding.NotFound, "no DigitalProductId in the registry"));

        // The firmware key, where the machine came with Windows.
        if (firmwareKey is not null)
            rows.Add(new LicenceRow("Windows, in the firmware", firmwareKey, KeyStanding.Key, "the OEM key burned into the board; a clean install of the same edition picks it up by itself"));
        else
            rows.Add(new LicenceRow("Windows, in the firmware", null, KeyStanding.NotFound, "no OEM key in the firmware; this machine did not ship with Windows, or it was retail"));

        if (partial is not null)
            rows.Add(new LicenceRow("Windows, activated with", null, KeyStanding.Partial, $"a key ending in {partial}{(status is null ? "" : $", {status}")}"));

        // Office: the last five, from its own script, where it is installed.
        rows.AddRange(await OfficeAsync(token));

        rows.Add(new LicenceRow("Adobe, JetBrains, Steam, Office 365, and everything with a login", null, KeyStanding.Account, "account: sign in on the new install"));

        return rows;
    }

    private static string Edition()
    {
        if (!OperatingSystem.IsWindows())
            return "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var edition = key?.GetValue("EditionID") as string ?? "";
            var build = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? "";
            return $"{edition} {build}".Trim();
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>The key from DigitalProductId, decoded the way every key finder decodes it.</summary>
    public static string? RegistryKey()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("DigitalProductId") is byte[] id ? Decode(id) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The 15 bytes at offset 52 are a base-24 number; read it out backwards in the key alphabet.
    /// Windows 8 and later hide an N in the key and say where with a bit in byte 66.
    /// </summary>
    public static string? Decode(byte[] digitalProductId)
    {
        if (digitalProductId.Length < 67)
            return null;
        const string alphabet = "BCDFGHJKMPQRTVWXY2346789";
        var id = (byte[])digitalProductId.Clone();
        var isWin8 = (id[66] / 6) & 1;
        id[66] = (byte)((id[66] & 0xF7) | ((isWin8 & 2) * 4));

        var key = new StringBuilder();
        var last = 0;
        for (var i = 24; i >= 0; i--)
        {
            var current = 0;
            for (var j = 14; j >= 0; j--)
            {
                current = (current * 256) ^ id[j + 52];
                id[j + 52] = (byte)(current / 24);
                current %= 24;
            }
            key.Insert(0, alphabet[current]);
            last = current;
        }

        var text = key.ToString();
        if (isWin8 == 1)
        {
            var head = text[1..(last + 1)];
            text = text[1..].Replace(head, head + "N");
            if (last == 0)
                text = "N" + text;
        }
        if (text.Length != 25 || text.All(c => c == 'B'))
            return null;
        return string.Join('-', Enumerable.Range(0, 5).Select(i => text.Substring(i * 5, 5)));
    }

    private static async Task<string?> FirmwareKeyAsync(CancellationToken token)
    {
        var result = await Run.PowerShellAsync("(Get-CimInstance -ClassName SoftwareLicensingService).OA3xOriginalProductKey", token, TimeSpan.FromSeconds(60));
        var key = result.Output.Select(l => l.Trim()).FirstOrDefault(l => l.Length == 29 && l.Count(c => c == '-') == 4);
        return key;
    }

    /// <summary>What Windows says about its own licence: the status, the last five of the key it was activated with, and the channel.</summary>
    private static async Task<(string? Status, string? Partial, string? Channel)> LicenceStatusAsync(CancellationToken token)
    {
        var result = await Run.PowerShellAsync(
            "Get-CimInstance -ClassName SoftwareLicensingProduct -Filter \"PartialProductKey IS NOT NULL AND ApplicationID = '55c92734-d682-4d71-983e-d6ec3f16059f'\" | ForEach-Object { \"$($_.LicenseStatus)|$($_.PartialProductKey)|$($_.Description)\" }",
            token, TimeSpan.FromSeconds(60));
        var line = result.Output.Select(l => l.Trim()).FirstOrDefault(l => l.Contains('|'));
        if (line is null)
            return (null, null, null);
        var parts = line.Split('|');
        var status = parts[0] switch
        {
            "1" => "licensed",
            "0" => "unlicensed",
            "2" => "in the grace period",
            "5" => "notification: not activated",
            _ => "status " + parts[0],
        };
        var channel = parts.Length > 2 && parts[2].Contains("channel", StringComparison.OrdinalIgnoreCase)
            ? parts[2][(parts[2].LastIndexOf(',') + 1)..].Trim()
            : null;
        return (status, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null, channel);
    }

    private static async Task<IReadOnlyList<LicenceRow>> OfficeAsync(CancellationToken token)
    {
        var rows = new List<LicenceRow>();
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Office", "Office16", "OSPP.VBS"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Office", "Office16", "OSPP.VBS"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Office", "Office15", "OSPP.VBS"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Office", "Office15", "OSPP.VBS"),
        };
        var script = candidates.FirstOrDefault(File.Exists);
        if (script is null)
            return rows;

        var result = await Run.CaptureAsync("cscript.exe", ["//nologo", script, "/dstatus"], token, TimeSpan.FromSeconds(60));
        string? name = null;
        string? status = null;
        foreach (var raw in result.Output)
        {
            var line = raw.Trim();
            if (line.StartsWith("LICENSE NAME:", StringComparison.OrdinalIgnoreCase))
                name = line["LICENSE NAME:".Length..].Trim();
            else if (line.StartsWith("LICENSE STATUS:", StringComparison.OrdinalIgnoreCase))
                status = line["LICENSE STATUS:".Length..].Trim();
            else if (line.StartsWith("Last 5 characters of installed product key:", StringComparison.OrdinalIgnoreCase))
            {
                var last = line[(line.LastIndexOf(':') + 1)..].Trim();
                var product = name ?? "Office";
                var standing = product.Contains("Subscription", StringComparison.OrdinalIgnoreCase) || product.Contains("O365", StringComparison.OrdinalIgnoreCase)
                    ? KeyStanding.Account
                    : KeyStanding.Partial;
                rows.Add(new LicenceRow(product, null, standing,
                    standing == KeyStanding.Account ? "subscription: sign in on the new install" : $"Office keeps the last five characters, {last}, and no more{(status is null ? "" : $"; {status}")}"));
                name = null;
                status = null;
            }
        }
        return rows;
    }

    /// <summary>keys.txt, plainly named, beside the manifest.</summary>
    public static string Export(IReadOnlyList<LicenceRow> rows, DateTime when)
    {
        var s = new StringBuilder();
        s.AppendLine($"Licence keys on {Environment.MachineName}, read {when:yyyy-MM-dd HH:mm} by Rehome for {Environment.UserName}.");
        s.AppendLine("This file has keys in it. Keep it with the rest of the Rehome folder and nowhere else.");
        s.AppendLine();
        foreach (var row in rows)
        {
            s.AppendLine(row.Product);
            if (row.Key is not null)
                s.AppendLine($"    {row.Key}");
            s.AppendLine($"    {row.Detail}");
            s.AppendLine();
        }
        s.AppendLine("The Windows key goes in with `slmgr /ipk <key>` in an elevated prompt, or Settings > Activation > Change product key.");
        s.AppendLine("Every other key goes into its program's own dialog.");
        return s.ToString();
    }
}
