using Synth.Domain;
namespace Synth.Core;

/// <summary>
/// Lightweight RU→EN query expansion. When a search query contains Cyrillic characters,
/// appends the English equivalents of any recognised Russian code/programming terms to the
/// query text before it is embedded, so a Russian query can still land near the (English)
/// code it describes. Deliberately a small hardcoded dictionary — not a translator; the
/// heavier LLM-based expansion noted in the roadmap is out of scope. Mirrors Sonar's
/// term-expansion step.
/// </summary>
public sealed class QueryExpander
{
    // Modest set of common programming / domain terms. Keys are lowercase Russian words,
    // values their English counterparts appended to the query. Not meant to be exhaustive.
    private static readonly IReadOnlyDictionary<string, string> Dictionary =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["класс"] = "class",
            ["интерфейс"] = "interface",
            ["метод"] = "method",
            ["функция"] = "function",
            ["конструктор"] = "constructor",
            ["свойство"] = "property",
            ["поиск"] = "search",
            ["найти"] = "find",
            ["получить"] = "get",
            ["создать"] = "create",
            ["добавить"] = "add",
            ["удалить"] = "delete",
            ["обновить"] = "update",
            ["пользователь"] = "user",
            ["запрос"] = "query",
            ["индекс"] = "index",
            ["файл"] = "file",
            ["строка"] = "string",
            ["число"] = "number",
            ["список"] = "list",
            ["база"] = "database",
            ["данные"] = "data",
            ["ошибка"] = "error",
            ["тест"] = "test",
            ["сервис"] = "service",
            ["имя"] = "name",
            ["путь"] = "path",
        };

    /// <summary>
    /// Returns <paramref name="query"/> unchanged when it is null/blank or contains no
    /// Cyrillic characters. Otherwise appends the English translations of any recognised
    /// Russian terms it contains, skipping terms whose translation is already present.
    /// </summary>
    public string Expand(string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || !ContainsCyrillic(query))
            return query ?? string.Empty;

        var present = new HashSet<string>(IdentifierTokenizer.Tokenize(query), StringComparer.OrdinalIgnoreCase);

        var additions = new List<string>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in IdentifierTokenizer.Tokenize(query))
        {
            if (!Dictionary.TryGetValue(token, out var translation))
                continue;
            // Don't duplicate a translation the query already carries or we've just appended.
            if (present.Contains(translation) || !added.Add(translation))
                continue;
            additions.Add(translation);
        }

        return additions.Count == 0
            ? query
            : $"{query} {string.Join(' ', additions)}";
    }

    private static bool ContainsCyrillic(string text)
    {
        foreach (var c in text)
        {
            // Cyrillic Unicode block (U+0400–U+04FF) covers the Russian alphabet.
            if (c is >= 'Ѐ' and <= 'ӿ')
                return true;
        }

        return false;
    }
}
