using System;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Markdown.Avalonia;
using Hakata.Services;
using Hakata.ViewModels;

namespace Hakata.Views;

/// <summary>The update window: the found release's notes and the button that opens its page.</summary>
public partial class UpdaterWindow : Window
{
    /// <summary>XAML loader entry point; the app goes through <see cref="ShowFor"/>.</summary>
    public UpdaterWindow()
    {
        InitializeComponent();
        // the release notes are feed-controlled markdown: a clicked link goes through the same
        // validation as any external URL, so a non-http(s) scheme never reaches the OS handler
        Opened += (_, _) =>
        {
            var viewer = this.GetVisualDescendants().OfType<MarkdownScrollViewer>().FirstOrDefault();
            if (viewer?.Engine is IMarkdownEngine engine)
                engine.HyperlinkCommand = new GuardedHyperlinkCommand();
        };
    }

    internal UpdaterWindow(UpdateViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    void CloseButton_OnClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Opens the window for a found release; modal when there is an owner, otherwise standalone.</summary>
    internal static void ShowFor(Window? owner, UpdateResponse update, AppMetadata metadata)
    {
        var window = new UpdaterWindow(new UpdateViewModel(update, metadata));
        if (owner is not null) _ = window.ShowDialog(owner);
        else window.Show();
    }
}

/// <summary>A markdown hyperlink command that refuses anything LinkGuard would not fetch:
/// feed-controlled link text must not dispatch arbitrary schemes to the OS handler.</summary>
sealed class GuardedHyperlinkCommand : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter)
    {
        if (parameter is string url) _ = LinkGuard.OpenExternal(url);
    }
}
