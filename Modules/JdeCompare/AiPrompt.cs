using System.Text;
using Utils.Colas.Infrastructure;

namespace Utils.Colas.Modules.JdeCompare;

public sealed record FileDiff(
    string Object, string Source, string Target, string Path,
    PackageFile? Src, PackageFile? Tgt, bool Binary, string SrcText, string TgtText, List<DiffLine> Lines);

// Prompt de l'analyse IA d'un écart de fichier entre deux environnements JDE.
public static class AiPrompt
{
    public const string System =
        "Tu es un expert JD Edwards EnterpriseOne (C, NER, Event Rules, specs, tables F98xx). " +
        "On te fournit le diff d'un fichier du package d'un objet JDE entre un environnement source et un environnement cible. " +
        "Les lignes « - » n'existent que dans la source, les lignes « + » que dans la cible ; Lxx est le numéro de ligne. " +
        "Réponds en français, de façon concise et structurée, en texte brut (pas de tableau) :\n" +
        "1. Synthèse : une ou deux phrases sur la nature de l'écart.\n" +
        "2. Changements : liste à puces regroupant les modifications par thème (fonctions, structures, champs, règles, constantes...), en citant les lignes.\n" +
        "3. Impact : risques ou points à vérifier avant promotion de la source vers la cible.\n" +
        "Distingue les écarts purement cosmétiques (commentaires, espaces, dates de génération) des écarts fonctionnels. " +
        "N'invente rien qui ne soit pas dans le diff ; si le diff est tronqué, signale-le.";

    public static string User(FileDiff d, int maxChars)
    {
        var sb = new StringBuilder();
        sb.Append("Objet JDE : ").Append(d.Object).Append('\n');
        sb.Append("Fichier : ").Append(d.Path).Append('\n');
        sb.Append("Source : ").Append(d.Source).Append(d.Src is null ? " (fichier absent)" : $" ({d.SrcText.Split('\n').Length} lignes)").Append('\n');
        sb.Append("Cible : ").Append(d.Target).Append(d.Tgt is null ? " (fichier absent)" : $" ({d.TgtText.Split('\n').Length} lignes)").Append('\n');
        sb.Append("\nDiff :\n");
        sb.Append(LineDiff.ToText(d.Lines, maxChars));
        return sb.ToString();
    }
}
