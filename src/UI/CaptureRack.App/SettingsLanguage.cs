namespace CaptureRack.App
{
    internal static class SettingsLanguage
    {
        internal static string Resolve(string? saved, string windowsCulture) =>
            saved is "en" or "zh-Hans" or "zh-Hant" or "ja" ? saved : FromWindowsCulture(windowsCulture);

        internal static string FromWindowsCulture(string name)
        {
            var tags = name.ToLowerInvariant().Split('-');
            if (tags[0] == "ja")
                return "ja";
            if (tags[0] != "zh" && tags[0] != "yue")
                return "en";

            // An explicit script takes priority over a regional convention.
            var qualifier = tags.Length > 1 ? tags[1] : "";
            if (qualifier == "hans" || qualifier == "chs")
                return "zh-Hans";
            if (qualifier == "hant" || qualifier == "cht")
                return "zh-Hant";
            if (qualifier.Length == 4)
                return "en";

            return qualifier == "hk" || qualifier == "tw" || qualifier == "mo" || tags[0] == "yue"
                ? "zh-Hant"
                : "zh-Hans";
        }
    }
}
