using System.Windows;
using WirelessScanner.Domain;

namespace WirelessScanner.Presentation;

public partial class SessionCaptureDialog : Window
{
    public string SessionName => TxtSessionName.Text;
    public string Facility => TxtFacility.Text;
    public string Scope => TxtScope.Text;
    public string SurveyPoint => TxtSurveyPoint.Text;
    public string Notes => TxtNotes.Text;

    public CaptureSessionMode Mode => RbStationary.IsChecked == true ? CaptureSessionMode.Stationary : CaptureSessionMode.Roaming;

    public SessionCaptureDialog()
    {
        InitializeComponent();
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
