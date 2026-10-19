using System;
using System.Globalization;
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
}

/// <summary>bool -> !bool (enable controls while the selection is not locked).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : false;
}
