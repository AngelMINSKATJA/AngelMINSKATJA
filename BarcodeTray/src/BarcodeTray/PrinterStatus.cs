using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BarcodeTray;

/// <summary>
/// État de la file d'impression Windows (spouleur) : hors connexion, en pause, capot ouvert, plus de ruban...
/// Sans ce contrôle, une imprimante éteinte ou débranchée accepte quand même le travail (il est simplement mis en
/// attente) : le programme annoncerait « envoyée » alors que rien ne sort, et chaque nouveau clic ajouterait une
/// étiquette qui sortirait en double à l'allumage.
/// La lecture est « au mieux » : si Windows ne répond pas, ou si le pilote ne publie pas d'état, on imprime quand même.
/// </summary>
internal static class PrinterStatus
{
    // PRINTER_STATUS_* (winspool.h) - le champ Status de PRINTER_INFO_2.
    internal const uint StatusPaused = 0x00000001;
    internal const uint StatusError = 0x00000002;
    internal const uint StatusPendingDeletion = 0x00000004;
    internal const uint StatusPaperJam = 0x00000008;
    internal const uint StatusPaperOut = 0x00000010;
    internal const uint StatusPaperProblem = 0x00000040;
    internal const uint StatusOffline = 0x00000080;
    internal const uint StatusNotAvailable = 0x00001000;
    internal const uint StatusUserIntervention = 0x00100000;
    internal const uint StatusDoorOpen = 0x00400000;
    internal const uint StatusServerOffline = 0x02000000;

    // PRINTER_ATTRIBUTE_WORK_OFFLINE : case « Utiliser l'imprimante hors connexion ».
    internal const uint AttributeWorkOffline = 0x00000400;

    private const int InfoLevel = 2;
    private const int MaxInfoBytes = 1_000_000;

    /// <summary>Photographie de l'état de la file : Status, Attributes, port et pilote.</summary>
    internal readonly record struct Snapshot(uint Status, uint Attributes, string? PortName, string? DriverName);

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public IntPtr pServerName;
        public IntPtr pPrinterName;
        public IntPtr pShareName;
        public IntPtr pPortName;
        public IntPtr pDriverName;
        public IntPtr pComment;
        public IntPtr pLocation;
        public IntPtr pDevMode;
        public IntPtr pSepFile;
        public IntPtr pPrintProcessor;
        public IntPtr pDatatype;
        public IntPtr pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool OpenPrinter(string printerName, out IntPtr printerHandle, IntPtr defaults);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetPrinter(IntPtr printerHandle, int level, IntPtr buffer, int bufferSize, out int bytesNeeded);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool ClosePrinter(IntPtr printerHandle);

    /// <summary>
    /// Lit l'état de la file auprès du spouleur. Renvoie null si la lecture est impossible (imprimante inconnue,
    /// accès refusé, système non Windows...) ; ne lève jamais d'exception. À appeler hors du thread d'interface.
    /// </summary>
    internal static Snapshot? TryRead(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
        {
            return null;
        }

        IntPtr handle = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenPrinter(printerName, out handle, IntPtr.Zero))
            {
                Log.Warn("État de l'imprimante : OpenPrinter a échoué (erreur " + Marshal.GetLastWin32Error() + ").");
                handle = IntPtr.Zero;
                return null;
            }

            // Premier appel : taille nécessaire (il échoue volontairement avec ERROR_INSUFFICIENT_BUFFER).
            GetPrinter(handle, InfoLevel, IntPtr.Zero, 0, out int needed);
            if (needed < Marshal.SizeOf<PrinterInfo2>() || needed > MaxInfoBytes)
            {
                Log.Warn("État de l'imprimante : taille inattendue (" + needed + " octets).");
                return null;
            }

            buffer = Marshal.AllocHGlobal(needed);
            if (!GetPrinter(handle, InfoLevel, buffer, needed, out _))
            {
                Log.Warn("État de l'imprimante : GetPrinter a échoué (erreur " + Marshal.GetLastWin32Error() + ").");
                return null;
            }

            PrinterInfo2 info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
            return new Snapshot(
                info.Status,
                info.Attributes,
                Marshal.PtrToStringUni(info.pPortName),
                Marshal.PtrToStringUni(info.pDriverName));
        }
        catch (Exception ex)
        {
            Log.Warn("État de l'imprimante illisible : " + ex.Message);
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (handle != IntPtr.Zero)
            {
                try
                {
                    ClosePrinter(handle);
                }
                catch
                {
                    // ignoré
                }
            }
        }
    }

    /// <summary>
    /// Message d'erreur en français si l'état interdit l'impression (l'étiquette ne sortirait pas, ou resterait
    /// en attente et sortirait en double plus tard) ; null si l'impression peut partir.
    /// Seuls les états fiables bloquent : hors connexion, en pause, capot ouvert, plus de ruban, bourrage.
    /// Les autres (erreur générique, intervention demandée...) sont seulement journalisés.
    /// </summary>
    internal static string? DescribeProblem(string printerName, uint status, uint attributes)
    {
        string name = "« " + printerName + " »";
        const string Escape = " Si Windows affiche un état erroné, mettez CheckPrinterStatus à false dans settings.json.";

        if ((attributes & AttributeWorkOffline) != 0)
        {
            return "L'imprimante " + name + " est réglée sur « Utiliser l'imprimante hors connexion » dans Windows : "
                   + "l'étiquette resterait en attente. Rien n'a été envoyé. Dans la file d'impression (menu Imprimante), "
                   + "décochez cette option, puis réessayez." + Escape;
        }

        if ((status & (StatusOffline | StatusNotAvailable | StatusServerOffline)) != 0)
        {
            return "L'imprimante " + name + " est hors connexion (éteinte, câble USB débranché ?). Rien n'a été envoyé : "
                   + "allumez-la, vérifiez le câble, puis réessayez." + Escape;
        }

        if ((status & StatusPaused) != 0)
        {
            return "La file d'impression de l'imprimante " + name + " est en pause. Rien n'a été envoyé : "
                   + "reprenez l'impression (file d'impression, menu Imprimante), puis réessayez." + Escape;
        }

        if ((status & StatusDoorOpen) != 0)
        {
            return "Le capot de l'imprimante " + name + " est ouvert. Rien n'a été envoyé : fermez-le, puis réessayez." + Escape;
        }

        if ((status & StatusPaperOut) != 0)
        {
            return "L'imprimante " + name + " signale qu'il n'y a plus de ruban (ou qu'il est mal installé). "
                   + "Rien n'a été envoyé : vérifiez le rouleau, puis réessayez." + Escape;
        }

        if ((status & StatusPaperJam) != 0)
        {
            return "L'imprimante " + name + " signale un bourrage de ruban. Rien n'a été envoyé : dégagez-le, puis réessayez." + Escape;
        }

        return null;
    }

    /// <summary>Noms lisibles des indicateurs d'état actifs (pour le journal et le diagnostic).</summary>
    internal static string DescribeFlags(uint status, uint attributes)
    {
        var names = new List<string>();
        if ((attributes & AttributeWorkOffline) != 0)
        {
            names.Add("WORK_OFFLINE (utiliser hors connexion)");
        }

        void Add(uint flag, string label)
        {
            if ((status & flag) != 0)
            {
                names.Add(label);
            }
        }

        Add(StatusPaused, "PAUSED");
        Add(StatusError, "ERROR");
        Add(StatusPendingDeletion, "PENDING_DELETION");
        Add(StatusPaperJam, "PAPER_JAM");
        Add(StatusPaperOut, "PAPER_OUT");
        Add(StatusPaperProblem, "PAPER_PROBLEM");
        Add(StatusOffline, "OFFLINE");
        Add(StatusNotAvailable, "NOT_AVAILABLE");
        Add(StatusUserIntervention, "USER_INTERVENTION");
        Add(StatusDoorOpen, "DOOR_OPEN");
        Add(StatusServerOffline, "SERVER_OFFLINE");

        return names.Count == 0 ? "prête (aucun indicateur)" : string.Join(", ", names);
    }
}
