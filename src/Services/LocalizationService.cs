using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace RobloxAccountManager.Services;

/// <summary>
/// Runtime localization. Every string lives in an embedded <c>Localization/&lt;code&gt;.json</c> file
/// (flat key → text). The active language is published into the application resources under
/// <c>Str.&lt;key&gt;</c>, so XAML binds with <c>{DynamicResource Str.Nav.Accounts}</c> and a language
/// switch repaints instantly — no restart, no satellite assemblies. Code uses <see cref="L.T(string)"/>.
///
/// English is the source language: it defines the key set and fills any gap in a translation, so a
/// missing string degrades to English rather than to an empty label.
/// </summary>
public static class LocalizationService
{
    public sealed record Language(string Code, string NativeName);

    public static readonly Language[] Languages =
    {
        new("en", "English"),
        new("de", "Deutsch"),
        new("es", "Español"),
        new("fr", "Français"),
        new("pt", "Português (Brasil)"),
        new("pl", "Polski"),
        new("tr", "Türkçe"),
        new("ru", "Русский"),
    };

    private static readonly Dictionary<string, string> _english = LoadTable("en");
    private static Dictionary<string, string> _active = _english;

    /// <summary>The language code currently applied to the UI.</summary>
    public static string Current { get; private set; } = "en";

    /// <summary>Raised on the UI thread after <see cref="Apply"/> switched language, so formatted
    /// strings built in code (status lines, counts) can be rebuilt.</summary>
    public static event Action? Changed;

    public static bool IsSupported(string? code) => Languages.Any(l => l.Code == code);

    /// <summary>
    /// The language to start in. An explicit choice wins; a fresh install follows Windows' display
    /// language when we have a translation for it, and English otherwise.
    /// </summary>
    public static string ResolveInitial(string? saved)
    {
        if (IsSupported(saved)) return saved!;
        string os = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return IsSupported(os) ? os : "en";
    }

    public static void Apply(string? code)
    {
        if (!IsSupported(code)) code = "en";

        var app = System.Windows.Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => Apply(code));
            return;
        }

        _active = code == "en" ? _english : LoadTable(code!);
        Current = code!;

        if (app != null)
        {
            // Publish every English key so a string a translation lacks still shows up (in English).
            foreach (var key in _english.Keys)
                app.Resources["Str." + key] = Lookup(key);
        }

        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Text for a key in the active language; English, then the key itself, as fallbacks.</summary>
    public static string Get(string key) => Lookup(key);

    public static string Format(string key, params object?[] args)
    {
        string pattern = Lookup(key);
        if (args.Length == 0) return pattern;
        try { return string.Format(CultureInfo.CurrentCulture, pattern, args); }
        catch (FormatException)
        {
            // A translation with a broken placeholder must never throw into a UI handler — fall
            // back to the English pattern, which the CI check guarantees is well-formed.
            try { return string.Format(CultureInfo.CurrentCulture, _english.GetValueOrDefault(key, key), args); }
            catch { return pattern; }
        }
    }

    /// <summary>
    /// Count-aware text. Looks up <c>key.One</c> / <c>key.Few</c> / <c>key.Many</c> / <c>key.Other</c>
    /// using the plural rules of the active language, with <c>{0}</c> set to the count.
    /// </summary>
    public static string Plural(string key, long count, params object?[] extra)
    {
        // Resolve the form inside the active language first, so a Russian "few" never falls back to
        // an English sentence; only a language that lacks the key entirely falls back to English rules.
        string full = $"{key}.{PluralForm(Current, count)}";
        if (!_active.ContainsKey(full))
            full = _active.ContainsKey($"{key}.Other") ? $"{key}.Other" : $"{key}.{PluralForm("en", count)}";

        var args = new object?[extra.Length + 1];
        args[0] = count;
        extra.CopyTo(args, 1);
        return Format(full, args);
    }

    /// <summary>CLDR plural category for the languages we ship.</summary>
    public static string PluralForm(string code, long n)
    {
        long abs = Math.Abs(n);
        switch (code)
        {
            case "ru":
                if (abs % 10 == 1 && abs % 100 != 11) return "One";
                if (abs % 10 is >= 2 and <= 4 && abs % 100 is < 12 or > 14) return "Few";
                return "Many";
            case "pl":
                if (abs == 1) return "One";
                if (abs % 10 is >= 2 and <= 4 && abs % 100 is < 12 or > 14) return "Few";
                return "Many";
            case "fr":
            case "pt":
                return abs is 0 or 1 ? "One" : "Other";
            default:
                return abs == 1 ? "One" : "Other";
        }
    }

    private static string Lookup(string key)
    {
        if (_active.TryGetValue(key, out var text) && text.Length > 0) return text;
        if (_english.TryGetValue(key, out var en)) return en;
        return key;
    }

    private static Dictionary<string, string> LoadTable(string code)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Localization.{code}.json");
            if (stream == null) return table;

            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String && !prop.Name.StartsWith('_'))
                    table[prop.Name] = prop.Value.GetString() ?? "";
        }
        catch (Exception ex)
        {
            DiagnosticsService.Warn("i18n", $"Could not load language '{code}'", ex);
        }
        return table;
    }
}

/// <summary>Short alias for localized strings in code: <c>L.T("Key")</c>, <c>L.T("Key", arg)</c>, <c>L.N("Key", count)</c>.</summary>
public static class L
{
    public static string T(string key) => LocalizationService.Get(key);
    public static string T(string key, params object?[] args) => LocalizationService.Format(key, args);
    public static string N(string key, long count, params object?[] extra) => LocalizationService.Plural(key, count, extra);
}
