using ToolKit.Tools.Common;
using ToolKit.Tools.Extension;

namespace ToolKit.Tests.Utility;

public class MultiListDictionaryTests
{
    [Fact]
    public void AddAppendsIndependentLists_AndPreservesDuplicatesAndNullValues()
    {
        var dictionary = new MultiListDictionary<string, string, string?>();
        dictionary.Add("player", "items", "sword");
        dictionary.Add("player", "items", "sword");
        dictionary.Add("player", "items", null);
        dictionary.Add("player", "effects", "shield");
        dictionary.Add("enemy", "items", "bow");

        Assert.Equal(2, dictionary.Count);
        Assert.Equal(new string?[] { "sword", "sword", null }, dictionary["player", "items"]);
        Assert.Equal(new[] { "shield" }, dictionary["player", "effects"]);
        Assert.Equal(new[] { "bow" }, dictionary["enemy", "items"]);
        Assert.True(dictionary.ContainsKey("player", "items"));
        Assert.True(dictionary.TryGetValue("player", "items", out var items));
        Assert.Same(dictionary["player"]["items"], items);
        Assert.True(dictionary.Remove("player", "items", null));
        Assert.Equal(new[] { "sword", "sword" }, items);
    }

    [Fact]
    public void IndexerCreatesAndReplacesLists_WithoutClearingPreviousReferences()
    {
        var dictionary = new MultiListDictionary<string, string, int>();
        var original = new List<int> { 1, 2 };
        dictionary["group", "first"] = original;
        var group = dictionary["group"];
        var second = new List<int>();
        dictionary["group", "second"] = second;
        var replacement = new List<int> { 3 };
        dictionary["group", "first"] = replacement;
        replacement.Add(4);

        Assert.Single(dictionary);
        Assert.Same(group, dictionary["group"]);
        Assert.Equal(2, group.Count);
        Assert.Same(replacement, dictionary["group", "first"]);
        Assert.Equal(new[] { 3, 4 }, dictionary["group", "first"]);
        Assert.Equal(new[] { 1, 2 }, original);
        Assert.Same(second, dictionary["group", "second"]);
        Assert.True(dictionary.ContainsKey("group", "second"));
    }

    [Fact]
    public void MissingReadsDoNotCreateGroups_AndResetOutputToNull()
    {
        var dictionary = new MultiListDictionary<string, string, int>();
        dictionary.Add("group", "items", 1);

        foreach (var key1 in new[] { "group", "missing" })
        {
            List<int>? values = new() { 123 };
            Assert.False(dictionary.ContainsKey(key1, "missing"));
            Assert.False(dictionary.TryGetValue(key1, "missing", out values));
            Assert.Null(values);
            Assert.Throws<KeyNotFoundException>(() => dictionary[key1, "missing"]);
        }

        Assert.Single(dictionary);
        Assert.Single(dictionary["group"]);
        Assert.Equal(new[] { 1 }, dictionary["group", "items"]);
    }

    [Fact]
    public void RemoveValueDeletesOnlyFirstMatch_AndPrunesBothLevelsWhenTheyBecomeEmpty()
    {
        var dictionary = new MultiListDictionary<string, string, int>();
        dictionary.Add("player", "items", 1);
        dictionary.Add("player", "items", 1);
        dictionary.Add("player", "effects", 2);
        dictionary.Add("enemy", "items", 3);
        var items = dictionary["player", "items"];
        var player = dictionary["player"];

        Assert.False(dictionary.Remove("missing", "items", 1));
        Assert.False(dictionary.Remove("player", "missing", 1));
        Assert.False(dictionary.Remove("player", "items", 99));
        Assert.True(dictionary.Remove("player", "items", 1));
        Assert.Equal(new[] { 1 }, items);
        Assert.True(dictionary.Remove("player", "items", 1));
        Assert.Empty(items);
        Assert.False(dictionary.ContainsKey("player", "items"));
        Assert.True(dictionary.ContainsKey("player"));
        Assert.True(dictionary.Remove("player", "effects", 2));
        Assert.Empty(player);
        Assert.False(dictionary.ContainsKey("player"));
        Assert.False(dictionary.Remove("player", "effects", 2));
        Assert.Single(dictionary);
        Assert.Equal(new[] { 3 }, dictionary["enemy", "items"]);
    }

    [Fact]
    public void RemovePairDetachesWholeLists_WithoutClearingThem()
    {
        var dictionary = new MultiListDictionary<string, string, int>();
        dictionary["group", "items"] = new List<int> { 1, 2 };
        dictionary["group", "empty"] = new List<int>();
        var items = dictionary["group", "items"];
        var group = dictionary["group"];

        Assert.False(dictionary.Remove("missing", "items"));
        Assert.False(dictionary.Remove("group", "missing"));
        Assert.True(dictionary.Remove("group", "items"));
        Assert.Equal(new[] { 1, 2 }, items);
        Assert.True(dictionary.ContainsKey("group"));
        Assert.True(dictionary.Remove("group", "empty"));
        Assert.Empty(group);
        Assert.Empty(dictionary);
        Assert.False(dictionary.Remove("group", "items"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullKeysAreRejectedByAllPairOperations_WithoutChangingGroups(bool groupExists)
    {
        var dictionary = new MultiListDictionary<string, string, int>();
        if (groupExists) dictionary.Add("group", "items", 1);

        Action<string, string>[] operations =
        {
            (key1, key2) => dictionary.Add(key1, key2, 2),
            (key1, key2) => dictionary[key1, key2] = new List<int> { 2 },
            (key1, key2) => { _ = dictionary[key1, key2]; },
            (key1, key2) => dictionary.ContainsKey(key1, key2),
            (key1, key2) => dictionary.TryGetValue(key1, key2, out _),
            (key1, key2) => dictionary.Remove(key1, key2),
            (key1, key2) => dictionary.Remove(key1, key2, 1)
        };

        foreach (var operation in operations)
        {
            Assert.Throws<ArgumentNullException>("key1", () => operation(null!, "items"));
            Assert.Throws<ArgumentNullException>("key2", () => operation("group", null!));
        }

        Assert.Equal(groupExists ? 1 : 0, dictionary.Count);
        if (groupExists) Assert.Equal(new[] { 1 }, dictionary["group", "items"]);
    }

    [Fact]
    public void PairIndexerRejectsNullLists_WithoutReplacingExistingDataOrCreatingGroups()
    {
        var dictionary = new MultiListDictionary<string, string, int>();
        dictionary.Add("group", "items", 1);
        var group = dictionary["group"];
        var items = dictionary["group", "items"];

        Assert.Throws<ArgumentNullException>("value", () => dictionary["missing", "items"] = null!);
        Assert.Throws<ArgumentNullException>("value", () => dictionary["group", "items"] = null!);

        Assert.Single(dictionary);
        Assert.Same(group, dictionary["group"]);
        Assert.Same(items, dictionary["group", "items"]);
        Assert.Equal(new[] { 1 }, items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedInnerInsertionDoesNotPublishAnEmptyOuterGroup(bool useIndexer)
    {
        var dictionary = new MultiListDictionary<string, ThrowingKey, int>();
        var key = new ThrowingKey();

        Assert.Throws<InvalidOperationException>(() =>
        {
            if (useIndexer) dictionary["group", key] = new List<int> { 1 };
            else dictionary.Add("group", key, 1);
        });

        Assert.Empty(dictionary);
    }

    [Fact]
    public void SingleKeyOperationsAndDirectMutationsKeepDictionaryReferenceSemantics()
    {
        var group = new ListDictionary<string, int>();
        group.Add("items", 1);
        var items = group["items"];
        var dictionary = new MultiListDictionary<string, string, int>();
        dictionary.Add("group", group);
        dictionary["empty"] = new ListDictionary<string, int>();

        Assert.Throws<ArgumentException>(() => dictionary.Add("group", new ListDictionary<string, int>()));
        Assert.True(dictionary.TryGetValue("group", out var storedGroup));
        Assert.Same(group, storedGroup);
        items.Clear();
        Assert.True(dictionary.ContainsKey("group", "items"));
        Assert.False(dictionary.Remove("group", "items", 1));
        Assert.True(group.ContainsKey("items"));
        group.Remove("items");
        Assert.True(dictionary.ContainsKey("group"));
        Assert.False(dictionary.Remove("group", "items"));
        Assert.False(dictionary.Remove("empty", "missing"));
        Assert.True(dictionary.ContainsKey("empty"));
        Assert.True(dictionary.Remove("empty"));

        group.Add("items", 2);
        Assert.True(dictionary.Remove("group"));
        Assert.Equal(new[] { 2 }, group["items"]);
        dictionary["group"] = group;
        dictionary.Clear();

        Assert.Empty(dictionary);
        Assert.Equal(new[] { 2 }, group["items"]);
    }

    [Fact]
    public void AddReusesDuplicateDetectionConfiguredOnAnExistingInnerDictionary()
    {
        var group = new ListDictionary<string, int>(0, 4, true);
        group.Add("items", 1);
        var dictionary = new MultiListDictionary<string, string, int>();
        dictionary.Add("group", group);

        Assert.Throws<ArgumentException>(() => dictionary.Add("group", "items", 1));
        dictionary.Add("group", "items", 2);

        Assert.Same(group, dictionary["group"]);
        Assert.Single(group);
        Assert.Equal(new[] { 1, 2 }, dictionary["group", "items"]);
    }

    [Fact]
    public void ListDictionaryRemoveReturnsFalseForMissingValues_AndPreservesExplicitEmptyLists()
    {
        var dictionary = new ListDictionary<string, int>();
        dictionary.Add("items", 1);
        dictionary["empty"] = new List<int>();

        Assert.False(dictionary.Remove("missing", 1));
        Assert.False(dictionary.Remove("items", 2));
        Assert.Equal(new[] { 1 }, dictionary["items"]);
        Assert.False(dictionary.Remove("empty", 1));
        Assert.True(dictionary.ContainsKey("empty"));
        Assert.True(dictionary.Remove("items", 1));
        Assert.False(dictionary.ContainsKey("items"));
        Assert.Single(dictionary);
    }

    private sealed class ThrowingKey
    {
        public override int GetHashCode() => throw new InvalidOperationException("Hashing failed.");
    }
}
