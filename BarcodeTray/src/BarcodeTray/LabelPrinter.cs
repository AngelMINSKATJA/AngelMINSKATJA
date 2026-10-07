using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BarcodeTray;

/// <summary>Un format de papier connu du pilote (dimensions en centièmes de pouce).</summary>
internal readonly record struct PaperInfo(string Name, int WidthHundredthsInch, int HeightHundredthsInch);

/// <summary>Format de papier retenu pour l'impression.</summary>
/// <param name="Name">Nom du format (celui du pilote, ou « Custom »).</param>
/// <param name="WidthHundredthsInch">Largeur en centièmes de pouce.</param>
/// <param name="UseDriverSizeAsIs">Vrai : utiliser tel quel l'objet PaperSize du pilote (étiquettes prédécoupées).</param>
/// <param name="Reason">Explication lisible (journal et diagnostic).</param>
internal readonly record struct PaperChoice(string Name, int WidthHundredthsInch, bool UseDriverSizeAsIs, string Reason);

/// <summary>
/// Impression silencieuse (sans boîte de dialogue) d'une étiquette sur rouleau continu, par exemple
/// Brother QL-800 avec du ruban DK-22205 de 62 mm : la longueur du papier suit la hauteur de l'image.
/// </summary>
public static class LabelPrinter
{
    private const string DocumentName = "Code-barres Code 128";
    private const int MaxPaperLengthUnits = 4000; // ~ 1 m, limite des QL
    private const double FitTolerance = 1.02;     // 2 % de débordement toléré (zone de silence rognée)

    // ------------------------------------------------------------------ imprimantes

    /// <summary>Noms des imprimantes installées, triés. Liste vide (et journal) en cas d'échec.</summary>
    public static IReadOnlyList<string> GetPrinters()
    {
        try
        {
            var list = new List<string>();
            foreach (string name in PrinterSettings.InstalledPrinters)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    list.Add(name);
                }
            }

            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
        catch (Exception ex)
        {
            Log.Error("Énumération des imprimantes impossible.", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>Nom de l'imprimante par défaut de Windows, ou null s'il n'y en a pas.</summary>
    public static string? GetSystemDefaultPrinter()
    {
        try
        {
            var settings = new PrinterSettings();
            string name = settings.PrinterName;
            return !string.IsNullOrWhiteSpace(name) && settings.IsDefaultPrinter ? name : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Imprimante par défaut introuvable : " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Choisit l'imprimante à présélectionner. Ordre : imprimante mémorisée (si toujours installée, sans tenir
    /// compte de la casse) ; nom contenant « QL-800 » ; contenant « Brother QL » ; contenant « QL- » et « Brother » ;
    /// imprimante par défaut de Windows (si installée) ; première de la liste ; sinon null.
    /// </summary>
    public static string? PickDefaultPrinter(IReadOnlyList<string> printers, string? saved, string? systemDefault)
    {
        if (printers == null || printers.Count == 0)
        {
            return null;
        }

        string? Exact(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            string wanted = name.Trim();
            return printers.FirstOrDefault(p => p != null && string.Equals(p.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
        }

        static bool Has(string? haystack, string needle)
        {
            return haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        string? pick = Exact(saved);
        if (pick != null)
        {
            return pick;
        }

        pick = printers.FirstOrDefault(p => Has(p, "QL-800"));
        if (pick != null)
        {
            return pick;
        }

        pick = printers.FirstOrDefault(p => Has(p, "Brother QL"));
        if (pick != null)
        {
            return pick;
        }

        pick = printers.FirstOrDefault(p => Has(p, "QL-") && Has(p, "Brother"));
        if (pick != null)
        {
            return pick;
        }

        pick = Exact(systemDefault);
        if (pick != null)
        {
            return pick;
        }

        return printers.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
    }

    // ------------------------------------------------------------------ choix du papier (pur)

    /// <summary>
    /// Choisit le format de papier parmi ceux du pilote.
    /// 1) <paramref name="paperNameOverride"/> (nom exact, sans tenir compte de la casse) : utilisé tel quel ;
    /// 2) entrée « 62mm » / « 62 mm » du pilote (rouleau continu) ;
    /// 3) entrée dont le nom commence par « 62mm » sans « x », « × » ni « ( » ;
    /// 4) toute entrée de largeur voisine (± 3 centièmes de pouce), la moins haute d'abord ;
    /// 5) sinon un format « Custom » de la largeur du ruban.
    /// </summary>
    internal static PaperChoice ChoosePaper(IReadOnlyList<PaperInfo> available, int labelWidthMm, string? paperNameOverride)
    {
        available ??= Array.Empty<PaperInfo>();
        int widthUnits = MmToUnits(labelWidthMm);
        string prefix = string.Empty;

        if (!string.IsNullOrWhiteSpace(paperNameOverride))
        {
            string wanted = paperNameOverride.Trim();
            foreach (PaperInfo p in available)
            {
                if (string.Equals((p.Name ?? string.Empty).Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return new PaperChoice(
                        p.Name ?? wanted,
                        p.WidthHundredthsInch > 0 ? p.WidthHundredthsInch : widthUnits,
                        true,
                        "Format imposé par le réglage PaperName : « " + p.Name + " », utilisé tel quel.");
                }
            }

            prefix = "Format imposé « " + wanted + " » introuvable dans le pilote : choix automatique. ";
        }

        string mm = labelWidthMm.ToString(CultureInfo.InvariantCulture);
        var exactName = new Regex(@"^\s*" + Regex.Escape(mm) + @"\s*mm\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var startsWithName = new Regex(@"^\s*" + Regex.Escape(mm) + @"\s*mm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Largeur retenue : celle du pilote si elle est renseignée, sinon celle calculée depuis les mm.
        int WidthOf(PaperInfo p) => p.WidthHundredthsInch > 0 ? p.WidthHundredthsInch : widthUnits;

        foreach (PaperInfo p in available)
        {
            if (p.Name != null && exactName.IsMatch(p.Name))
            {
                return new PaperChoice(p.Name, WidthOf(p), false,
                    prefix + "Entrée « " + p.Name + " » du pilote (rouleau continu " + mm + " mm).");
            }
        }

        foreach (PaperInfo p in available)
        {
            if (p.Name != null
                && startsWithName.IsMatch(p.Name)
                && p.Name.IndexOfAny(new[] { 'x', 'X', '×', '(' }) < 0)
            {
                return new PaperChoice(p.Name, WidthOf(p), false,
                    prefix + "Entrée « " + p.Name + " » du pilote (nom commençant par " + mm + "mm).");
            }
        }

        PaperInfo? byWidth = null;
        foreach (PaperInfo p in available)
        {
            if (p.Name == null || Math.Abs(p.WidthHundredthsInch - widthUnits) > 3)
            {
                continue;
            }

            if (byWidth == null || p.HeightHundredthsInch < byWidth.Value.HeightHundredthsInch)
            {
                byWidth = p;
            }
        }

        if (byWidth != null)
        {
            PaperInfo p = byWidth.Value;
            return new PaperChoice(p.Name, WidthOf(p), false,
                prefix + "Entrée « " + p.Name + " » du pilote, retenue pour sa largeur (" + p.WidthHundredthsInch
                + "/100 pouce, hauteur " + p.HeightHundredthsInch + ").");
        }

        return new PaperChoice("Custom", widthUnits, false,
            prefix + "Aucun format de " + mm + " mm dans le pilote : format personnalisé (" + widthUnits + "/100 pouce).");
    }

    // ------------------------------------------------------------------ géométrie (pure)

    /// <summary>Facteur de réduction (1 = taille réelle) pour que l'image tienne dans la zone imprimable.</summary>
    internal static double FitScale(double naturalWidthUnits, double areaWidthUnits)
    {
        if (areaWidthUnits <= 0 || naturalWidthUnits <= areaWidthUnits * FitTolerance)
        {
            return 1.0; // petit débordement : on rogne un peu de zone de silence plutôt que de rééchantillonner
        }

        return areaWidthUnits / naturalWidthUnits;
    }

    /// <summary>
    /// Rectangle de destination (centièmes de pouce, origine = coin de la zone imprimable) : image à sa taille
    /// physique, réduite uniformément si elle est trop large, centrée horizontalement, y = 0, alignée sur les
    /// points de l'imprimante quand leur résolution est connue (évite tout rééchantillonnage des barres).
    /// </summary>
    internal static RectangleF ComputeDestination(
        int imageWidthPx, int imageHeightPx, float imageDpiX, float imageDpiY,
        float areaWidthUnits, float deviceDpiX, float deviceDpiY, out bool scaledDown)
    {
        if (imageDpiX <= 0)
        {
            imageDpiX = 300f;
        }

        if (imageDpiY <= 0)
        {
            imageDpiY = imageDpiX;
        }

        double naturalW = imageWidthPx * 100.0 / imageDpiX;
        double naturalH = imageHeightPx * 100.0 / imageDpiY;
        double scale = FitScale(naturalW, areaWidthUnits);
        scaledDown = scale < 1.0;

        double w = naturalW * scale;
        double h = naturalH * scale;
        double x = areaWidthUnits > 0 ? (areaWidthUnits - w) / 2.0 : 0.0;

        if (deviceDpiX > 0)
        {
            w = Math.Max(1.0, Math.Round(w * deviceDpiX / 100.0)) * 100.0 / deviceDpiX;
            x = Math.Round(x * deviceDpiX / 100.0) * 100.0 / deviceDpiX;
        }

        if (deviceDpiY > 0)
        {
            h = Math.Max(1.0, Math.Round(h * deviceDpiY / 100.0)) * 100.0 / deviceDpiY;
        }

        return new RectangleF((float)x, 0f, (float)w, (float)h);
    }

    // ------------------------------------------------------------------ impression

    /// <summary>
    /// Imprime l'image sans aucune boîte de dialogue. Lève <see cref="InvalidOperationException"/>
    /// avec un message en français en cas d'échec (l'exception d'origine est journalisée et jointe).
    /// </summary>
    public static void Print(Bitmap image, string printerName, AppSettings settings)
    {
        if (image == null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        if (string.IsNullOrWhiteSpace(printerName))
        {
            throw new InvalidOperationException("Aucune imprimante sélectionnée.");
        }

        AppSettings s = (settings ?? new AppSettings()).Clone();
        s.Validate();
        string printer = printerName.Trim();

        try
        {
            PrintCore(image, printer, s);
        }
        catch (PrintFailure failure)
        {
            Log.Error("Impression : " + failure.Message, failure.InnerException);
            throw new InvalidOperationException(failure.Message, failure.InnerException ?? failure);
        }
        catch (InvalidPrinterException ex)
        {
            Log.Error("Impression : imprimante invalide « " + printer + " ».", ex);
            throw new InvalidOperationException(
                "L'imprimante « " + printer + " » est introuvable ou indisponible. Vérifiez qu'elle est branchée et allumée, "
                + "puis sélectionnez-la à nouveau dans la liste.", ex);
        }
        catch (Win32Exception ex)
        {
            Log.Error("Impression : erreur Windows " + ex.NativeErrorCode + ".", ex);
            throw new InvalidOperationException(
                "Windows a signalé une erreur d'impression (" + ex.Message + "). Vérifiez que l'imprimante « " + printer
                + " » est allumée, connectée et sans erreur (rouleau, capot).", ex);
        }
        catch (Exception ex)
        {
            Log.Error("Impression : échec inattendu.", ex);
            throw new InvalidOperationException(
                "L'impression a échoué (" + ex.Message + "). Utilisez « Copier le diagnostic d'impression » "
                + "dans le menu de l'icône pour obtenir le détail.", ex);
        }
    }

    private static void PrintCore(Bitmap image, string printerName, AppSettings s)
    {
        float imageDpiX = image.HorizontalResolution > 0 ? image.HorizontalResolution : s.Dpi;
        float imageDpiY = image.VerticalResolution > 0 ? image.VerticalResolution : imageDpiX;
        double naturalW = image.Width * 100.0 / imageDpiX;
        double naturalH = image.Height * 100.0 / imageDpiY;

        using var doc = new PrintDocument();
        doc.PrinterSettings.PrinterName = printerName;
        if (!doc.PrinterSettings.IsValid)
        {
            throw new PrintFailure(
                "L'imprimante « " + printerName + " » est introuvable ou indisponible. Vérifiez qu'elle est branchée "
                + "et allumée, puis sélectionnez-la à nouveau dans la liste.");
        }

        if (s.CheckPrinterStatus)
        {
            CheckPrinterReady(printerName);
        }

        doc.DocumentName = DocumentName;
        doc.PrintController = new StandardPrintController(); // pas de fenêtre « Impression en cours »
        doc.PrinterSettings.Copies = 1;
        doc.OriginAtMargins = false;
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.DefaultPageSettings.Landscape = false;

        // Formats de papier du pilote.
        var infos = new List<PaperInfo>();
        var driverSizes = new List<PaperSize>();
        try
        {
            foreach (PaperSize ps in doc.PrinterSettings.PaperSizes)
            {
                infos.Add(new PaperInfo(ps.PaperName, ps.Width, ps.Height));
                driverSizes.Add(ps);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Formats de papier du pilote illisibles : " + ex.Message);
        }

        PaperChoice choice = ChoosePaper(infos, s.LabelWidthMm, s.PaperName);
        int minUnits = MmToUnits(s.MinLabelLengthMm);
        PaperSize paper;
        if (choice.UseDriverSizeAsIs)
        {
            paper = driverSizes.FirstOrDefault(p => string.Equals(p.PaperName, choice.Name, StringComparison.OrdinalIgnoreCase))
                    ?? new PaperSize(choice.Name, choice.WidthHundredthsInch, Math.Max(minUnits, (int)Math.Ceiling(naturalH)));
            doc.DefaultPageSettings.PaperSize = paper;
        }
        else
        {
            int heightUnits = Math.Max(minUnits, (int)Math.Ceiling(naturalH));
            paper = new PaperSize(choice.Name, choice.WidthHundredthsInch, heightUnits);
            doc.DefaultPageSettings.PaperSize = paper;

            // Si l'image doit être réduite pour tenir dans la zone imprimable, la longueur du papier suit.
            RectangleF area = SafePrintableArea(doc.DefaultPageSettings);
            if (area.Width > 0)
            {
                double fit = FitScale(naturalW, area.Width);
                int fittedHeight = Math.Max(minUnits, (int)Math.Ceiling(naturalH * fit));
                if (fittedHeight != heightUnits)
                {
                    heightUnits = fittedHeight;
                    paper = new PaperSize(choice.Name, choice.WidthHundredthsInch, heightUnits);
                    doc.DefaultPageSettings.PaperSize = paper;
                }
            }

            if (heightUnits > MaxPaperLengthUnits)
            {
                throw new PrintFailure("L'étiquette est trop longue pour l'imprimante (" + UnitsToMm(heightUnits).ToString("0", CultureInfo.InvariantCulture)
                                       + " mm). Raccourcissez le texte.");
            }
        }

        Log.Info("Impression : imprimante « " + printerName + " », papier « " + paper.PaperName + " » "
                 + paper.Width + "x" + paper.Height + " (1/100 pouce) [" + choice.Reason + "], image "
                 + image.Width + "x" + image.Height + " px à " + imageDpiX.ToString("0.##", CultureInfo.InvariantCulture)
                 + " dpi, zone imprimable " + FormatRect(SafePrintableArea(doc.DefaultPageSettings)) + ".");

        Exception? pageError = null;
        bool printed = false;
        doc.PrintPage += (sender, e) =>
        {
            try
            {
                DrawLabel(e, image, imageDpiX, imageDpiY);
                printed = true;
            }
            catch (Exception ex)
            {
                pageError = ex;
                e.Cancel = true;
            }
        };

        doc.Print();

        if (pageError != null)
        {
            throw new PrintFailure("Impossible de dessiner l'étiquette (" + pageError.Message + ").", pageError);
        }

        string? noPage = DescribeNoPageProblem(printed, pageError);
        if (noPage != null)
        {
            Log.Warn("Impression : " + noPage);
            throw new PrintFailure(noPage);
        }
    }

    /// <summary>
    /// Quand le pilote annule le travail dès l'ouverture (boîte du pilote fermée, « Enregistrer sous » de
    /// Microsoft Print to PDF annulé, erreur 1223...), .NET ne lève aucune exception et ne déclenche jamais
    /// PrintPage : <c>Print()</c> revient normalement alors qu'aucune page n'est partie. Renvoie le message
    /// d'erreur (français) dans ce cas, sinon null.
    /// </summary>
    internal static string? DescribeNoPageProblem(bool pagePrinted, Exception? pageError)
    {
        if (pagePrinted || pageError != null)
        {
            return null;
        }

        return "L'impression a été annulée ou refusée par le pilote (aucune page envoyée). "
               + "Vérifiez l'imprimante et fermez toute fenêtre du pilote restée ouverte.";
    }

    /// <summary>
    /// Contrôle l'état de la file Windows avant d'envoyer l'étiquette (voir <see cref="PrinterStatus"/>).
    /// Lève <see cref="PrintFailure"/> si l'étiquette ne pourrait pas sortir ; ne bloque jamais si l'état est illisible.
    /// </summary>
    private static void CheckPrinterReady(string printerName)
    {
        PrinterStatus.Snapshot? snapshot = PrinterStatus.TryRead(printerName);
        if (snapshot == null)
        {
            Log.Info("État de l'imprimante illisible : contrôle ignoré, l'impression continue.");
            return;
        }

        PrinterStatus.Snapshot state = snapshot.Value;
        Log.Info("État de l'imprimante « " + printerName + " » : " + PrinterStatus.DescribeFlags(state.Status, state.Attributes)
                 + " (Status 0x" + state.Status.ToString("X", CultureInfo.InvariantCulture)
                 + ", Attributes 0x" + state.Attributes.ToString("X", CultureInfo.InvariantCulture)
                 + ", port « " + state.PortName + " », pilote « " + state.DriverName + " »).");

        string? problem = PrinterStatus.DescribeProblem(printerName, state.Status, state.Attributes);
        if (problem != null)
        {
            throw new PrintFailure(problem);
        }
    }

    private static void DrawLabel(PrintPageEventArgs e, Bitmap image, float imageDpiX, float imageDpiY)
    {
        Graphics g = e.Graphics ?? throw new InvalidOperationException("Surface d'impression indisponible.");

        RectangleF area = SafePrintableArea(e.PageSettings);
        if (area.Width <= 0 || area.Height <= 0)
        {
            area = new RectangleF(0, 0, e.PageBounds.Width, e.PageBounds.Height);
        }

        RectangleF dest = ComputeDestination(
            image.Width, image.Height, imageDpiX, imageDpiY, area.Width, g.DpiX, g.DpiY, out bool scaledDown);

        if (scaledDown)
        {
            Log.Warn("Image plus large que la zone imprimable (" + area.Width.ToString("0.##", CultureInfo.InvariantCulture)
                     + "/100 pouce) : réduite uniformément.");
        }

        Log.Info("Impression : zone " + FormatRect(area) + ", destination " + FormatRect(dest)
                 + ", résolution imprimante " + g.DpiX.ToString("0", CultureInfo.InvariantCulture) + "x"
                 + g.DpiY.ToString("0", CultureInfo.InvariantCulture) + " dpi.");

        // L'origine du Graphics est le coin de la zone imprimable (OriginAtMargins = false) : x est donc
        // relatif à cette zone. Aucun lissage : les barres doivent rester nettes.
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.None;
        g.CompositingMode = CompositingMode.SourceCopy;

        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY); // évite le liseré de demi-pixel en bord d'image

        var corners = new[]
        {
            new PointF(dest.Left, dest.Top),
            new PointF(dest.Right, dest.Top),
            new PointF(dest.Left, dest.Bottom),
        };
        g.DrawImage(image, corners, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, attributes);

        e.HasMorePages = false;
    }

    // ------------------------------------------------------------------ diagnostic

    /// <summary>
    /// Texte de diagnostic (à copier/coller pour un dépannage) : imprimantes, formats de papier,
    /// zone imprimable, résolutions, choix de papier effectué, réglages, fin du journal.
    /// Ne lève jamais d'exception.
    /// </summary>
    public static string BuildDiagnostics(string? printerName, AppSettings settings)
    {
        var sb = new StringBuilder();
        AppSettings s;
        try
        {
            s = (settings ?? new AppSettings()).Clone();
            s.Validate();
        }
        catch
        {
            s = new AppSettings();
        }

        sb.AppendLine("=== Diagnostic d'impression - Code-barres Code 128 ===");
        Guard(sb, "environnement", () =>
        {
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            sb.AppendLine("Date            : " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            sb.AppendLine("Version         : " + (version?.ToString() ?? "?"));
            sb.AppendLine("Windows         : " + RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")");
            sb.AppendLine(".NET            : " + RuntimeInformation.FrameworkDescription);
            sb.AppendLine("Exécutable      : " + (Environment.ProcessPath ?? "?"));
            sb.AppendLine("Réglages        : " + AppSettings.FilePath + (System.IO.File.Exists(AppSettings.FilePath) ? "" : " (absent)"));
            sb.AppendLine("Journal         : " + Log.FilePath);
        });

        string? defaultPrinter = GetSystemDefaultPrinter();
        IReadOnlyList<string> printers = GetPrinters();
        sb.AppendLine();
        sb.AppendLine("--- Imprimantes installées (" + printers.Count + ") ---");
        foreach (string p in printers)
        {
            string marks = string.Empty;
            if (string.Equals(p, defaultPrinter, StringComparison.OrdinalIgnoreCase))
            {
                marks += " [défaut Windows]";
            }

            if (string.Equals(p, printerName, StringComparison.OrdinalIgnoreCase))
            {
                marks += " [sélectionnée]";
            }

            sb.AppendLine("  " + p + marks);
        }

        if (printers.Count == 0)
        {
            sb.AppendLine("  (aucune)");
        }

        sb.AppendLine();
        sb.AppendLine("Imprimante testée : " + (string.IsNullOrWhiteSpace(printerName) ? "(aucune sélectionnée)" : printerName));

        if (!string.IsNullOrWhiteSpace(printerName))
        {
            Guard(sb, "imprimante", () => AppendPrinterDetails(sb, printerName!.Trim(), s));
        }

        sb.AppendLine();
        sb.AppendLine("--- Réglages (settings.json) ---");
        sb.AppendLine(s.ToJson());

        Guard(sb, "journal", () =>
        {
            IReadOnlyList<string> tail = Log.ReadTail(40);
            sb.AppendLine();
            sb.AppendLine("--- Fin du journal (" + tail.Count + " lignes) ---");
            foreach (string line in tail)
            {
                sb.AppendLine(line);
            }
        });

        return sb.ToString();
    }

    private static void AppendPrinterDetails(StringBuilder sb, string printerName, AppSettings s)
    {
        var ps = new PrinterSettings { PrinterName = printerName };
        sb.AppendLine("IsValid           : " + ps.IsValid);
        if (!ps.IsValid)
        {
            sb.AppendLine("(imprimante invalide ou indisponible : pas de détail)");
            return;
        }

        Guard(sb, "état de la file", () =>
        {
            PrinterStatus.Snapshot? snapshot = PrinterStatus.TryRead(printerName);
            if (snapshot == null)
            {
                sb.AppendLine("État (spouleur)   : illisible");
                return;
            }

            PrinterStatus.Snapshot state = snapshot.Value;
            sb.AppendLine("État (spouleur)   : " + PrinterStatus.DescribeFlags(state.Status, state.Attributes)
                          + " [Status 0x" + state.Status.ToString("X", CultureInfo.InvariantCulture)
                          + ", Attributes 0x" + state.Attributes.ToString("X", CultureInfo.InvariantCulture) + "]");
            sb.AppendLine("Port              : " + (state.PortName ?? "?"));
            sb.AppendLine("Pilote            : " + (state.DriverName ?? "?"));
            string? problem = PrinterStatus.DescribeProblem(printerName, state.Status, state.Attributes);
            sb.AppendLine("Contrôle d'état   : " + (problem == null
                ? "OK, l'impression serait lancée"
                : "BLOQUERAIT l'impression (" + (s.CheckPrinterStatus ? "contrôle actif" : "contrôle désactivé par CheckPrinterStatus") + ")"));
        });

        Guard(sb, "capacités", () =>
        {
            sb.AppendLine("IsDefaultPrinter  : " + ps.IsDefaultPrinter);
            sb.AppendLine("SupportsColor     : " + ps.SupportsColor);
            sb.AppendLine("CanDuplex         : " + ps.CanDuplex);
            sb.AppendLine("MaximumCopies     : " + ps.MaximumCopies);
        });

        sb.AppendLine();
        sb.AppendLine("--- Formats de papier du pilote ---");
        var infos = new List<PaperInfo>();
        Guard(sb, "formats de papier", () =>
        {
            foreach (PaperSize p in ps.PaperSizes)
            {
                infos.Add(new PaperInfo(p.PaperName, p.Width, p.Height));
                sb.AppendLine("  « " + p.PaperName + " »  kind=" + p.Kind + " (raw " + p.RawKind + ")  "
                              + p.Width + "x" + p.Height + " /100 pouce = "
                              + UnitsToMm(p.Width).ToString("0.0", CultureInfo.InvariantCulture) + " x "
                              + UnitsToMm(p.Height).ToString("0.0", CultureInfo.InvariantCulture) + " mm");
            }

            if (infos.Count == 0)
            {
                sb.AppendLine("  (aucun)");
            }
        });

        sb.AppendLine();
        sb.AppendLine("--- Résolutions ---");
        Guard(sb, "résolutions", () =>
        {
            foreach (PrinterResolution r in ps.PrinterResolutions)
            {
                sb.AppendLine("  " + r.Kind + " " + r.X + "x" + r.Y + " dpi");
            }
        });

        sb.AppendLine();
        sb.AppendLine("--- Page par défaut ---");
        Guard(sb, "page par défaut", () =>
        {
            PageSettings page = ps.DefaultPageSettings;
            sb.AppendLine("PaperSize         : « " + page.PaperSize.PaperName + " » " + page.PaperSize.Width + "x" + page.PaperSize.Height);
            sb.AppendLine("Landscape         : " + page.Landscape);
            sb.AppendLine("Margins           : " + page.Margins);
            sb.AppendLine("PrintableArea     : " + FormatRect(page.PrintableArea));
            sb.AppendLine("HardMargin        : " + page.HardMarginX.ToString("0.##", CultureInfo.InvariantCulture)
                                                + " / " + page.HardMarginY.ToString("0.##", CultureInfo.InvariantCulture));
            sb.AppendLine("PrinterResolution : " + page.PrinterResolution.Kind + " " + page.PrinterResolution.X + "x" + page.PrinterResolution.Y);
        });

        sb.AppendLine();
        sb.AppendLine("--- Choix du papier ---");
        PaperChoice choice = ChoosePaper(infos, s.LabelWidthMm, s.PaperName);
        sb.AppendLine("Format retenu     : « " + choice.Name + " » largeur " + choice.WidthHundredthsInch
                      + "/100 pouce, tel quel : " + choice.UseDriverSizeAsIs);
        sb.AppendLine("Raison            : " + choice.Reason);
        sb.AppendLine("Longueur minimale : " + s.MinLabelLengthMm + " mm = " + MmToUnits(s.MinLabelLengthMm) + "/100 pouce");
    }

    // ------------------------------------------------------------------ utilitaires

    private static void Guard(StringBuilder sb, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            sb.AppendLine("(" + what + " : lecture impossible - " + ex.GetType().Name + " : " + ex.Message + ")");
        }
    }

    private static RectangleF SafePrintableArea(PageSettings page)
    {
        try
        {
            return page.PrintableArea;
        }
        catch (Exception ex)
        {
            Log.Warn("Zone imprimable illisible : " + ex.Message);
            return RectangleF.Empty;
        }
    }

    private static string FormatRect(RectangleF r)
    {
        return "(" + r.X.ToString("0.##", CultureInfo.InvariantCulture)
               + ", " + r.Y.ToString("0.##", CultureInfo.InvariantCulture)
               + ", " + r.Width.ToString("0.##", CultureInfo.InvariantCulture)
               + " x " + r.Height.ToString("0.##", CultureInfo.InvariantCulture) + ")";
    }

    internal static int MmToUnits(double mm)
    {
        return (int)Math.Round(mm / 25.4 * 100.0, MidpointRounding.AwayFromZero);
    }

    internal static double UnitsToMm(int units)
    {
        return units * 25.4 / 100.0;
    }

    /// <summary>Erreur « prévue » : le message est déjà en français et destiné à l'utilisateur.</summary>
    private sealed class PrintFailure : Exception
    {
        public PrintFailure(string message, Exception? inner = null)
            : base(message, inner)
        {
        }
    }
}
