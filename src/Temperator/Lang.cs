using System.Globalization;

namespace Temperator;

/// <summary>
/// Langue de l'interface : français ou anglais. « auto » suit la langue de Windows
/// (français si Windows est en français, anglais sinon).
/// </summary>
static class L
{
    public const string Auto = "auto", French = "fr", English = "en";

    public static bool Fr { get; private set; } = true;
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("fr-FR");

    public static void Init(string? setting)
    {
        Fr = setting switch
        {
            French => true,
            English => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr",
        };
        Culture = CultureInfo.GetCultureInfo(Fr ? "fr-FR" : "en-US");
    }

    /// <summary>Le texte dans la langue choisie.</summary>
    public static string T(string fr, string en) => Fr ? fr : en;
}
