using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WirelessScanner.Presentation;

/// <summary>Grouped network explorer (DR-0010). All behaviour lives in <see cref="NetworkExplorerViewModel"/>.</summary>
public partial class NetworkExplorerView : UserControl
{
    public NetworkExplorerView()
    {
        InitializeComponent();
    }

    private NetworkExplorerViewModel? Explorer => (DataContext as MainWindowViewModel)?.Explorer;

    /// <summary>Unit row menu (DR-0013): rename, back to automatic grouping.</summary>
    private void UnitRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: UnitGroupItem unit, ContextMenu: { } menu } || Explorer is not { } ex) { e.Handled = true; return; }
        menu.Items.Clear();
        bool editable = ex.CanEditUnits;
        menu.Items.Add(Item("Rename access point…", editable, () =>
        {
            var name = PromptName(unit.Name);
            if (name != null) ex.RenameUnit(unit.Id, name);
        }));
        menu.Items.Add(Item("Reset to automatic grouping", editable, () => ex.ResetUnit(unit.Id)));
        if (!editable) menu.Items.Add(Item("(locked while a test runs)", false, () => { }));
    }

    /// <summary>Member row menu (DR-0013): split into its own access point, or move to another one.</summary>
    private void UnitMember_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NetworkApItem ap, ContextMenu: { } menu } || Explorer is not { } ex) { e.Handled = true; return; }
        menu.Items.Clear();
        bool editable = ex.CanEditUnits;
        menu.Items.Add(Item("This is a separate device (split out)", editable, () => ex.SplitAp(ap.Bssid)));
        var move = new MenuItem { Header = "Belongs to access point", IsEnabled = editable };
        foreach (var (id, name) in ex.MoveTargets(ap.Bssid)) move.Items.Add(Item(name, editable, () => ex.MoveAp(ap.Bssid, id)));
        if (move.Items.Count == 0) move.IsEnabled = false;
        menu.Items.Add(move);
    }

    private static MenuItem Item(string header, bool enabled, Action action)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        mi.Click += (_, _) => action();
        return mi;
    }

    /// <summary>Small modal name prompt (null = cancelled; empty = back to the automatic name).</summary>
    private string? PromptName(string current)
    {
        var box = new TextBox { Text = current, Margin = new Thickness(0, 6, 0, 10), MinWidth = 260 };
        var ok = new Button { Content = "Save", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Name this access point (leave empty for the automatic name):", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dlg = new Window
        {
            Title = "Rename access point", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), ShowInTaskbar = false
        };
        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dlg.ShowDialog() == true ? box.Text : null;
    }
}

/// <summary>bool -> !bool (enable controls while the selection is not locked).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : false;
}
