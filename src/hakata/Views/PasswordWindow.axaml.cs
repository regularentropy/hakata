using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Hakata.Infrastructure;

namespace Hakata.Views;

/// <summary>The history-password dialog. Unlock mode has one field and a wrong password keeps the
/// dialog open with an error; set mode needs the password twice and confirms only when they match.</summary>
public partial class PasswordWindow : Window
{
    readonly bool setMode;
    readonly Func<string, bool>? tryUnlock;
    internal readonly TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    PasswordWindow(bool setMode, Func<string, bool>? tryUnlock)
    {
        this.setMode = setMode;
        this.tryUnlock = tryUnlock;
        InitializeComponent();
        ConfirmBox.IsVisible = setMode;
        TitleText.Text = setMode ? "Set a history password" : "Unlock history";
        HintText.Text = setMode
            ? "The password wraps the history key — the stored translations are not re-encrypted, and the folder can be moved to another machine and unlocked there. Forgetting the password means losing the history."
            : "This history is password-protected. Enter the password to open it.";
        OkButton.Content = setMode ? "Set password" : "Unlock";
        PasswordBox.TextChanged += (_, _) => Validate();
        ConfirmBox.TextChanged += (_, _) => Validate();
        Opened += (_, _) => PasswordBox.Focus();
        Validate();
    }

    void Validate()
    {
        OkButton.IsEnabled = PasswordBox.Text is { Length: > 0 }
            && (!setMode || ConfirmBox.Text == PasswordBox.Text);
        ErrorText.IsVisible = false;
    }

    void OnOk(object? sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Text ?? "";
        if (setMode)
        {
            completion.TrySetResult(password);
            Close();
            return;
        }
        if (tryUnlock is not null && !tryUnlock(password))
        {
            ErrorText.Text = "Wrong password";
            ErrorText.IsVisible = true;
            return;
        }
        completion.TrySetResult(password);
        Close();
    }

    void OnCancel(object? sender, RoutedEventArgs e) => ModalUi.Cancel(this, completion);

    /// <summary>Unlock mode: the dialog stays open on a wrong password; null when cancelled.</summary>
    public static Task<string?> PromptUnlockAsync(Window? owner, Func<string, bool> tryUnlock)
    {
        var window = new PasswordWindow(setMode: false, tryUnlock);
        return ModalUi.ShowModal(window, window.completion, owner);
    }

    /// <summary>Set mode: returns the new password, or null when cancelled.</summary>
    public static Task<string?> PromptSetAsync(Window? owner)
    {
        var window = new PasswordWindow(setMode: true, null);
        return ModalUi.ShowModal(window, window.completion, owner);
    }
}
