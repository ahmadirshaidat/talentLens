using System.Globalization;
using System.Text.RegularExpressions;
using TalentLens.Infrastructure.Storage;
using TalentLens.Web.Localization;

namespace TalentLens.Tests;

public partial class UiAndStorageTests
{
    // Matches T["Key"], T.F("Key", …), _t["Key"], _t.F("Key", …)
    [GeneratedRegex(@"(?:T|_t)(?:\[|\.F\()""([A-Za-z0-9_]+)""")]
    private static partial Regex KeyUsage();

    private static string WebProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "TalentLens.Web")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "TalentLens.Web");
    }

    [Fact]
    public void Every_text_key_used_in_views_and_controllers_exists()
    {
        var files = Directory.EnumerateFiles(WebProjectDir(), "*.cshtml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(WebProjectDir(), "Controllers"), "*.cs"));
        var missing = files
            .SelectMany(f => KeyUsage().Matches(File.ReadAllText(f)))
            .Select(m => m.Groups[1].Value)
            .Where(k => !UiText.Keys.Contains(k))
            .Distinct()
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Text_follows_the_ui_culture()
    {
        var t = new UiText();
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo(UiText.Arabic);
            Assert.True(t.IsArabic);
            Assert.Equal("بحث", t["Search"]);
            Assert.Equal("الخبرات", t.Section("experience"));

            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo(UiText.English);
            Assert.Equal("7 results", t.F("ResultsCount", 7));
            Assert.Equal("unknown_section", t.Section("unknown_section"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Local_storage_saves_reads_and_deletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "tl-tests-" + Guid.NewGuid().ToString("N"));
        var storage = new LocalFileStorage(root);
        var ws = Guid.NewGuid();
        try
        {
            var name = await storage.SaveAsync(ws, Guid.NewGuid(), ".PDF", new MemoryStream("%PDF"u8.ToArray()));

            Assert.EndsWith(".pdf", name);
            await using (var read = storage.OpenRead(ws, name))
                Assert.Equal((byte)'%', (byte)read.ReadByte());
            storage.Delete(ws, name);
            Assert.Throws<FileNotFoundException>(() => storage.OpenRead(ws, name));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../secret.pdf")]
    [InlineData("..\\secret.pdf")]
    [InlineData("")]
    public void Local_storage_rejects_path_traversal(string storedName)
    {
        var storage = new LocalFileStorage(Path.GetTempPath());
        Assert.Throws<ArgumentException>(() => storage.OpenRead(Guid.NewGuid(), storedName));
    }

    [Fact]
    public async Task Local_storage_rejects_other_extensions()
    {
        var storage = new LocalFileStorage(Path.GetTempPath());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), ".exe", new MemoryStream()));
    }
}
