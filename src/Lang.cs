using System;
using System.Collections.Generic;
using System.Globalization;

namespace OrclWAMP
{
    /// <summary>
    /// UI language (English / Bulgarian). Texts are written in English in the code and looked up in
    /// <see cref="LangBg.Texts"/>; anything not translated simply stays English.
    /// </summary>
    internal static class Lang
    {
        public static readonly string Code = Load();
        public static bool IsBg => Code == "bg";

        static string Load()
        {
            var s = AppSettings.Get("Language");
            if (s == "bg" || s == "en") return s;
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "bg" ? "bg" : "en";
        }

        /// <summary>Translate a fixed text.</summary>
        public static string T(string en) => IsBg && LangBg.Texts.TryGetValue(en, out var bg) ? bg : en;

        /// <summary>Translate a text with {0}, {1}… placeholders and fill them in.</summary>
        public static string F(string en, params object[] args)
        {
            try { return string.Format(T(en), args); }
            catch (FormatException) { return string.Format(en, args); } // a broken translation must never crash the app
        }

        /// <summary>Shows a stored (English) note in the UI language; "From &lt;file&gt;" keeps the file name.</summary>
        public static string Note(string note)
        {
            if (string.IsNullOrEmpty(note)) return note ?? "";
            if (note.StartsWith("From ", StringComparison.Ordinal)) return F("From {0}", note.Substring(5));
            return T(note);
        }

        public static void Save(string code) => AppSettings.Set("Language", code);
    }
}
