using Synth.Core;
using Synth.Domain;

namespace Synth.Core.Tests;

// Proves the camelCase-aware tokenization SYNTH-11's keyword boost relies on.
public class IdentifierTokenizerTests
{
    [Theory]
    [InlineData("GetUserById", new[] { "get", "user", "by", "id" })]
    [InlineData("getUserById", new[] { "get", "user", "by", "id" })]
    [InlineData("HTMLParser", new[] { "html", "parser" })]
    [InlineData("get_user_by_id", new[] { "get", "user", "by", "id" })]
    [InlineData("Repo.GetUser", new[] { "repo", "get", "user" })]
    [InlineData("", new string[0])]
    public void Tokenize_splits_on_camelCase_and_delimiters(string input, string[] expected)
    {
        Assert.Equal(expected, IdentifierTokenizer.Tokenize(input));
    }
}
