using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Material.Icons;
using Markdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using Hakata.Controls;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;
using Hakata.ViewModels;
using Hakata.Views;
using Xunit;
using VisualExtensions = Avalonia.VisualTree.VisualExtensions;

namespace Hakata;

/// <summary>
/// UI tests that need the real windows: under [AvaloniaFact] the headless app is already
/// initialized (TestSupport.TestAppBuilder), so these run against Application.Current.
/// </summary>
public class UiTests : SelfTestBase
{
    /// <summary>
    /// Clicks a history row's star through the visual tree: that command is reached with a
    /// ReflectionBinding, which the compiler cannot check, so it needs a live window to prove it works.
    /// </summary>
    [AvaloniaFact]
    public void RowTest()
    {
        using var services = AppServices.Create();
        var window = services.GetRequiredService<MainWindow>();
        var history = services.GetRequiredService<History>();
        var target = history.Entries.FirstOrDefault(e => !e.Favorite);
        if (target is null)
        {
            // self-seeding: a fresh private home has no history, so add one non-favorite and one
            // favorite entry through the store, mirroring how MetaTest seeds its entry, so both
            // star states render. An explicit HAKATA_HOME with entries still wins.
            history.Add(new HistoryEntry { Source = "rowtest", Output = "rowtest output", From = "en", To = "en", Favorite = false });
            history.Add(new HistoryEntry { Source = "rowtest fav", Output = "rowtest favorite", From = "en", To = "en", Favorite = true });
            window.ViewModel.History.Refresh(); // the sidebar binds the VM's collection, not the store's
            target = history.Entries.FirstOrDefault(e => !e.Favorite);
        }
        Assert.NotNull(target); // a seeded HAKATA_HOME can also provide the entry

        window.Show();
        Pump(30);
        var stars = VisualExtensions.GetVisualDescendants(window)
            .OfType<IconButton>()
            .Where(b => b.Icon is MaterialIconKind.Star or MaterialIconKind.StarOutline)
            .ToList();
        var button = stars.FirstOrDefault(b => ReferenceEquals(b.DataContext, target));
        var bound = button?.Command is { } command && command.CanExecute(button.CommandParameter);
        if (bound) button!.Command!.Execute(button.CommandParameter);
        Pump(10);

        // the row keeps its control (Refresh syncs in place), so the swap only happens if the entry
        // notifies Favorite: exactly the filled star must now be the visible one (#0067)
        var shown = stars.Where(b => ReferenceEquals(b.DataContext, target) && b.IsVisible).ToList();
        var visible = shown.Select(b => b.Icon).ToList();
        var swapped = visible.Count == 1 && visible[0] == MaterialIconKind.Star;
        // and it must be the accent star, not the muted grey of the unfavorited one (#0068): the colour
        // comes from the .starred class, so compare against the resource the style reads
        var accent = Application.Current!.Resources.TryGetResource("Accent", window.ActualThemeVariant, out var res)
            ? (res as ISolidColorBrush)?.Color
            : null;
        var shownColor = (shown.FirstOrDefault()?.Foreground as ISolidColorBrush)?.Color;
        var colored = accent is not null && shownColor == accent;
        Console.WriteLine($"rowtest: {stars.Count} star button(s), command bound={bound}, favorite={target!.Favorite}, visible stars=[{string.Join(",", visible)}], star colour={shownColor} accent={accent}");
        Assert.True(bound, "rowtest: the star command is bound and can execute");
        Assert.True(target.Favorite, "rowtest: clicking the star favorites the entry");
        Assert.True(swapped, "rowtest: the visible star swapped to the filled one");
        Assert.True(colored, "rowtest: the filled star wears the accent colour");
    }

    /// <summary>
    /// Regression check for issue #0022: the history row's meta line (target · age · cost) once drew
    /// its cost text over the row's favorite star in the 210px sidebar. The row is a 3-column grid
    /// (target trims in *, cost in Auto, star in Auto); this proves the columns never overlap by
    /// laying out the real window with a long target name plus a cost, then comparing the laid-out
    /// bounds of the cost TextBlock and the star IconButton.
    /// </summary>
    [AvaloniaFact]
    public void MetaTest()
    {
        using var services = AppServices.Create();
        var window = services.GetRequiredService<MainWindow>();
        window.Show();
        Pump(10);

        // worst case for the old stack layout: a long target label, a live cost and an unfavorite
        // entry (so the outline star is visible). Added straight to the bound collection, not the
        // store, so the harness home stays empty.
        var entry = new HistoryEntry { To = "zh_tw", Source = "metatest", Cost = 0.0123, Favorite = false };
        window.ViewModel.History.Entries.Add(entry);
        Pump(20);

        var descendants = VisualExtensions.GetVisualDescendants(window).ToList();
        var cost = descendants.OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == entry.CostShort);
        var star = descendants.OfType<IconButton>()
            .FirstOrDefault(b => ReferenceEquals(b.DataContext, entry) && b.IsVisible);
        Assert.True(cost is not null, $"metatest: row not realized (cost text '{entry.CostShort}' not found)");
        Assert.True(star is not null, "metatest: star button not found");

        // both bounds are parent-relative; map their top-left corners into window space first
        var costLeft = cost!.TranslatePoint(new Point(0, 0), window) ?? default;
        var starLeft = star!.TranslatePoint(new Point(0, 0), window) ?? default;
        var costRight = costLeft.X + cost.Bounds.Width;
        const double epsilon = 0.5;
        var overlap = costRight > starLeft.X + epsilon;
        Console.WriteLine($"{(overlap ? "OVERLAP" : "ok"),-8} cost right={costRight:0.#} star left={starLeft.X:0.#} " +
                          $"(target='{entry.Target}' ago='{entry.AgoShort}' cost='{entry.CostShort}')");
        Assert.False(overlap, $"metatest: cost text overlaps the star (cost right={costRight:0.#} star left={starLeft.X:0.#})");
    }

    /// <summary>Steps the window through awkward sizes and checks the layout never overflows its client area.</summary>
    [AvaloniaFact]
    public void ResizeTest()
    {
        using var services = AppServices.Create();
        var window = services.GetRequiredService<MainWindow>();
        window.Width = 1200;
        window.Height = 820;
        window.Show();
        Pump(20);

        var sizes = new (int W, int H)[]
        {
            (1200, 820), (900, 560), (760, 480), (1600, 1000), (1600, 560), (980, 1400), (1100, 400),
        };
        var failures = new List<string>();
        var content = window.Content as Control;
        Assert.True(content is not null, "resize: window has no content");

        foreach (var (w, h) in sizes)
        {
            try
            {
                // Resize through the layout system: TopLevel.ClientSize has no public setter.
                content!.Measure(new Size(w, h));
                content.Arrange(new Rect(0, 0, w, h));
                Pump(6);
                content.Measure(new Size(w, h));
                content.Arrange(new Rect(0, 0, w, h));

                var overflows = content.Bounds.Width > w + 0.5 || content.Bounds.Height > h + 0.5;
                if (overflows) failures.Add($"resizetest: {w}x{h} -> content {content.Bounds.Width:0}x{content.Bounds.Height:0} overflows");
                Console.WriteLine($"{(overflows ? "OVERFLOW" : "ok"),-8} {w}x{h} -> content {content.Bounds.Width:0}x{content.Bounds.Height:0}");
                RenderTo(content, new Size(w, h), Scratch($"resize-{w}x{h}.png"));
            }
            catch (Exception e)
            {
                failures.Add($"resizetest: resizing to {w}x{h}: {e.GetType().Name}: {e.Message}");
                Console.WriteLine($"FAIL     resizing to {w}x{h}: {e.GetType().Name}: {e.Message}");
            }
        }
        Console.WriteLine(failures.Count == 0 ? "resize: layout stable" : $"resize: {failures.Count} problem(s)");
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    /// <summary>
    /// Opens the settings dialog and checks the help icons: the footnotes live in their tooltip and
    /// their automation name, so a missing explanation is the whole feature failing silently.
    /// </summary>
    [AvaloniaFact]
    public void SettingsTest()
    {
        using var services = AppServices.Create();
        var window = services.GetRequiredService<SettingsWindow>();
        window.Show();
        Pump(20);

        // the dialog is a tab strip now: a page that was never selected has no laid-out children, so
        // every page is visited and its help icons and pickers are collected from there
        var tabs = VisualExtensions.GetVisualDescendants(window)
            .OfType<TabControl>()
            .FirstOrDefault();
        var icons = new List<HelpDot>();
        var boxes = new List<AutoCompleteBox>();
        for (var page = 0; page < (tabs?.ItemCount ?? 1); page++)
        {
            if (tabs is not null) tabs.SelectedIndex = page;
            Pump(8);
            var shown = VisualExtensions.GetVisualDescendants(tabs ?? (Visual)window)
                .OfType<HelpDot>()
                .ToList();
            icons.AddRange(shown);
            // every picker, including the provider rows built from a template, has to carry the app's filter:
            // a picker on the default filter demands stricter typing than its neighbours
            boxes.AddRange(VisualExtensions.GetVisualDescendants(tabs ?? (Visual)window)
                .OfType<AutoCompleteBox>()
                .Where(b => !boxes.Contains(b)));
            Render(window, Scratch($"settings-{page}.png"));
        }
        if (tabs is not null) tabs.SelectedIndex = 0;
        Pump(8);
        var silent = icons.Where(i => ToolTip.GetTip(i) is not string { Length: > 8 }
                                        || i.Bounds.Width <= 0).ToList();
        var unfiltered = boxes.Where(b => !ReferenceEquals(b.ItemFilter, FuzzyMatch.Predicate)).ToList();
        Render(window, Scratch("settings.png"));
        // the tightest size the window allows: the provider card is the first thing to squeeze.
        // A shown window cannot be re-measured here, so this is a second one.
        var tight = new SettingsWindow();
        tight.Width = tight.MinWidth;
        tight.Height = tight.MinHeight;
        tight.Show();
        Pump(12);
        Render(tight, Scratch("settings-min.png"));
        tight.Close();
        Console.WriteLine($"settings: {icons.Count} help icon(s), {silent.Count} without a tooltip, " +
                          $"{boxes.Count} picker(s), {unfiltered.Count} without the fuzzy filter");
        Assert.True(icons.Count >= 6 && silent.Count == 0 && boxes.Count >= 7 && unfiltered.Count == 0,
            $"settings: {icons.Count} help icon(s) ({silent.Count} silent), {boxes.Count} picker(s) ({unfiltered.Count} unfiltered)");
    }

    /// <summary>
    /// Scrolls a model picker through a list of mixed-length ids and checks the drop-down keeps one
    /// width. The Fluent template sizes the popup to the widest row it has built, so a virtualized
    /// list made it stretch to the long ids and shrink back to the short ones.
    /// </summary>
    [AvaloniaFact]
    public void PopupTest()
    {
        // short ids first, long "provider · model" labels last: they only enter the list once scrolled to
        var items = Enumerable.Range(0, 40).Select(i => i < 20 ? $"short-{i}" : $"a-very-long-provider · extremely-long-model-id-{i}").ToList();
        var box = new AutoCompleteBox
        {
            Width = 150, ItemsSource = items, MinimumPrefixLength = 0,
            FilterMode = AutoCompleteFilterMode.Contains,
        };
        var window = new Window { Width = 620, Height = 420, Content = new StackPanel { Children = { box } } };
        window.Show();
        Pump(10);
        box.IsDropDownOpen = true;
        Pump(10);

        var popup = VisualExtensions.GetVisualDescendants(box)
            .OfType<Popup>().FirstOrDefault();
        var list = popup?.Child is null ? null : VisualExtensions.GetVisualDescendants(popup.Child)
            .OfType<ListBox>().FirstOrDefault();
        var scroll = list is null ? null : VisualExtensions.GetVisualDescendants(list)
            .OfType<ScrollViewer>().FirstOrDefault();
        Assert.True(popup?.Child is Control, "popuptest: no drop-down opened");
        Assert.True(list is not null && scroll is not null, "popuptest: no list or scroll viewer in the drop-down");
        var content = (Control)popup!.Child!;

        var widths = new List<double>();
        for (var offset = 0d; offset <= scroll!.Extent.Height; offset += scroll.Viewport.Height / 2)
        {
            scroll.Offset = new Vector(0, offset);
            Pump(4);
            widths.Add(content.Bounds.Width);
        }

        var spread = widths.Max() - widths.Min();
        var rows = VisualExtensions.GetVisualDescendants(list!).OfType<ListBoxItem>().ToList();
        Console.WriteLine($"popuptest: field {box.Bounds.Width:0}px, {rows.Count} row(s), drop-down widths {string.Join("/", widths.Distinct().Select(w => w.ToString("0")))}, spread {spread:0}px");
        var ok = spread <= 0.5 && content.Bounds.Width > box.Bounds.Width;
        Console.WriteLine(ok ? "popuptest: drop-down width stable and fits the long ids" : "popuptest: drop-down resizes while scrolling");
        Assert.True(ok, $"popuptest: drop-down width spread {spread:0}px while scrolling");
    }

    /// <summary>
    /// The update window: the view model's derived text (version chip, notes fallback and trim), then a
    /// live window proving the XAML builds a MarkdownScrollViewer bound to the release notes.
    /// </summary>
    [AvaloniaFact]
    public void UpdaterTest()
    {
        var metadata = new AppMetadata();
        var vm = new UpdateViewModel(
            new UpdateResponse("1.3.0", "https://example.invalid/x", ""), metadata);
        Assert.True(vm.NewVersion == "v1.3.0", $"NewVersion was '{vm.NewVersion}'");
        Assert.True(vm.ReleaseNotes == "No release notes were provided.", $"empty notes gave '{vm.ReleaseNotes}'");
        Assert.True(vm.VersionSummary.Contains(metadata.Version.ToString()), $"VersionSummary was '{vm.VersionSummary}'");
        Assert.True(!vm.HasStatus, "HasStatus true before any open attempt");
        var trimmed = new UpdateViewModel(
            new UpdateResponse("1.3.0", "https://example.invalid/x", "  ## What's new\n- a fix  "), metadata);
        Assert.True(trimmed.ReleaseNotes == "## What's new\n- a fix", $"trimmed notes gave '{trimmed.ReleaseNotes}'");

        var window = new UpdaterWindow(vm);
        window.Show();
        Pump(20);
        var viewer = VisualExtensions.GetVisualDescendants(window)
            .OfType<MarkdownScrollViewer>()
            .FirstOrDefault();
        Assert.True(viewer is not null, "no MarkdownScrollViewer in the visual tree");
        Assert.True(viewer?.Markdown == vm.ReleaseNotes, $"viewer Markdown was '{viewer?.Markdown}'");
        // security-audit C2 (2026-11-16): the release-notes viewer must not ship the package-default
        // HyperlinkCommand, which dispatches any scheme to the OS handler
        var linkCmd = (viewer?.Engine as IMarkdownEngine)?.HyperlinkCommand;
        Assert.True(linkCmd is not null && linkCmd.GetType().Name != "DefaultHyperlinkCommand",
            $"viewer HyperlinkCommand is {linkCmd?.GetType().Name ?? "null"}, not the package default");
        linkCmd?.Execute("file:///etc/hostname"); // guarded command: refused before any Process.Start (a dispatch throws and fails the test)
        // an image URL in the rendered notes must resolve through the app's guarded resolver,
        // not the package default that fetches any http(s) URL and opens file: paths
        var pathResolver = (viewer?.Engine as IMarkdownEngine)?.Plugins?.PathResolver;
        Assert.True(pathResolver is not null && ReferenceEquals(pathResolver, MarkdownEngines.GuardedPathResolver),
            $"viewer PathResolver is {pathResolver?.GetType().Name ?? "null"}, the app's guarded resolver");
        window.Close();
    }

    /// <summary>
    /// Every NumericUpDown in the settings dialog must render its up/down spinner pair at identical
    /// widths: the pair shares one StackPanel, so a wider button pushes its neighbour onto the
    /// control's rounded edge (#0086). Reads the laid-out widths of both template buttons.
    /// </summary>
    [AvaloniaFact]
    public void SpinTest()
    {
        using var services = AppServices.Create();
        var window = services.GetRequiredService<SettingsWindow>();
        window.Show();
        Pump(20);
        var tabs = VisualExtensions.GetVisualDescendants(window)
            .OfType<TabControl>().FirstOrDefault();
        var failures = new List<string>();
        for (var page = 0; page < (tabs?.ItemCount ?? 1); page++)
        {
            if (tabs is not null) tabs.SelectedIndex = page;
            Pump(8);
            foreach (var up in VisualExtensions.GetVisualDescendants(window)
                         .OfType<RepeatButton>()
                         .Where(b => b.Name == "PART_IncreaseButton"))
            {
                var down = VisualExtensions.GetVisualDescendants((Visual)up.Parent!)
                               .OfType<RepeatButton>()
                               .FirstOrDefault(b => b.Name == "PART_DecreaseButton");
                if (down is null) { failures.Add("spin: increase button without a decrease sibling"); Console.WriteLine($"spin: increase button without a decrease sibling"); continue; }
                var ok = Math.Abs(up.Bounds.Width - down.Bounds.Width) < 0.05
                         && Math.Abs(up.Bounds.X + up.Bounds.Width - down.Bounds.X) < 0.05;
                if (!ok) failures.Add($"spin: unequal pair up=[{up.Bounds.X:0.#}..{up.Bounds.Right:0.#}] down=[{down.Bounds.X:0.#}..{down.Bounds.Right:0.#}]");
                Console.WriteLine($"{(ok ? "ok" : "UNEQUAL"),-7} up=[{up.Bounds.X:0.#}..{up.Bounds.Right:0.#}] down=[{down.Bounds.X:0.#}..{down.Bounds.Right:0.#}] gap={down.Bounds.X - up.Bounds.Right:0.#}");
            }
        }
        Console.WriteLine(failures.Count == 0 ? "spintest: spinner pairs identical" : $"spintest: {failures.Count} unequal pair(s)");
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }
}
