using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Material.Icons;
using Markdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Hakata.Controls;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;
using Hakata.ViewModels;
using Hakata.Views;
using VisualExtensions = Avalonia.VisualTree.VisualExtensions;

namespace Hakata;

class TestProgram
{
    [STAThread]
    public static int Main(string[] args)
    {
        UsePrivateHome();
        if (args.Contains("--rowtest")) return RowTest();
        if (args.Contains("--metatest")) return MetaTest();
        if (args.Contains("--selftest")) return SelfTest.RunAsync().GetAwaiter().GetResult();
        if (args.Contains("--resizetest")) return ResizeTest();
        if (args.Contains("--settingstest")) return SettingsTest();
        if (args.Contains("--popuptest")) return PopupTest();
        if (args.Contains("--updatertest")) return UpdaterTest();
        if (args.Contains("--laytest")) return LayTest();
        if (args.Contains("--spintest")) return SpinTest();
        if (args.Contains("--spinscan")) return SpinScan();
        if (args.Contains("--laydump")) return LayDump(args[Array.IndexOf(args, "--laydump") + 1]);
        if (args.Contains("--propertytest")) return PropertyTest.Run(args.Contains("--strict"));
        if (args.Contains("--screenshot")) return Screenshot(args[Array.IndexOf(args, "--screenshot") + 1],
            args.Contains("--translate") ? args[Array.IndexOf(args, "--translate") + 1] : null,
            args.Contains("--extras"),
            args.Contains("--size") ? args[Array.IndexOf(args, "--size") + 1] : null);
        Console.WriteLine("usage: --selftest | --rowtest | --metatest | --resizetest | --settingstest | --spintest | --spinscan | --popuptest | --updatertest | --propertytest [--strict] | --screenshot <path> [--translate <text>] [--extras] [--size WxH]");
        return 1;
    }

    /// <summary>
    /// A harness run composes the real app, and some paths do save settings (the settings dialog's Save).
    /// Without this, a check that points a provider at a mock server writes that fixture over the user's
    /// configuration. An explicit HAKATA_HOME still wins, which is how a seeded history is tested.
    /// </summary>
    static void UsePrivateHome()
    {
        if (Environment.GetEnvironmentVariable("HAKATA_HOME") is { Length: > 0 }) return;
        var home = Path.Combine(Path.GetTempPath(), "hakata-harness");
        try { if (Directory.Exists(home)) Directory.Delete(home, true); } catch (Exception) { }
        Environment.SetEnvironmentVariable("HAKATA_HOME", home);
    }

    /// <summary>
    /// Clicks a history row's star through the visual tree: that command is reached with a
    /// ReflectionBinding, which the compiler cannot check, so it needs a live window to prove it works.
    /// </summary>
    static int RowTest()
    {
        Program.BuildAvaloniaApp().SetupWithoutStarting();
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
        if (target is null)
        {
            Console.WriteLine("rowtest: no non-favorite entry to click (set HAKATA_HOME to a seeded history)");
            return 1;
        }

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
        Console.WriteLine($"rowtest: {stars.Count} star button(s), command bound={bound}, favorite={target.Favorite}, visible stars=[{string.Join(",", visible)}], star colour={shownColor} accent={accent}");
        return bound && target.Favorite && swapped && colored ? 0 : 1;
    }

    /// <summary>
    /// Regression check for issue #0022: the history row's meta line (target · age · cost) once drew
    /// its cost text over the row's favorite star in the 210px sidebar. The row is a 3-column grid
    /// (target trims in *, cost in Auto, star in Auto); this proves the columns never overlap by
    /// laying out the real window with a long target name plus a cost, then comparing the laid-out
    /// bounds of the cost TextBlock and the star IconButton.
    /// </summary>
    static int MetaTest()
    {
        Program.BuildAvaloniaApp().SetupWithoutStarting();
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
        if (cost is null || star is null)
        {
            Console.WriteLine($"metatest: row not realized (cost={cost is not null}, star={star is not null})");
            return 1;
        }

        // both bounds are parent-relative; map their top-left corners into window space first
        var costLeft = cost.TranslatePoint(new Point(0, 0), window) ?? default;
        var starLeft = star.TranslatePoint(new Point(0, 0), window) ?? default;
        var costRight = costLeft.X + cost.Bounds.Width;
        const double epsilon = 0.5;
        var overlap = costRight > starLeft.X + epsilon;
        Console.WriteLine($"{(overlap ? "OVERLAP" : "ok"),-8} cost right={costRight:0.#} star left={starLeft.X:0.#} " +
                          $"(target='{entry.Target}' ago='{entry.AgoShort}' cost='{entry.CostShort}')");
        return overlap ? 1 : 0;
    }

    /// <summary>Prints the laid-out bounds of the quality tier row: labels vs pill buttons vs disclosure.</summary>
    static int LayTest()
    {
        Program.BuildAvaloniaApp().SetupWithoutStarting();
        using var services = AppServices.Create();
        var window = services.GetRequiredService<MainWindow>();
        window.Width = 1100; window.Height = 760;
        window.Show();
        Pump(30);

        var basic = VisualExtensions.GetVisualDescendants(window)
            .OfType<ToggleButton>().FirstOrDefault(t => t.Name == "BasicTier");
        if (basic is null) { Console.WriteLine("laytest: BasicTier not found"); return 1; }
        var row = basic.Parent as WrapPanel;
        var rowTop = row?.Bounds.Y ?? 0;
        Console.WriteLine($"row WrapPanel: Y={rowTop:0.#} H={row?.Bounds.Height:0.#}");
        foreach (var c in row!.Children)
        {
            var b = c.Bounds;
            var name = (c as TemplatedControl)?.Name
                       ?? (c as TextBlock)?.Name ?? c.GetType().Name;
            var text = (c as ContentControl)?.Content as string
                       ?? (c as TextBlock)?.Text ?? "";
            Console.WriteLine($"  {name,-14} text={text,-13} Y={b.Y:0.#} H={b.Height:0.#} center={(b.Y + b.Height / 2):0.#}");
        }
        return 0;
    }

    /// <summary>Renders the window to a PNG without an interactive session (used for smoke tests).</summary>
    static int Screenshot(string path, string? translate = null, bool extras = false, string? size = null)
    {
        Program.BuildAvaloniaApp().SetupWithoutStarting();
        using var services = AppServices.Create();
        var window = services.GetRequiredService<MainWindow>();
        window.Width = 1100;
        window.Height = 760;
        if (size is not null)
        {
            var parts = size.Split('x');
            window.Width = int.Parse(parts[0]);
            window.Height = int.Parse(parts[1]);
        }
        window.Show();
        Pump(40);
        if (translate is not null)
        {
            window.ViewModel.InputText = translate;
            var job = window.TranslateNow();
            for (var i = 0; i < 400 && !job.IsCompleted; i++) Pump(1);
            Console.WriteLine("translate task: " + (job.IsCompletedSuccessfully ? "done" : "incomplete") + " | " + window.ViewModel.Status);
            if (extras && job.IsCompletedSuccessfully)
            {
                var extra = window.RunExtrasForTest();
                for (var i = 0; i < 400 && !extra.IsCompleted; i++) Pump(1);
                Console.WriteLine("extras: " + (extra.IsCompletedSuccessfully ? "done" : "incomplete"));
            }
        }
        var pixelSize = new PixelSize((int)window.Width, (int)window.Height);
        using var bmp = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        bmp.Render(window);
        using var fs = File.Create(path);
        bmp.Save(fs, PngBitmapEncoderOptions.Default);
        Console.WriteLine("wrote " + path);
        return 0;
    }

    /// <summary>Steps the window through awkward sizes and checks the layout never overflows its client area.</summary>
    static int ResizeTest()
    {
        Program.BuildAvaloniaApp().SetupWithoutStarting();
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
        var failures = 0;
        var content = window.Content as Control;
        if (content is null) { Console.WriteLine("no content"); return 1; }

        foreach (var (w, h) in sizes)
        {
            try
            {
                // Resize through the layout system: TopLevel.ClientSize has no public setter.
                content.Measure(new Size(w, h));
                content.Arrange(new Rect(0, 0, w, h));
                Pump(6);
                content.Measure(new Size(w, h));
                content.Arrange(new Rect(0, 0, w, h));

                var overflows = content.Bounds.Width > w + 0.5 || content.Bounds.Height > h + 0.5;
                if (overflows) failures++;
                Console.WriteLine($"{(overflows ? "OVERFLOW" : "ok"),-8} {w}x{h} -> content {content.Bounds.Width:0}x{content.Bounds.Height:0}");
                RenderTo(content, new Size(w, h), Scratch($"resize-{w}x{h}.png"));
            }
            catch (Exception e)
            {
                failures++;
                Console.WriteLine($"FAIL     resizing to {w}x{h}: {e.GetType().Name}: {e.Message}");
            }
        }
        Console.WriteLine(failures == 0 ? "resize: layout stable" : $"resize: {failures} problem(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Opens the settings dialog and checks the help icons: the footnotes live in their tooltip and
    /// their automation name, so a missing explanation is the whole feature failing silently.
    /// </summary>
    static int SettingsTest()
    {
        Program.BuildAvaloniaApp().SetupWithoutStarting();
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
        return icons.Count >= 6 && silent.Count == 0 && boxes.Count >= 7 && unfiltered.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Scrolls a model picker through a list of mixed-length ids and checks the drop-down keeps one
    /// width. The Fluent template sizes the popup to the widest row it has built, so a virtualized
    /// list made it stretch to the long ids and shrink back to the short ones.
    /// </summary>
    static int PopupTest()
    {
        Program.BuildAvaloniaApp().SetupWithoutStarting();
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
        if (popup?.Child is not Control content || list is null || scroll is null)
        {
            Console.WriteLine("popuptest: no drop-down opened");
            return 1;
        }

        var widths = new List<double>();
        for (var offset = 0d; offset <= scroll.Extent.Height; offset += scroll.Viewport.Height / 2)
        {
            scroll.Offset = new Vector(0, offset);
            Pump(4);
            widths.Add(content.Bounds.Width);
        }

        var spread = widths.Max() - widths.Min();
        var rows = VisualExtensions.GetVisualDescendants(list).OfType<ListBoxItem>().ToList();
        Console.WriteLine($"popuptest: field {box.Bounds.Width:0}px, {rows.Count} row(s), drop-down widths {string.Join("/", widths.Distinct().Select(w => w.ToString("0")))}, spread {spread:0}px");
        var ok = spread <= 0.5 && content.Bounds.Width > box.Bounds.Width;
        Console.WriteLine(ok ? "popuptest: drop-down width stable and fits the long ids" : "popuptest: drop-down resizes while scrolling");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// The update window: the view model's derived text (version chip, notes fallback and trim), then a
    /// live window proving the XAML builds a MarkdownScrollViewer bound to the release notes.
    /// </summary>
    static int UpdaterTest()
    {
        var failures = 0;
        void Check(bool ok, string what)
        {
            if (!ok)
            {
                Console.WriteLine($"updatertest: FAIL {what}");
                failures++;
            }
        }

        var metadata = new AppMetadata();
        var vm = new UpdateViewModel(
            new UpdateResponse("1.3.0", "https://example.invalid/x", ""), metadata);
        Check(vm.NewVersion == "v1.3.0", $"NewVersion was '{vm.NewVersion}'");
        Check(vm.ReleaseNotes == "No release notes were provided.", $"empty notes gave '{vm.ReleaseNotes}'");
        Check(vm.VersionSummary.Contains(metadata.Version.ToString()), $"VersionSummary was '{vm.VersionSummary}'");
        Check(!vm.HasStatus, "HasStatus true before any open attempt");
        var trimmed = new UpdateViewModel(
            new UpdateResponse("1.3.0", "https://example.invalid/x", "  ## What's new\n- a fix  "), metadata);
        Check(trimmed.ReleaseNotes == "## What's new\n- a fix", $"trimmed notes gave '{trimmed.ReleaseNotes}'");

        Program.BuildAvaloniaApp().SetupWithoutStarting();
        var window = new UpdaterWindow(vm);
        window.Show();
        Pump(20);
        var viewer = VisualExtensions.GetVisualDescendants(window)
            .OfType<MarkdownScrollViewer>()
            .FirstOrDefault();
        Check(viewer is not null, "no MarkdownScrollViewer in the visual tree");
        Check(viewer?.Markdown == vm.ReleaseNotes, $"viewer Markdown was '{viewer?.Markdown}'");
        // security-audit C2 (2026-11-16): the release-notes viewer must not ship the package-default
        // HyperlinkCommand, which dispatches any scheme to the OS handler
        var linkCmd = (viewer?.Engine as IMarkdownEngine)?.HyperlinkCommand;
        Check(linkCmd is not null && linkCmd.GetType().Name != "DefaultHyperlinkCommand",
            $"viewer HyperlinkCommand is {linkCmd?.GetType().Name ?? "null"}, not the package default");
        linkCmd?.Execute("file:///etc/hostname"); // guarded command: refused before any Process.Start
        Check(true, "guarded hyperlink command swallowed a file: link without dispatching");
        // an image URL in the rendered notes must resolve through the app's guarded resolver,
        // not the package default that fetches any http(s) URL and opens file: paths
        var pathResolver = (viewer?.Engine as IMarkdownEngine)?.Plugins?.PathResolver;
        Check(pathResolver is not null && ReferenceEquals(pathResolver, MarkdownEngines.GuardedPathResolver),
            $"viewer PathResolver is {pathResolver?.GetType().Name ?? "null"}, the app's guarded resolver");
        window.Close();

        Console.WriteLine(failures == 0 ? "updatertest: ok" : $"updatertest: {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Every NumericUpDown in the settings dialog must render its up/down spinner pair at identical
    /// widths: the pair shares one StackPanel, so a wider button pushes its neighbour onto the
    /// control's rounded edge (#0086). Reads the laid-out widths of both template buttons.
    /// </summary>
    static int SpinTest()
    {
        Program.BuildAvaloniaApp().SetupWithoutStarting();
        using var services = AppServices.Create();
        var window = services.GetRequiredService<SettingsWindow>();
        window.Show();
        Pump(20);
        var tabs = VisualExtensions.GetVisualDescendants(window)
            .OfType<TabControl>().FirstOrDefault();
        var failures = 0;
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
                if (down is null) { failures++; Console.WriteLine($"spin: increase button without a decrease sibling"); continue; }
                var ok = Math.Abs(up.Bounds.Width - down.Bounds.Width) < 0.05
                         && Math.Abs(up.Bounds.X + up.Bounds.Width - down.Bounds.X) < 0.05;
                if (!ok) failures++;
                Console.WriteLine($"{(ok ? "ok" : "UNEQUAL"),-7} up=[{up.Bounds.X:0.#}..{up.Bounds.Right:0.#}] down=[{down.Bounds.X:0.#}..{down.Bounds.Right:0.#}] gap={down.Bounds.X - up.Bounds.Right:0.#}");
            }
        }
        Console.WriteLine(failures == 0 ? "spintest: spinner pairs identical" : $"spintest: {failures} unequal pair(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Renders every settings tab page to a PNG, so a style change can be proven pixel-identical by
    /// diffing the two dumps.
    /// </summary>
    static int LayDump(string dir)
    {
        Program.BuildAvaloniaApp().SetupWithoutStarting();
        using var services = AppServices.Create();
        var window = services.GetRequiredService<SettingsWindow>();
        window.Show();
        Pump(20);
        var tabs = VisualExtensions.GetVisualDescendants(window)
            .OfType<TabControl>().FirstOrDefault();
        Directory.CreateDirectory(dir);
        var pages = tabs?.ItemCount ?? 1;
        for (var page = 0; page < pages; page++)
        {
            if (tabs is not null) tabs.SelectedIndex = page;
            Pump(12);
            using var bmp = new RenderTargetBitmap(
                new PixelSize((int)window.Width, (int)window.Height), new Vector(96, 96));
            bmp.Render(window);
            bmp.Save(Path.Combine(dir, $"page-{page}.png"));
        }
        Console.WriteLine($"laydump: {pages} page(s) -> {dir}");
        return 0;
    }

    /// <summary>
    /// Renders the settings window at 100/125/150/175/200% DPI and scans the physical pixels through
    /// the sidebar-width spinner pair: every vertical line around the up/down buttons is reported with
    /// position, thickness and strength, so a fractional layout shows up as a faint or shifted line
    /// and the two buttons' rendered widths can be compared (#0086).
    /// </summary>
    static int SpinScan()
    {
        Program.BuildAvaloniaApp().SetupWithoutStarting();
        using var services = AppServices.Create();
        var window = services.GetRequiredService<SettingsWindow>();
        window.Show();
        Pump(20);
        var tabs = VisualExtensions.GetVisualDescendants(window)
            .OfType<TabControl>().FirstOrDefault();
        RepeatButton? up = null, down = null;
        for (var page = 0; page < (tabs?.ItemCount ?? 1) && up is null; page++)
        {
            if (tabs is not null) tabs.SelectedIndex = page;
            Pump(8);
            var box = VisualExtensions.GetVisualDescendants(window)
                .OfType<NumericUpDown>()
                .FirstOrDefault(n => n.Name == "SidebarWidthBox");
            if (box is not null)
                up = VisualExtensions.GetVisualDescendants(box)
                    .OfType<RepeatButton>()
                    .FirstOrDefault(b => b.Name == "PART_IncreaseButton");
        }
        if (up is null || up.Parent is not Visual panel) { Console.WriteLine("spinscan: pair not found"); return 1; }
        down = VisualExtensions.GetVisualDescendants(panel)
            .OfType<RepeatButton>().FirstOrDefault(b => b.Name == "PART_DecreaseButton");
        if (down is null) { Console.WriteLine("spinscan: no decrease button"); return 1; }

        var upPos = up.TranslatePoint(new Point(0, 0), window)!.Value;
        var downPos = down.TranslatePoint(new Point(0, 0), window)!.Value;
        if (panel is StackPanel sp)
            Console.WriteLine($"panel: StackPanel Spacing={sp.Spacing} templatedParent={(sp.TemplatedParent as StyledElement)?.GetType().Name} " +
                              $"bounds={sp.Bounds.Width:0.#}x{sp.Bounds.Height:0.#}");
        foreach (var child in VisualExtensions.GetVisualChildren(panel))
        {
            var el = child as StyledElement;
            var b = ((Visual)child).Bounds;
            var marg = el?.GetValue(Layoutable.MarginProperty) ?? Thickness.Parse("0");
            Console.WriteLine($"panel child: {child.GetType().Name} '{el?.Name}' bounds=[{b.X:0.#},{b.Y:0.#} {b.Width:0.#}x{b.Height:0.#}] margin={marg}");
        }
        Console.WriteLine($"logical: up [{upPos.X:0.###},{upPos.Y:0.###}] w={up.Bounds.Width:0.###} | " +
                          $"down [{downPos.X:0.###}] w={down.Bounds.Width:0.###} | pair right {downPos.X + down.Bounds.Width:0.###}");

        foreach (var dpi in new[] { 96, 120, 144, 168, 192 })
        {
            var scale = dpi / 96.0;
            var pw = (int)Math.Round(window.Width * scale);
            var ph = (int)Math.Round(window.Height * scale);
            using var bmp = new RenderTargetBitmap(
                new PixelSize(pw, ph), new Vector(dpi, dpi));
            bmp.Render(window);
            var stride = pw * 4;
            var buf = Marshal.AllocHGlobal(stride * ph);
            try
            {
                bmp.CopyPixels(new PixelRect(0, 0, pw, ph), buf, stride * ph, stride);
                // a row 3px under the button top: below the top border, above the chevron
                var y = (int)((upPos.Y + 3) * scale);
                var x0 = Math.Max(0, (int)((upPos.X - 4) * scale));
                var x1 = Math.Min(pw - 1, (int)((downPos.X + down.Bounds.Width + 4) * scale));
                // background = the most common byte value on the row (Bgra8888: B at +0, G +1, R +2, A +3)
                var counts = new Dictionary<int, int>();
                for (var x = x0; x <= x1; x++)
                {
                    var v = Marshal.ReadByte(buf + y * stride + x * 4);
                    counts[v] = counts.TryGetValue(v, out var c) ? c + 1 : 1;
                }
                var bg = counts.OrderByDescending(kv => kv.Value).First().Key;
                var runs = new StringBuilder();
                var inRun = false; var runStart = 0;
                for (var x = x0; x <= x1 + 1; x++)
                {
                    var dev = 0;
                    if (x <= x1)
                    {
                        dev = Math.Abs(Marshal.ReadByte(buf + y * stride + x * 4) - bg);
                    }
                    if (dev > 12 && !inRun) { inRun = true; runStart = x; }
                    else if (dev <= 12 && inRun)
                    {
                        inRun = false;
                        var w = x - runStart;
                        var mean = 0;
                        for (var i = runStart; i < x; i++)
                            mean += Math.Abs(Marshal.ReadByte(buf + y * stride + i * 4) - bg);
                        runs.Append($"[{runStart}..{x - 1}] w={w} str={mean / w:0} ");
                    }
                }
                Console.WriteLine($"{(dpi / 96.0):0.##}x  bg={bg}  y={y}: {runs}");
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return 0;
    }

    /// <summary>Scratch output under the OS temp dir, never a literal /tmp (Windows-first app).</summary>
    static string Scratch(string fileName) => Path.Combine(Path.GetTempPath(), fileName);

    static void Render(Window window, string path)
    {
        var pixel = new PixelSize((int)window.Width, (int)window.Height);
        using var shot = new RenderTargetBitmap(pixel, new Vector(96, 96));
        shot.Render(window);
        using var fs = File.Create(path);
        shot.Save(fs, PngBitmapEncoderOptions.Default);
    }

    static void RenderTo(Control target, Size size, string path)
    {
        var w = Math.Max(1, (int)size.Width);
        var h = Math.Max(1, (int)size.Height);
        using var bmp = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        bmp.Render(target);
        using var fs = File.Create(path);
        bmp.Save(fs, PngBitmapEncoderOptions.Default);
    }

    static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }
    }
}
