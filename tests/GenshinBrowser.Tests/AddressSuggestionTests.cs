using GenshinBrowser.Models;
using GenshinBrowser.Utils;
using GenshinBrowser.ViewModels;

namespace GenshinBrowser.Tests;

public sealed class AddressSuggestionTests
{
    [Fact]
    public void MatchesQuery_MatchesTitleOrUrlIgnoringCase()
    {
        Assert.True(EntryText.MatchesQuery("刻晴 培养攻略", "https://www.bilibili.com/video/BV1", "刻晴"));
        Assert.True(EntryText.MatchesQuery("Guide", "https://www.bilibili.com/video/BV1abc", "bv1ABC"));
        Assert.False(EntryText.MatchesQuery("刻晴 培养攻略", "https://www.bilibili.com/video/BV1", "甘雨"));
    }

    [Fact]
    public void Build_PutsFavoritesFirstAndDeduplicatesUrls()
    {
        var favorites = new[]
        {
            new FavoriteEntry { Url = "https://www.bilibili.com/video/BV2", Title = "刻晴 圣遗物" },
        };
        var history = new[]
        {
            new HistoryEntry { Url = "https://www.bilibili.com/video/BV1", Title = "刻晴 培养攻略" },
            new HistoryEntry { Url = "https://WWW.bilibili.com/video/BV2", Title = "刻晴 圣遗物（历史里的同一页）" },
            new HistoryEntry { Url = "https://www.bilibili.com/video/BV3", Title = "甘雨 培养攻略" },
        };

        var suggestions = AddressSuggestion.Build(favorites, history, "刻晴", currentAddress: null);

        Assert.Collection(
            suggestions,
            first =>
            {
                Assert.True(first.IsFavorite);
                Assert.Equal("https://www.bilibili.com/video/BV2", first.Url);
                Assert.Equal("bilibili.com/video/BV2", first.DisplayUrl);
            },
            second =>
            {
                Assert.False(second.IsFavorite);
                Assert.Equal("https://www.bilibili.com/video/BV1", second.Url);
            });
    }

    [Fact]
    public void Build_StopsAtMaxCount()
    {
        var history = Enumerable.Range(1, 20)
            .Select(i => new HistoryEntry { Url = $"https://www.bilibili.com/video/BV{i}", Title = $"攻略 {i}" })
            .ToArray();

        var suggestions = AddressSuggestion.Build(Array.Empty<FavoriteEntry>(), history, "攻略", currentAddress: null, maxCount: 8);

        Assert.Equal(8, suggestions.Count);
        Assert.Equal("https://www.bilibili.com/video/BV1", suggestions[0].Url);
        Assert.Equal("https://www.bilibili.com/video/BV8", suggestions[^1].Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("https://www.bilibili.com/video/BV1")]
    public void Build_ReturnsNothingForBlankInputOrCurrentAddress(string? input)
    {
        var history = new[]
        {
            new HistoryEntry { Url = "https://www.bilibili.com/video/BV1", Title = "刻晴 培养攻略" },
        };

        var suggestions = AddressSuggestion.Build(
            Array.Empty<FavoriteEntry>(),
            history,
            input,
            currentAddress: "https://www.bilibili.com/video/BV1");

        Assert.Empty(suggestions);
    }

    [Fact]
    public void Build_FallsBackToDisplayUrlWhenTitleIsEmpty()
    {
        var history = new[]
        {
            new HistoryEntry { Url = "https://www.bilibili.com/video/BV9", Title = string.Empty },
        };

        var suggestion = Assert.Single(AddressSuggestion.Build(Array.Empty<FavoriteEntry>(), history, "BV9", currentAddress: null));

        Assert.Equal("bilibili.com/video/BV9", suggestion.Title);
    }
}
