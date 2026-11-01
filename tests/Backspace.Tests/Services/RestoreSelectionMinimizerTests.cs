using System.Linq;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public class RestoreSelectionMinimizerTests
{
    [Fact]
    public void Fully_selected_folder_collapses_to_one_pattern()
    {
        // Universe: 3 leaves under /docs.
        var all = new[] { "/docs/a.txt", "/docs/b.txt", "/docs/c.txt" };
        // User selected all 3.
        var selected = all;

        var min = RestoreSelectionMinimizer.Compute(all, selected);

        Assert.Equal(new[] { "/docs" }, min);
    }

    [Fact]
    public void Partially_selected_folder_emits_per_leaf_patterns()
    {
        var all = new[] { "/docs/a.txt", "/docs/b.txt", "/docs/c.txt" };
        var selected = new[] { "/docs/a.txt", "/docs/c.txt" };

        var min = RestoreSelectionMinimizer.Compute(all, selected);

        Assert.Equal(new[] { "/docs/a.txt", "/docs/c.txt" }, min);
    }

    [Fact]
    public void Nested_full_coverage_promotes_to_outermost_folder()
    {
        // /pics, /pics/2024, /pics/2024/jan all fully covered.
        var all = new[]
        {
            "/pics/cover.jpg",
            "/pics/2024/jan/01.jpg",
            "/pics/2024/jan/02.jpg",
            "/pics/2024/feb/01.jpg",
        };
        var selected = all;

        var min = RestoreSelectionMinimizer.Compute(all, selected);

        // Everything under /pics is selected → emit just "/pics".
        Assert.Equal(new[] { "/pics" }, min);
    }

    [Fact]
    public void Mixed_selection_emits_folder_and_loose_leaves()
    {
        var all = new[]
        {
            "/docs/a.txt",
            "/docs/b.txt",
            "/pics/2024/jan/01.jpg",
            "/pics/2024/jan/02.jpg",
            "/pics/2024/feb/03.jpg",
            "/loose.txt",
        };
        // Pick /docs entirely + /pics/2024/jan entirely + /loose.txt.
        var selected = new[]
        {
            "/docs/a.txt", "/docs/b.txt",
            "/pics/2024/jan/01.jpg", "/pics/2024/jan/02.jpg",
            "/loose.txt",
        };

        var min = RestoreSelectionMinimizer.Compute(all, selected);

        Assert.Equal(new[]
        {
            "/docs",
            "/loose.txt",
            "/pics/2024/jan",
        }, min);
    }

    [Fact]
    public void Empty_selection_returns_empty()
    {
        var all = new[] { "/docs/a.txt", "/docs/b.txt" };
        var min = RestoreSelectionMinimizer.Compute(all, System.Array.Empty<string>());
        Assert.Empty(min);
    }

    [Fact]
    public void Backslash_paths_normalize_to_forward_slash()
    {
        var all = new[] { @"\docs\a.txt", @"\docs\b.txt" };
        var selected = all;
        var min = RestoreSelectionMinimizer.Compute(all, selected);
        Assert.Equal(new[] { "/docs" }, min);
    }

    [Fact]
    public void Missing_leading_slash_is_repaired()
    {
        var all = new[] { "docs/a.txt", "docs/b.txt" };
        var selected = all;
        var min = RestoreSelectionMinimizer.Compute(all, selected);
        Assert.Equal(new[] { "/docs" }, min);
    }

    [Fact]
    public void High_count_selection_collapses_to_single_pattern()
    {
        // Simulate the real-world scenario that broke restore: 1500
        // leaves under one folder, all selected. Without the
        // minimizer this would produce 1500 per-file patterns.
        var all = Enumerable.Range(0, 1500)
            .Select(i => $"/big/file-{i:D5}.bin")
            .ToArray();
        var selected = all;

        var min = RestoreSelectionMinimizer.Compute(all, selected);

        Assert.Single(min);
        Assert.Equal("/big", min[0]);
    }

    [Fact]
    public void Two_folders_selected_emit_two_patterns_not_thousands()
    {
        // The user's bug report shape: two folders, each with ~1000
        // leaves; selecting both folders should produce exactly 2
        // patterns.
        var folderA = Enumerable.Range(0, 1000).Select(i => $"/folderA/f-{i:D4}.bin");
        var folderB = Enumerable.Range(0, 1400).Select(i => $"/folderB/f-{i:D4}.bin");
        var all = folderA.Concat(folderB).ToArray();
        var selected = all;

        var min = RestoreSelectionMinimizer.Compute(all, selected);

        Assert.Equal(new[] { "/folderA", "/folderB" }, min);
    }
}
