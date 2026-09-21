using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>Collects WPF binding failures so a mistyped path in XAML fails the test instead of silently showing nothing.</summary>
internal sealed class BindingErrorListener : TraceListener
{
    public List<string> Errors { get; } = [];
    public override void Write(string? message) { }
    public override void WriteLine(string? message)
    {
        if (message is not null && message.Contains("BindingExpression")) Errors.Add(message);
    }
}

/// <summary>Drives the real MainWindow and its views the way a user would.</summary>
[Collection("UI")] // the WPF binding trace source is process-wide, so UI tests must not overlap
public class ViewTests
{
    [Fact]
    public void Random_starting_gear_travels_Paste_to_Roll_through_the_real_views()
    {
        Sta.Run(() =>
        {
            var listener = new BindingErrorListener();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

            using var temp = new TempDatabase();
            using var db = temp.Open();
            var dice = new FixedDice(9);
            // Shown off-screen: a Window only builds its visual tree (and applies templates) once it has a handle.
            var window = new MainWindow
            {
                DataContext = new MainViewModel(db, dice),
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            try
            {
            void Layout()
            {
                // Let queued work (command re-query, bindings) run as the real message loop would.
                window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, () => { });
                window.UpdateLayout();
            }
            Layout();

            // 1. create a collection
            Find<TextBox>(window, t => t.Name == "NewCollectionBox").Text = "Solo Play";
            Click(window, "New collection");
            Layout();
            Assert.Equal("Solo Play", ((MainViewModel)window.DataContext).SelectedCollection?.Name);

            // 2. open Paste Table, paste, Interpret
            Click(window, "Paste Table…");
            Layout();
            Assert.IsType<PasteViewModel>(((MainViewModel)window.DataContext).Current);
            Find<TextBox>(window, t => t.AcceptsReturn).Text = Fixtures.RandomStartingGear;
            Click(window, "Interpret");
            Layout();

            // 3. Review: source and interpretation on one screen
            Assert.IsType<ReviewViewModel>(((MainViewModel)window.DataContext).Current);
            var source = Find<TextBox>(window, t => t.Name == "SourceBox");
            Assert.Equal(Fixtures.RandomStartingGear, source.Text.Replace("\r\n", "\n"));
            Assert.True(source.IsReadOnly);
            Assert.Equal("Random Starting Gear", Find<TextBox>(window, t => t.Name == "NameBox").Text);
            Assert.Equal("d10", Find<TextBox>(window, t => t.Name == "DiceBox").Text);

            var rows = Find<ItemsControl>(window, i => i.Name == "RowsList");
            Assert.Equal(8, rows.Items.Count);
            var lastRow = (ContentPresenter)rows.ItemContainerGenerator.ContainerFromIndex(7);
            var lastRowBoxes = FindAll<TextBox>(lastRow).ToList();
            Assert.Equal("9-10", lastRowBoxes[0].Text);
            Assert.Equal("D20 Construction Supplies", lastRowBoxes[1].Text);
            var note = FindAll<TextBlock>(lastRow).Single(t => t.Name == "NotesText");
            Assert.Equal(Visibility.Visible, note.Visibility);           // parser issue shown on the affected row
            Assert.Contains("Supplies", note.Text);
            var firstRow = (ContentPresenter)rows.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.Equal(Visibility.Collapsed, FindAll<TextBlock>(firstRow).Single(t => t.Name == "NotesText").Visibility);

            // Focusing a row highlights its lines in the original text.
            lastRowBoxes[1].Focus();
            ((ReviewViewModel)((MainViewModel)window.DataContext).Current!).SelectedRow =
                (EntryRowViewModel)lastRow.Content;
            Layout();
            Assert.Equal("9-10 D20 Construction\r\nSupplies".Replace("\r\n", "\n"), source.SelectedText.Replace("\r\n", "\n"));

            // Edit through the controls: rename, and fix a row's text.
            Find<TextBox>(window, t => t.Name == "NameBox").Text = "Starter Gear";
            FindAll<TextBox>((ContentPresenter)rows.ItemContainerGenerator.ContainerFromIndex(0)).ToList()[1].Text = "Backpack (canvas)";
            Layout();

            // 4. Save
            Click(window, "Save Table");
            Layout();

            // 5. Roll view for the saved, reloaded table
            var main = (MainViewModel)window.DataContext;
            Assert.IsType<RollViewModel>(main.Current);
            Assert.Equal("Starter Gear", Assert.Single(main.Tables).Name);
            Click(window, "Roll");
            Layout();
            Assert.Contains(FindAll<TextBlock>(window), t => t.Text == "Rolled 9");
            Assert.Contains(FindAll<TextBlock>(window), t => t.Text == "D20 Construction Supplies" && t.FontSize == 26);

            // 6. manual legal roll
            Find<TextBox>(window, t => t.Name == "ManualBox").Text = "1";
            Click(window, "Resolve");
            Layout();
            Assert.Contains(FindAll<TextBlock>(window), t => t.Text == "Rolled 1");
            Assert.Contains(FindAll<TextBlock>(window), t => t.Text == "Backpack (canvas)" && t.FontSize == 26);

            // ...and an illegal one is refused visibly.
            Find<TextBox>(window, t => t.Name == "ManualBox").Text = "11";
            Click(window, "Resolve");
            Layout();
            Assert.Contains(FindAll<TextBlock>(window), t => t.Text == "Enter a whole number from 1 to 10.");

            Assert.Empty(listener.Errors);
            }
            finally { window.Close(); }
        });
    }

    // ---- helpers ----------------------------------------------------------------------------

    internal static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static T Find<T>(DependencyObject root, Func<T, bool> where) where T : DependencyObject =>
        FindAll<T>(root).Single(where);

    /// <summary>Invokes a button through UI Automation, the same path as a click.</summary>
    private static void Click(DependencyObject root, string content)
    {
        var button = Find<Button>(root, b => b.Content as string == content);
        button.Dispatcher.Invoke(DispatcherPriority.ContextIdle, () => { }); // a real user's click follows a message-loop turn
        Assert.True(button.IsEnabled, $"'{content}' should be enabled");
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
}

internal static class Sta
{
    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

