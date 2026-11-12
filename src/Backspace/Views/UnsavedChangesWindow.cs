using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Backspace.Views;

/// <summary>Asked when an editor with unsaved changes is closed (U206,
/// owner, 12 November 2026: "Save your changes?" with Save, Don't save
/// and Keep editing): closing used to discard them silently.</summary>
internal sealed class UnsavedChangesWindow : Window
{
    internal enum Choice { KeepEditing, Save, Discard }
    private Choice _choice = Choice.KeepEditing;

    internal UnsavedChangesWindow(string what)
    {
        Title = "Backspace";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("BS.Brush.BgBase");

        var heading = new TextBlock { Name = "Heading", Text = "Save your changes?", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        heading.Classes.Add("card-title");
        var text = new TextBlock { Text = $"You changed {what}. Closing without saving discards these changes.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        text.Classes.Add("body");

        Button Make(string name, string label, string style, Choice choice)
        {
            var button = new Button { Name = name, Content = label };
            button.Classes.Add(style);
            button.Click += (_, _) => { _choice = choice; Close(); };
            return button;
        }
        var keep = Make("KeepEditingButton", "Keep editing", "ghost", Choice.KeepEditing);
        keep.IsCancel = true;
        var discard = Make("DiscardButton", "Don't save", "ghost", Choice.Discard);
        var save = Make("SaveButton", "Save", "primary", Choice.Save);
        save.IsDefault = true;

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(34, 32, 34, 28),
            Spacing = 18,
            Children =
            {
                new StackPanel { Spacing = 8, Children = { heading, text } },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { keep, discard, save } },
            },
        };
    }

    internal async Task<Choice> AskAsync(Window owner)
    {
        await ShowDialog(owner);
        return _choice;
    }

    /// <summary>Hooks an editor window: closing it other than through its
    /// own Save asks first while <paramref name="changed"/> says there are
    /// unsaved changes. Save runs <paramref name="save"/>, which closes
    /// the window itself when the save works.</summary>
    internal static void Guard(Window editor, string what, System.Func<bool> changed, System.Action save, System.Func<bool>? skip = null)
    {
        var confirmed = false;
        editor.Closing += async (_, e) =>
        {
            if (confirmed || Saved.TryGetValue(editor, out _) || skip?.Invoke() == true) return;
            // Quitting Backspace, or Windows shutting down, closes it without asking.
            if (Backspace.App.QuitRequested || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) return;
            bool dirty;
            try { dirty = changed(); } catch { dirty = false; }
            if (!dirty) return;
            e.Cancel = true;
            switch (await new UnsavedChangesWindow(what).AskAsync(editor))
            {
                case Choice.Save: save(); break;
                case Choice.Discard: confirmed = true; editor.Close(); break;
            }
        };
    }

    /// <summary>A Save that worked closes the editor with its result: no question then.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> Saved = new();
    internal static void MarkSaved(Window editor) => Saved.AddOrUpdate(editor, editor);
}
