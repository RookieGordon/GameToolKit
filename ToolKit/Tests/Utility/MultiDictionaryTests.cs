using ToolKit.Tools.Common;

namespace ToolKit.Tests.Utility;

public class MultiDictionaryTests
{
    [Fact]
    public void GroupsStoreIndependentValues_AndDuplicateAddPreservesOriginal()
    {
        var dictionary = new MultiDictionary<string, string, int>();
        dictionary.Add("player", "health", 100);
        dictionary.Add("player", "mana", 50);
        dictionary.Add("enemy", "health", 80);

        Assert.Throws<ArgumentException>(() => dictionary.Add("player", "health", 1));

        Assert.Equal(2, dictionary.Count);
        Assert.Equal(100, dictionary["player", "health"]);
        Assert.Equal(50, dictionary["player", "mana"]);
        Assert.Equal(80, dictionary["enemy", "health"]);
        Assert.True(dictionary.ContainsKey("player", "health"));
        Assert.True(dictionary.TryGetValue("enemy", "health", out var health));
        Assert.Equal(80, health);
    }

    [Fact]
    public void IndexerCreatesGroupsAndEntries_AndUpdatesInPlace()
    {
        var dictionary = new MultiDictionary<int, int, string>();
        dictionary[1, 1] = "first";
        var group = dictionary[1];
        dictionary[1, 2] = "second";
        dictionary[1, 1] = "updated";

        Assert.Single(dictionary);
        Assert.Same(group, dictionary[1]);
        Assert.Equal(2, group.Count);
        Assert.Equal("updated", group[1]);
        Assert.Equal("second", dictionary[1, 2]);
    }

    [Fact]
    public void MissingReadsDoNotCreateGroups_AndResetOutputToDefault()
    {
        var dictionary = new MultiDictionary<string, string, int>();
        dictionary.Add("player", "health", 100);

        foreach (var key1 in new[] { "player", "missing" })
        {
            var value = 123;
            Assert.False(dictionary.ContainsKey(key1, "missing"));
            Assert.False(dictionary.TryGetValue(key1, "missing", out value));
            Assert.Equal(0, value);
            Assert.Throws<KeyNotFoundException>(() => dictionary[key1, "missing"]);
        }

        Assert.Single(dictionary);
        Assert.Single(dictionary["player"]);
    }

    [Fact]
    public void RemoveReportsActualDeletion_AndPrunesOnlyTheEmptiedGroup()
    {
        var dictionary = new MultiDictionary<string, string, int>();
        dictionary.Add("player", "health", 100);
        dictionary.Add("player", "mana", 50);
        dictionary.Add("enemy", "health", 80);

        Assert.False(dictionary.Remove("missing", "health"));
        Assert.False(dictionary.Remove("player", "missing"));
        Assert.True(dictionary.Remove("player", "health"));
        Assert.True(dictionary.ContainsKey("player"));
        Assert.Equal(50, dictionary["player", "mana"]);
        Assert.True(dictionary.Remove("player", "mana"));
        Assert.False(dictionary.ContainsKey("player"));
        Assert.False(dictionary.Remove("player", "mana"));
        Assert.Single(dictionary);
        Assert.Equal(80, dictionary["enemy", "health"]);
    }

    [Fact]
    public void NullValuesRemainDistinctFromMissingEntries()
    {
        var dictionary = new MultiDictionary<string, string, string?>();
        dictionary.Add("group", "null", null);

        Assert.True(dictionary.ContainsKey("group", "null"));
        Assert.True(dictionary.TryGetValue("group", "null", out var value));
        Assert.Null(value);
        Assert.Null(dictionary["group", "null"]);
        Assert.False(dictionary.TryGetValue("group", "missing", out value));
        Assert.Null(value);
        Assert.True(dictionary.Remove("group", "null"));
        Assert.Empty(dictionary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullKeysAreRejectedByAllPairOperations_WithoutChangingGroups(bool groupExists)
    {
        var dictionary = new MultiDictionary<string, string, int>();
        if (groupExists) dictionary.Add("group", "value", 1);

        Action<string, string>[] operations =
        {
            (key1, key2) => dictionary.Add(key1, key2, 2),
            (key1, key2) => dictionary[key1, key2] = 2,
            (key1, key2) => { _ = dictionary[key1, key2]; },
            (key1, key2) => dictionary.ContainsKey(key1, key2),
            (key1, key2) => dictionary.TryGetValue(key1, key2, out _),
            (key1, key2) => dictionary.Remove(key1, key2)
        };

        foreach (var operation in operations)
        {
            Assert.Throws<ArgumentNullException>("key1", () => operation(null!, "value"));
            Assert.Throws<ArgumentNullException>("key2", () => operation("group", null!));
        }

        Assert.Equal(groupExists ? 1 : 0, dictionary.Count);
        if (groupExists) Assert.Equal(1, dictionary["group", "value"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedInnerInsertionDoesNotPublishAnEmptyGroup(bool useIndexer)
    {
        var dictionary = new MultiDictionary<string, ThrowingKey, int>();
        var key = new ThrowingKey();

        Assert.Throws<InvalidOperationException>(() =>
        {
            if (useIndexer) dictionary["group", key] = 1;
            else dictionary.Add("group", key, 1);
        });

        Assert.Empty(dictionary);
    }

    [Fact]
    public void SingleKeyOperationsKeepTheirDictionarySemantics()
    {
        var group = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { { "Health", 100 } };
        var dictionary = new MultiDictionary<string, string, int>();
        dictionary.Add("player", group);
        dictionary["empty"] = new Dictionary<string, int>();

        Assert.True(dictionary.TryGetValue("player", out var storedGroup));
        Assert.Same(group, storedGroup);
        Assert.Equal(100, dictionary["player", "HEALTH"]);
        dictionary["player", "health"] = 90;
        Assert.Equal(90, group["Health"]);
        Assert.Single(group);
        Assert.False(dictionary.Remove("empty", "missing"));
        Assert.True(dictionary.ContainsKey("empty"));
        Assert.True(dictionary.Remove("empty"));

        dictionary.Clear();

        Assert.Empty(dictionary);
        Assert.Equal(90, group["Health"]);
    }

    private sealed class ThrowingKey
    {
        public override int GetHashCode() => throw new InvalidOperationException("Hashing failed.");
    }
}
